using System.Buffers.Binary;
using System.Text;

namespace Dmart.DataAdapters.Parquet;

// Reads back what ParquetFileWriter produces, so an export can be restored.
//
// SCOPE, stated plainly because the failure mode of getting this wrong is a
// silent misread: this reads the PROFILE we write — flat schemas, v1 data
// pages, PLAIN values, RLE/bit-packed definition levels, uncompressed or zstd.
// It is not a general Parquet reader. Dictionary encoding, v2 pages, nested
// types, and other codecs are all common in the wild and all REJECTED with an
// exception naming what was found.
//
// That refusal is the design. A reader that quietly ignored a dictionary page
// would return plausible-looking wrong values, and this is a restore path — the
// place where wrong data is least likely to be noticed and most expensive.
//
// Two things it accepts that we never emit, because a reader that could only
// read its own writer's output would make cross-verification meaningless:
// bit-packed level runs, and multiple data pages per column chunk.
internal static class ParquetFileReader
{
    private static ReadOnlySpan<byte> Magic => "PAR1"u8;

    /// <summary>One column's values, with nulls in their original row positions.</summary>
    /// <remarks>
    /// Exactly one of the arrays is populated, chosen by the column's physical
    /// type. Nullable elements are used even for required columns so a caller
    /// need not branch on the schema to read a value.
    /// </remarks>
    internal sealed record ParquetColumn(
        ParquetFileWriter.ColumnSpec Spec,
        long?[]? Int64Values = null,
        string?[]? StringValues = null,
        bool?[]? BooleanValues = null)
    {
        /// <summary>
        /// Reinterprets an INT64 column annotated TIMESTAMP_MICROS as UTC
        /// instants — the inverse of ParquetFileWriter.PlainTimestampMicros.
        /// </summary>
        public DateTime?[] AsTimestamps()
        {
            if (Spec.Converted is not ConvertedType.TimestampMicros)
                throw new InvalidOperationException(
                    $"column '{Spec.Name}' is not annotated TIMESTAMP_MICROS");
            var raw = Int64Values ?? throw new InvalidOperationException(
                $"column '{Spec.Name}' holds no INT64 values");

            var result = new DateTime?[raw.Length];
            for (var i = 0; i < raw.Length; i++)
                result[i] = raw[i] is { } micros
                    ? DateTime.UnixEpoch.AddTicks(micros * 10)   // 10 ticks per microsecond
                    : null;
            return result;
        }
    }

    internal sealed record ParquetTable(long RowCount, IReadOnlyList<ParquetColumn> Columns)
    {
        public ParquetColumn Column(string name) =>
            Columns.FirstOrDefault(c => c.Spec.Name == name)
            ?? throw new KeyNotFoundException($"no column named '{name}'");
    }

    /// <summary>
    /// Reads an entire file. <paramref name="input"/> must be seekable: the
    /// footer is at the end and every chunk offset is absolute, which is what
    /// lets a reader project single columns without scanning.
    /// </summary>
    public static ParquetTable Read(Stream input)
    {
        if (!input.CanSeek)
            throw new ArgumentException("reading requires a seekable stream", nameof(input));

        var (schema, rowGroups, rowCount) = ReadFooter(input);

        // One accumulator per column, filled row group by row group in file
        // order. Appending out of order would reorder rows without any error.
        var accumulators = schema.Select(s => new ColumnAccumulator(s)).ToList();

        foreach (var rg in rowGroups)
        {
            foreach (var chunk in rg.Chunks)
            {
                var index = schema.FindIndex(s => s.Name == chunk.ColumnName);
                if (index < 0)
                    throw new InvalidDataException(
                        $"column chunk names '{chunk.ColumnName}', which is not in the schema");
                ReadChunk(input, chunk, schema[index], accumulators[index]);
            }
        }

        return new ParquetTable(rowCount, [.. accumulators.Select(a => a.ToColumn())]);
    }

    /// <summary>Convenience for the common case of restoring from a path.</summary>
    public static ParquetTable ReadFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    // ---- footer ----

