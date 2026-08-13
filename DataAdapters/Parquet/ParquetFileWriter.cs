using System.Buffers.Binary;
using System.Text;

namespace Dmart.DataAdapters.Parquet;

// Minimal Parquet writer — the walking skeleton.
//
// Scope on purpose: one row group, PLAIN-encoded values, v1 data pages, no
// dictionary, no statistics, and (for now) a single required INT64 column.
// That is the smallest thing that can be a VALID Parquet file, which is the
// only claim worth proving before the remaining column types are added.
//
// File layout:
//   "PAR1"                 magic
//   <column chunk data>    page header + page payload, per column
//   <FileMetaData>         Thrift compact footer
//   <uint32 footer length> little-endian
//   "PAR1"                 magic
//
// Everything a reader needs to find the data lives in the footer, which is why
// the byte offsets recorded there have to be captured as the pages are
// written rather than computed afterwards.
internal sealed class ParquetFileWriter
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    internal sealed record ColumnSpec(string Name, ParquetType Type, ConvertedType? Converted);

    // What the footer must record about a column chunk once its pages are out.
    private sealed record ChunkResult(
        ColumnSpec Spec, long DataPageOffset, long CompressedSize, long UncompressedSize, int NumValues);

    private readonly List<ColumnSpec> _columns;
    public ParquetFileWriter(IReadOnlyList<ColumnSpec> columns) => _columns = [.. columns];

    /// <summary>
    /// Writes a single-row-group file. <paramref name="columnValues"/> supplies
    /// one already-PLAIN-encoded payload per column, in schema order.
    /// </summary>
    public void Write(Stream output, IReadOnlyList<byte[]> columnValues, int rowCount)
    {
        if (columnValues.Count != _columns.Count)
            throw new ArgumentException("one payload per column is required", nameof(columnValues));

        output.Write(Magic);
        var chunks = new List<ChunkResult>(_columns.Count);

        for (var i = 0; i < _columns.Count; i++)
        {
            var payload = columnValues[i];
            var pageStart = output.Position;

            // Page header, then the page body. Uncompressed for now, so the
            // two sizes are equal — kept as separate fields because they stop
            // being equal the moment zstd is switched on.
            WriteDataPageHeader(output, rowCount, payload.Length, payload.Length);
            output.Write(payload);

            chunks.Add(new ChunkResult(_columns[i], pageStart, output.Position - pageStart,
                                       output.Position - pageStart, rowCount));
        }

        var footerStart = output.Position;
        WriteFileMetaData(output, chunks, rowCount);
        var footerLength = (int)(output.Position - footerStart);

        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)footerLength);
        output.Write(len);
        output.Write(Magic);
    }

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

    private void WriteFileMetaData(Stream o, List<ChunkResult> chunks, int rowCount)
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
            t.WriteI32Field(3, (int)FieldRepetitionType.Required);    // repetition_type
            t.WriteStringField(4, c.Name);                            // name
            if (c.Converted is { } cv) t.WriteI32Field(6, (int)cv);   // converted_type
            t.StructEnd();
        }

        t.WriteI64Field(3, rowCount);                // num_rows

        // 4: list<RowGroup> — exactly one for now.
        t.WriteListFieldBegin(4, ThriftCompactWriter.ElemStruct, 1);
        t.StructBegin();

        // 1: list<ColumnChunk>
        t.WriteListFieldBegin(1, ThriftCompactWriter.ElemStruct, chunks.Count);
        foreach (var ch in chunks)
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

        t.WriteI64Field(2, chunks.Sum(c => c.CompressedSize));   // total_byte_size
        t.WriteI64Field(3, rowCount);                            // num_rows
        t.StructEnd();

        t.WriteStringField(6, "dmart");              // created_by
        t.StructEnd();
    }

    /// <summary>PLAIN encoding for INT64: little-endian, fixed width, no framing.</summary>
    public static byte[] PlainInt64(IReadOnlyList<long> values)
    {
        var buf = new byte[values.Count * 8];
        for (var i = 0; i < values.Count; i++)
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(i * 8, 8), values[i]);
        return buf;
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
