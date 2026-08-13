using System.Buffers.Binary;
using System.Text;

namespace Dmart.DataAdapters.Parquet;

// Minimal Parquet writer.
//
// Scope on purpose: PLAIN-encoded values, v1 data pages, one page per column
// chunk, no dictionary, no statistics. That is the smallest thing that can be a
// VALID Parquet file, which is the only claim worth proving before the
// remaining pieces (compression, the reader) are added.
//
// File layout:
//   "PAR1"                 magic
//   <row group>            per column: page header + page payload
//   <row group>            ...
//   <FileMetaData>         Thrift compact footer
//   <uint32 footer length> little-endian
//   "PAR1"                 magic
//
// Everything a reader needs to find the data lives in the footer, which is why
// the byte offsets recorded there have to be captured as the pages are written
// rather than computed afterwards.
//
// Row groups are the unit of writer memory: a caller streams one at a time and
// only that group's columns need to be resident, which is what makes a
// multi-GB export bounded. See docs/parquet-export-design.md §4.2.
internal sealed class ParquetFileWriter
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    // Optional columns carry definition levels; required ones do not. This is
    // a property of the SCHEMA, not of whether a particular batch happens to
    // contain nulls — a reader decides how to parse the page from the schema,
    // so an optional column with no nulls still needs its levels section.
    internal sealed record ColumnSpec(
        string Name, ParquetType Type, ConvertedType? Converted, bool Optional = false);

    // What the footer must record about a column chunk once its pages are out.
    private sealed record ChunkResult(
        ColumnSpec Spec, long DataPageOffset, long CompressedSize, long UncompressedSize, int NumValues);

    private sealed record RowGroupResult(List<ChunkResult> Chunks, int RowCount);

    private readonly List<ColumnSpec> _columns;
    private readonly List<RowGroupResult> _rowGroups = [];
    private CountingStream? _out;
    private long _totalRows;
    private bool _finished;

    public ParquetFileWriter(IReadOnlyList<ColumnSpec> columns) => _columns = [.. columns];

    /// <summary>
    /// One column's page content: PLAIN-encoded values for the rows that have
    /// them, plus a definition level per row when the column is optional.
    /// </summary>
    /// <remarks>
    /// <paramref name="DefinitionLevels"/> must have one entry per ROW, while
    /// <paramref name="PlainValues"/> holds only the PRESENT values. Conflating
    /// the two is the mistake that produces a file which decodes cleanly with
    /// every value shifted into the wrong row.
    /// </remarks>
    internal sealed record ColumnPage(byte[] PlainValues, IReadOnlyList<int>? DefinitionLevels);

    // ---- streaming form ----

    /// <summary>
    /// Begins a file on <paramref name="output"/>. The destination need not be
    /// seekable; offsets are tracked by counting bytes.
    /// </summary>
    public void Start(Stream output)
    {
        if (_out is not null) throw new InvalidOperationException("already started");
        _out = new CountingStream(output);
        _out.Write(Magic);
    }

    /// <summary>
    /// Writes one row group: one already-PLAIN-encoded page per column, in
    /// schema order. Row groups may differ in size — the last one usually does.
    /// </summary>
    public void WriteRowGroup(IReadOnlyList<ColumnPage> columnValues, int rowCount)
    {
        var o = _out ?? throw new InvalidOperationException("Start must be called first");
        if (_finished) throw new InvalidOperationException("the file is already finished");
        if (columnValues.Count != _columns.Count)
            throw new ArgumentException("one payload per column is required", nameof(columnValues));
        if (rowCount <= 0)
            // An empty row group is legal in the format but pointless here, and
            // silently writing one hides a caller bug (an exhausted pager that
            // asked for another group). Row counts come from a query page.
            throw new ArgumentOutOfRangeException(nameof(rowCount), "a row group must have rows");

        var chunks = new List<ChunkResult>(_columns.Count);

        for (var i = 0; i < _columns.Count; i++)
        {
            var col = _columns[i];
            var page = columnValues[i];

            if (col.Optional != (page.DefinitionLevels is not null))
                throw new ArgumentException(
                    $"column '{col.Name}' is {(col.Optional ? "optional" : "required")} but was given "
                    + $"{(page.DefinitionLevels is null ? "no" : "")} definition levels", nameof(columnValues));

            // v1 page body = [definition levels][values]. The levels section is
            // RLE with its own 4-byte length prefix; max level is 1 for a flat
            // optional column, so one bit is enough to express it.
            byte[] body;
            if (page.DefinitionLevels is { } levels)
            {
                if (levels.Count != rowCount)
                    throw new ArgumentException(
                        $"column '{col.Name}': {levels.Count} definition levels for {rowCount} rows",
                        nameof(columnValues));
                var encoded = RleEncoder.EncodeLevelsWithLengthPrefix(levels, bitWidth: 1);
                body = new byte[encoded.Length + page.PlainValues.Length];
                encoded.CopyTo(body, 0);
                page.PlainValues.CopyTo(body, encoded.Length);
            }
            else
            {
                body = page.PlainValues;
            }

            // Absolute file offset, counted rather than seeked. Recording it
            // relative to the row group would produce a file whose first group
            // reads correctly and whose every later group reads garbage.
            var pageStart = o.BytesWritten;

            // Page header, then the page body. Uncompressed for now, so the
            // two sizes are equal — kept as separate fields because they stop
            // being equal the moment compression is switched on.
            //
            // num_values counts ROWS, not stored values: for an optional column
            // the nulls are counted here but absent from the values section.
            WriteDataPageHeader(o, rowCount, body.Length, body.Length);
            o.Write(body);

            chunks.Add(new ChunkResult(col, pageStart, o.BytesWritten - pageStart,
                                       o.BytesWritten - pageStart, rowCount));
        }

        _rowGroups.Add(new RowGroupResult(chunks, rowCount));
        _totalRows += rowCount;
    }

    /// <summary>Writes the footer. Nothing may be written after this.</summary>
    public void Finish()
    {
        var o = _out ?? throw new InvalidOperationException("Start must be called first");
        if (_finished) throw new InvalidOperationException("already finished");
        _finished = true;

        var footerStart = o.BytesWritten;
        WriteFileMetaData(o);
        var footerLength = (int)(o.BytesWritten - footerStart);

        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)footerLength);
        o.Write(len);
        o.Write(Magic);
    }

    // ---- one-shot convenience ----

    /// <summary>
    /// Writes a complete single-row-group file. Convenience over
    /// Start/WriteRowGroup/Finish for callers whose data already fits in memory
    /// — tests, and small exports.
    /// </summary>
    public void Write(Stream output, IReadOnlyList<byte[]> columnValues, int rowCount)
        => Write(output, [.. columnValues.Select(v => new ColumnPage(v, null))], rowCount);

    public void Write(Stream output, IReadOnlyList<ColumnPage> columnValues, int rowCount)
    {
        Start(output);
        WriteRowGroup(columnValues, rowCount);
        Finish();
    }

    // ---- metadata ----

    // struct PageHeader {
    //   1: PageType type, 2: i32 uncompressed_page_size,
    //   3: i32 compressed_page_size, 5: DataPageHeader data_page_header }
    private static void WriteDataPageHeader(Stream o, int numValues, int uncompressed, int compressed)
    {
        var t = new ThriftCompactWriter(o);
        t.StructBegin();
        t.WriteI32Field(1, (int)PageType.DataPage);
        t.WriteI32Field(2, uncompressed);
        t.WriteI32Field(3, compressed);

        // struct DataPageHeader {
        //   1: i32 num_values, 2: Encoding encoding,
        //   3: Encoding definition_level_encoding,
        //   4: Encoding repetition_level_encoding }
        t.WriteStructFieldBegin(5);
        t.WriteI32Field(1, numValues);
        t.WriteI32Field(2, (int)ParquetEncoding.Plain);
        t.WriteI32Field(3, (int)ParquetEncoding.Rle);
        t.WriteI32Field(4, (int)ParquetEncoding.Rle);
        t.StructEnd();

        t.StructEnd();
    }

    private void WriteFileMetaData(Stream o)
    {
        var t = new ThriftCompactWriter(o);
        t.StructBegin();

        t.WriteI32Field(1, 1);                       // version

        // 2: list<SchemaElement>. Element 0 is the root, whose num_children
        // counts the real columns; readers rely on that to walk the tree.
        t.WriteListFieldBegin(2, ThriftCompactWriter.ElemStruct, _columns.Count + 1);
        t.StructBegin();
        t.WriteStringField(4, "dmart_schema");
        t.WriteI32Field(5, _columns.Count);          // num_children
        t.StructEnd();
        foreach (var c in _columns)
        {
            t.StructBegin();
            t.WriteI32Field(1, (int)c.Type);                          // type
            t.WriteI32Field(3, (int)(c.Optional                        // repetition_type
                ? FieldRepetitionType.Optional
                : FieldRepetitionType.Required));
            t.WriteStringField(4, c.Name);                            // name
            if (c.Converted is { } cv) t.WriteI32Field(6, (int)cv);   // converted_type
            t.StructEnd();
        }

        // num_rows is the total across every row group. A reader trusting this
        // over the per-group counts would stop early if it disagreed.
        t.WriteI64Field(3, _totalRows);

        // 4: list<RowGroup>
        t.WriteListFieldBegin(4, ThriftCompactWriter.ElemStruct, _rowGroups.Count);
        foreach (var rg in _rowGroups)
        {
            t.StructBegin();

            // 1: list<ColumnChunk>
            t.WriteListFieldBegin(1, ThriftCompactWriter.ElemStruct, rg.Chunks.Count);
            foreach (var ch in rg.Chunks)
            {
                t.StructBegin();
                t.WriteI64Field(2, ch.DataPageOffset);   // file_offset

                // 3: ColumnMetaData
                t.WriteStructFieldBegin(3);
                t.WriteI32Field(1, (int)ch.Spec.Type);   // type
                t.WriteListFieldBegin(2, ThriftCompactWriter.ElemI32, 1);
                t.WriteListI32((int)ParquetEncoding.Plain);
                t.WriteListFieldBegin(3, ThriftCompactWriter.ElemBinary, 1);
                t.WriteListString(ch.Spec.Name);         // path_in_schema
                t.WriteI32Field(4, (int)CompressionCodec.Uncompressed);
                t.WriteI64Field(5, ch.NumValues);
                t.WriteI64Field(6, ch.UncompressedSize);
                t.WriteI64Field(7, ch.CompressedSize);
                t.WriteI64Field(9, ch.DataPageOffset);   // data_page_offset
                t.StructEnd();

                t.StructEnd();
            }

            t.WriteI64Field(2, rg.Chunks.Sum(c => c.CompressedSize));   // total_byte_size
            t.WriteI64Field(3, rg.RowCount);                            // num_rows
            t.StructEnd();
        }

        t.WriteStringField(6, "dmart");              // created_by
        t.StructEnd();
    }

    // ---- PLAIN value encoders ----

    /// <summary>PLAIN encoding for INT64: little-endian, fixed width, no framing.</summary>
    public static byte[] PlainInt64(IReadOnlyList<long> values)
    {
        var buf = new byte[values.Count * 8];
        for (var i = 0; i < values.Count; i++)
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(i * 8, 8), values[i]);
        return buf;
    }

    /// <summary>
    /// PLAIN encoding for BOOLEAN: bit-packed, LSB first, NOT one byte per
    /// value. Writing a byte each produces a file that decodes without error
    /// and returns eight times too many rows of nonsense.
    /// </summary>
    public static byte[] PlainBoolean(IReadOnlyList<bool> values)
    {
        var buf = new byte[(values.Count + 7) / 8];
        for (var i = 0; i < values.Count; i++)
            if (values[i]) buf[i / 8] |= (byte)(1 << (i % 8));
        return buf;
    }

    /// <summary>
    /// PLAIN encoding for INT64 timestamps, as microseconds since the Unix
    /// epoch — the unit ConvertedType.TimestampMicros declares. DateTime is
    /// normalised to UTC first: a local-kind value would otherwise be written
    /// as though its wall-clock reading were UTC, shifting every timestamp by
    /// the host's offset with nothing in the file to reveal it.
    /// </summary>
    public static byte[] PlainTimestampMicros(IReadOnlyList<DateTime> values)
    {
        var micros = new long[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var utc = values[i].Kind switch
            {
                DateTimeKind.Utc => values[i],
                DateTimeKind.Local => values[i].ToUniversalTime(),
                // Unspecified is what comes back from a timestamp-without-tz
                // column; dmart stores wall-clock local time there, so treat it
                // as local rather than silently relabelling it UTC.
                _ => DateTime.SpecifyKind(values[i], DateTimeKind.Local).ToUniversalTime(),
            };
            micros[i] = (utc - DateTime.UnixEpoch).Ticks / 10;   // 10 ticks per microsecond
        }
        return PlainInt64(micros);
    }

    /// <summary>PLAIN encoding for BYTE_ARRAY: 4-byte LE length, then bytes.</summary>
    public static byte[] PlainByteArray(IReadOnlyList<string> values)
    {
        using var ms = new MemoryStream();
        Span<byte> len = stackalloc byte[4];
        foreach (var v in values)
        {
            var bytes = Encoding.UTF8.GetBytes(v);
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)bytes.Length);
            ms.Write(len);
            ms.Write(bytes);
        }
        return ms.ToArray();
    }
}