    private sealed record ChunkInfo(
        string ColumnName, long DataPageOffset, long NumValues,
        CompressionCodec Codec, ParquetType Type);

    private sealed record RowGroupInfo(List<ChunkInfo> Chunks, long NumRows);

    private static (List<ParquetFileWriter.ColumnSpec> Schema, List<RowGroupInfo> RowGroups, long RowCount)
        ReadFooter(Stream input)
    {
        if (input.Length < 12)
            throw new InvalidDataException("file is too short to be Parquet");

        Span<byte> head = stackalloc byte[4];
        input.Position = 0;
        input.ReadExactly(head);
        if (!head.SequenceEqual(Magic))
            throw new InvalidDataException("missing leading PAR1 magic — not a Parquet file");

        Span<byte> tail = stackalloc byte[8];
        input.Position = input.Length - 8;
        input.ReadExactly(tail);
        if (!tail[4..].SequenceEqual(Magic))
            throw new InvalidDataException("missing trailing PAR1 magic — file is truncated or not Parquet");

        var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(tail);
        var footerStart = input.Length - 8 - (long)footerLength;
        if (footerStart < 4)
            throw new InvalidDataException($"footer length {footerLength} does not fit the file");

        input.Position = footerStart;
        var footer = new byte[footerLength];
        input.ReadExactly(footer);

        using var ms = new MemoryStream(footer, writable: false);
        return ReadFileMetaData(new ThriftCompactReader(ms));
    }

    private static (List<ParquetFileWriter.ColumnSpec>, List<RowGroupInfo>, long)
        ReadFileMetaData(ThriftCompactReader t)
    {
        List<ParquetFileWriter.ColumnSpec>? schema = null;
        List<RowGroupInfo> rowGroups = [];
        long numRows = 0;

        t.StructBegin();
        while (t.TryReadFieldHeader(out var id, out var type))
        {
            switch (id)
            {
                case 2: schema = ReadSchema(t); break;
                case 3: numRows = t.ReadI64(); break;
                case 4: rowGroups = ReadRowGroups(t); break;
                default: t.Skip(type); break;   // version, created_by, column orders, key-value metadata
            }
        }
        t.StructEnd();

        if (schema is null)
            throw new InvalidDataException("footer has no schema");
        return (schema, rowGroups, numRows);
    }

    // list<SchemaElement>. Element 0 is the root and is not a column; its
    // num_children counts the real ones.
    private static List<ParquetFileWriter.ColumnSpec> ReadSchema(ThriftCompactReader t)
    {
        var count = t.ReadListHeader(out _);
        var columns = new List<ParquetFileWriter.ColumnSpec>(Math.Max(0, count - 1));

        for (var i = 0; i < count; i++)
        {
            ParquetType? physical = null;
            FieldRepetitionType repetition = FieldRepetitionType.Required;
            string? name = null;
            int? converted = null;
            var numChildren = 0;

            t.StructBegin();
            while (t.TryReadFieldHeader(out var id, out var ft))
            {
                switch (id)
                {
                    case 1: physical = (ParquetType)t.ReadI32(); break;
                    case 3: repetition = (FieldRepetitionType)t.ReadI32(); break;
                    case 4: name = t.ReadString(); break;
                    case 5: numChildren = t.ReadI32(); break;
                    case 6: converted = t.ReadI32(); break;
                    default: t.Skip(ft); break;
                }
            }
            t.StructEnd();

            if (i == 0) continue;   // root

            if (numChildren > 0)
                throw new NotSupportedException(
                    $"column '{name}' is a nested group; only flat schemas are supported");
            if (repetition == FieldRepetitionType.Repeated)
                throw new NotSupportedException(
                    $"column '{name}' is REPEATED; repetition levels are not supported "
                    + "(see docs/parquet-export-design.md §4.2 — list columns are flattened on export)");
            if (physical is null || name is null)
                throw new InvalidDataException("schema element is missing its type or name");

            columns.Add(new ParquetFileWriter.ColumnSpec(
                name, physical.Value,
                converted is { } cv ? (ConvertedType)cv : null,
                repetition == FieldRepetitionType.Optional));
        }

        return columns;
    }

    private static List<RowGroupInfo> ReadRowGroups(ThriftCompactReader t)
    {
        var count = t.ReadListHeader(out _);
        var groups = new List<RowGroupInfo>(count);

        for (var i = 0; i < count; i++)
        {
            List<ChunkInfo> chunks = [];
            long numRows = 0;

            t.StructBegin();
            while (t.TryReadFieldHeader(out var id, out var ft))
            {
                switch (id)
                {
                    case 1: chunks = ReadColumnChunks(t); break;
                    case 3: numRows = t.ReadI64(); break;
                    default: t.Skip(ft); break;
                }
            }
            t.StructEnd();

            groups.Add(new RowGroupInfo(chunks, numRows));
        }

        return groups;
    }

    private static List<ChunkInfo> ReadColumnChunks(ThriftCompactReader t)
    {
        var count = t.ReadListHeader(out _);
        var chunks = new List<ChunkInfo>(count);

        for (var i = 0; i < count; i++)
        {
            ChunkInfo? meta = null;

            t.StructBegin();
            while (t.TryReadFieldHeader(out var id, out var ft))
            {
                if (id == 3) meta = ReadColumnMetaData(t);
                else t.Skip(ft);        // file_path, file_offset, indexes, crypto
            }
            t.StructEnd();

            chunks.Add(meta ?? throw new InvalidDataException("column chunk has no metadata"));
        }

        return chunks;
    }

    private static ChunkInfo ReadColumnMetaData(ThriftCompactReader t)
    {
        ParquetType type = default;
        string? path = null;
        var codec = CompressionCodec.Uncompressed;
        long numValues = 0, dataPageOffset = 0, dictionaryPageOffset = 0;

        t.StructBegin();
        while (t.TryReadFieldHeader(out var id, out var ft))
        {
            switch (id)
            {
                case 1: type = (ParquetType)t.ReadI32(); break;
                case 3:
                {
                    // path_in_schema: list<string>. Flat schema, so exactly one
                    // element; more than one means a nested column.
                    var n = t.ReadListHeader(out _);
                    for (var j = 0; j < n; j++)
                    {
                        var part = t.ReadString();
                        path ??= part;
                    }
                    if (n > 1)
                        throw new NotSupportedException(
                            $"column '{path}' is nested; only flat schemas are supported");
                    break;
                }
                case 4: codec = (CompressionCodec)t.ReadI32(); break;
                case 5: numValues = t.ReadI64(); break;
                case 9: dataPageOffset = t.ReadI64(); break;
                case 11: dictionaryPageOffset = t.ReadI64(); break;
                default: t.Skip(ft); break;
            }
        }
        t.StructEnd();

        if (path is null) throw new InvalidDataException("column chunk has no path_in_schema");

        // A dictionary page means the values are indexes, not values. Reading
        // on regardless would produce a column of small integers reinterpreted
        // as data.
        if (dictionaryPageOffset != 0)
            throw new NotSupportedException(
                $"column '{path}' uses a dictionary page; only PLAIN encoding is supported. "
                + "Write with use_dictionary=False.");

        if (codec is not (CompressionCodec.Uncompressed or CompressionCodec.Zstd))
            throw new NotSupportedException(
                $"column '{path}' uses compression codec {(int)codec}; "
                + "only UNCOMPRESSED and ZSTD are supported");

        return new ChunkInfo(path, dataPageOffset, numValues, codec, type);
    }

    // ---- pages ----

    private static void ReadChunk(
        Stream input, ChunkInfo chunk, ParquetFileWriter.ColumnSpec spec, ColumnAccumulator into)
    {
        input.Position = chunk.DataPageOffset;

        // A chunk may hold several data pages — we write one, but other writers
        // split at a byte threshold, so stopping after the first would silently
        // drop the tail of every large column.
        long seen = 0;
        while (seen < chunk.NumValues)
        {
            var header = ReadPageHeader(input, chunk.ColumnName);

            var payload = new byte[header.CompressedSize];
            input.ReadExactly(payload);

            var body = chunk.Codec == CompressionCodec.Zstd
                ? Decompress(payload, header.UncompressedSize)
                : payload;

            DecodePage(body, header.NumValues, spec, chunk.Type, into);
            seen += header.NumValues;
        }
    }

    private static byte[] Decompress(byte[] payload, int uncompressedSize)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        var result = decompressor.Unwrap(payload).ToArray();
        if (result.Length != uncompressedSize)
            throw new InvalidDataException(
                $"page decompressed to {result.Length} bytes, header declared {uncompressedSize}");
        return result;
    }

    private sealed record PageHeaderInfo(int NumValues, int UncompressedSize, int CompressedSize);

    private static PageHeaderInfo ReadPageHeader(Stream input, string columnName)
    {
        var t = new ThriftCompactReader(input);
        PageType pageType = default;
        int uncompressed = 0, compressed = 0, numValues = 0;
        var sawDataPage = false;

        t.StructBegin();
        while (t.TryReadFieldHeader(out var id, out var ft))
        {
            switch (id)
            {
                case 1: pageType = (PageType)t.ReadI32(); break;
                case 2: uncompressed = t.ReadI32(); break;
                case 3: compressed = t.ReadI32(); break;
                case 5:
                    sawDataPage = true;
                    numValues = ReadDataPageHeader(t, columnName);
                    break;
                case 8:
                    throw new NotSupportedException(
                        $"column '{columnName}' uses v2 data pages; only v1 is supported");
                default: t.Skip(ft); break;   // crc, index page header
            }
        }
        t.StructEnd();

        if (pageType == PageType.DictionaryPage)
            throw new NotSupportedException(
                $"column '{columnName}' has a dictionary page; only PLAIN encoding is supported");
        if (!sawDataPage)
            throw new InvalidDataException($"column '{columnName}': page is not a v1 data page");

        return new PageHeaderInfo(numValues, uncompressed, compressed);
    }

    private static int ReadDataPageHeader(ThriftCompactReader t, string columnName)
    {
        var numValues = 0;

        t.StructBegin();
        while (t.TryReadFieldHeader(out var id, out var ft))
        {
            switch (id)
            {
                case 1: numValues = t.ReadI32(); break;
                case 2:
                {
                    var encoding = t.ReadI32();
                    if (encoding != (int)ParquetEncoding.Plain)
                        throw new NotSupportedException(
                            $"column '{columnName}' uses encoding {encoding}; only PLAIN is supported");
                    break;
                }
                case 3:
                {
                    var levelEncoding = t.ReadI32();
                    if (levelEncoding != (int)ParquetEncoding.Rle)
                        throw new NotSupportedException(
                            $"column '{columnName}' encodes definition levels as {levelEncoding}; "
                            + "only RLE is supported");
                    break;
                }
                default: t.Skip(ft); break;   // repetition level encoding, statistics
            }
        }
        t.StructEnd();

        return numValues;
    }

    // ---- values ----

    private static void DecodePage(
        ReadOnlySpan<byte> body, int numValues,
        ParquetFileWriter.ColumnSpec spec, ParquetType type, ColumnAccumulator into)
    {
        // v1 page body = [definition levels][values]. num_values counts ROWS,
        // so for an optional column the values section is shorter than that.
        int[]? levels = null;
        if (spec.Optional)
        {
            var (decoded, consumed) = RleDecoder.DecodeLevelsWithLengthPrefix(body, numValues, bitWidth: 1);
            levels = decoded;
            body = body[consumed..];
        }

        var presentCount = levels is null ? numValues : levels.Count(l => l != 0);

        switch (type)
        {
            case ParquetType.Int64:
                into.AddInt64(DecodeInt64(body, presentCount), levels, numValues);
                break;
            case ParquetType.ByteArray:
                into.AddString(DecodeByteArray(body, presentCount), levels, numValues);
                break;
            case ParquetType.Boolean:
                into.AddBoolean(DecodeBoolean(body, presentCount), levels, numValues);
                break;
            default:
                throw new NotSupportedException(
                    $"column '{spec.Name}' has physical type {type}, which is not supported");
        }
    }

    private static long[] DecodeInt64(ReadOnlySpan<byte> body, int count)
    {
        if (body.Length < count * 8)
            throw new InvalidDataException($"INT64 page holds {body.Length} bytes for {count} values");
        var values = new long[count];
        for (var i = 0; i < count; i++)
            values[i] = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(i * 8, 8));
        return values;
    }

    private static bool[] DecodeBoolean(ReadOnlySpan<byte> body, int count)
    {
        if (body.Length < (count + 7) / 8)
            throw new InvalidDataException($"BOOLEAN page holds {body.Length} bytes for {count} values");
        var values = new bool[count];
        for (var i = 0; i < count; i++)
            values[i] = (body[i / 8] & (1 << (i % 8))) != 0;
        return values;
    }

    private static string[] DecodeByteArray(ReadOnlySpan<byte> body, int count)
    {
        var values = new string[count];
        var pos = 0;
        for (var i = 0; i < count; i++)
        {
            if (pos + 4 > body.Length)
                throw new InvalidDataException($"BYTE_ARRAY page ended after {i} of {count} values");
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
            pos += 4;
            if (length < 0 || pos + length > body.Length)
                throw new InvalidDataException($"BYTE_ARRAY value {i} claims {length} bytes past the page end");
            values[i] = Encoding.UTF8.GetString(body.Slice(pos, length));
            pos += length;
        }
        return values;
    }

    // Gathers a column across row groups and re-inserts nulls at their original
    // row positions. Values arrive densely — only the present ones — so this is
    // where a levels bug turns into values landing in the wrong rows.
    private sealed class ColumnAccumulator(ParquetFileWriter.ColumnSpec spec)
    {
        private readonly List<long?> _int64 = [];
        private readonly List<string?> _strings = [];
        private readonly List<bool?> _booleans = [];

        public void AddInt64(long[] present, int[]? levels, int rows) =>
            ScatterStruct(present, levels, rows, _int64);

        public void AddString(string[] present, int[]? levels, int rows) =>
            ScatterRef(present, levels, rows, _strings);

        public void AddBoolean(bool[] present, int[]? levels, int rows) =>
            ScatterStruct(present, levels, rows, _booleans);

        // Two near-identical overloads because `T?` means Nullable<T> for a
        // struct and an annotated reference for a class, and no single
        // constraint covers both.
        private static void ScatterStruct<T>(T[] present, int[]? levels, int rows, List<T?> into)
            where T : struct
        {
            if (levels is null)
            {
                foreach (var v in present) into.Add(v);
                return;
            }

            var next = 0;
            for (var row = 0; row < rows; row++)
            {
                if (levels[row] == 0) { into.Add(null); continue; }
                if (next >= present.Length)
                    throw new InvalidDataException(
                        "definition levels mark more present values than the page contains");
                into.Add(present[next++]);
            }
        }

        private static void ScatterRef<T>(T[] present, int[]? levels, int rows, List<T?> into)
            where T : class
        {
            if (levels is null)
            {
                foreach (var v in present) into.Add(v);
                return;
            }

            var next = 0;
            for (var row = 0; row < rows; row++)
            {
                if (levels[row] == 0) { into.Add(null); continue; }
                if (next >= present.Length)
                    throw new InvalidDataException(
                        "definition levels mark more present values than the page contains");
                into.Add(present[next++]);
            }
        }

        public ParquetColumn ToColumn() => spec.Type switch
        {
            ParquetType.Int64 => new ParquetColumn(spec, Int64Values: [.. _int64]),
            ParquetType.ByteArray => new ParquetColumn(spec, StringValues: [.. _strings]),
            ParquetType.Boolean => new ParquetColumn(spec, BooleanValues: [.. _booleans]),
            _ => throw new NotSupportedException($"physical type {spec.Type} is not supported"),
        };
    }
}
