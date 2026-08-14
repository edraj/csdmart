using Dmart.DataAdapters.Parquet;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Parquet;

// The reader is verified against files pyarrow WROTE.
//
// This is the direction that carries the weight. Round-tripping our writer
// through our reader is nearly worthless on its own: both halves are ours, both
// can share the same misreading of the spec, and they would agree with each
// other forever. Only an independently produced file can tell us the bytes mean
// what we think.
//
// It also exercises paths our own writer never takes — bit-packed level runs,
// several data pages in one chunk, fields we do not emit — which is precisely
// where a reader that was only ever fed its own writer's output falls over.
//
// docs/parquet-export-design.md §2.3 requires both directions; the round-trip
// tests at the end are the weaker half, kept because they pin the pair together.
public class ParquetReaderCrossWriterTests
{
    // pyarrow's defaults are dictionary encoding and snappy, neither of which
    // is in our profile, so every file here is written explicitly in the
    // profile we support. That is not the reader dodging hard cases: the
    // rejection tests below prove it refuses the others loudly.
    private const string Profile = "use_dictionary=False, version='1.0', data_page_version='1.0'";

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"dmart-pqr-{Guid.NewGuid():N}.parquet");

    [FactIfPyArrow]
    public void Reads_Int64_And_String_Columns_Written_By_PyArrow()
    {
        var path = TempPath();
        try
        {
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq; "
                + "t = pa.table({'seq': pa.array([10, 20, 30, 40], pa.int64()), "
                + "'name': pa.array(['alpha', 'beta', 'gamma', 'delta'])}); "
                + $"pq.write_table(t, r'{path}', compression='none', {Profile})");

            var table = ParquetFileReader.ReadFile(path);

            table.RowCount.ShouldBe(4);
            table.Column("seq").Int64Values.ShouldBe([10L, 20L, 30L, 40L]);
            table.Column("name").StringValues.ShouldBe(["alpha", "beta", "gamma", "delta"]);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [FactIfPyArrow]
    public void Reads_Zstd_Compressed_Pages_Written_By_PyArrow()
    {
        var path = TempPath();
        try
        {
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq; "
                + "t = pa.table({'payload': pa.array(['{\"a\":1}'] * 500)}); "
                + $"pq.write_table(t, r'{path}', compression='zstd', {Profile})");

            var values = ParquetFileReader.ReadFile(path).Column("payload").StringValues!;
            values.Length.ShouldBe(500);
            values.ShouldAllBe(v => v == "{\"a\":1}");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // pyarrow packs alternating definition levels as BIT-PACKED runs, which our
    // writer never emits. Without the bit-packed branch in RleDecoder this
    // returns an eighth of the data, or nulls in the wrong rows — and the
    // pattern below keeps the null COUNT right under a shift, so only the
    // positions give it away.
    [FactIfPyArrow]
    public void Reads_Nulls_From_PyArrows_Bit_Packed_Levels()
    {
        var path = TempPath();
        try
        {
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq; "
                + "t = pa.table({'payload': pa.array(['a', None, None, 'b', None, 'c', 'd', None, 'e'])}); "
                + $"pq.write_table(t, r'{path}', compression='none', {Profile})");

            ParquetFileReader.ReadFile(path).Column("payload").StringValues
                .ShouldBe(["a", null, null, "b", null, "c", "d", null, "e"]);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [FactIfPyArrow]
    public void Reads_Booleans_Written_By_PyArrow()
    {
        var path = TempPath();
        try
        {
            // Nine values, so the bit packing crosses a byte boundary.
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq; "
                + "t = pa.table({'flag': pa.array([True, False, True, True, False, False, True, False, True])}); "
                + $"pq.write_table(t, r'{path}', compression='none', {Profile})");

            ParquetFileReader.ReadFile(path).Column("flag").BooleanValues
                .ShouldBe([true, false, true, true, false, false, true, false, true]);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [FactIfPyArrow]
    public void Reads_Timestamps_As_The_Same_Instant()
    {
        var path = TempPath();
        try
        {
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq, datetime as dt; "
                + "t = pa.table({'ts': pa.array("
                + "[dt.datetime(2026, 8, 13, 17, 4, 5), dt.datetime(1970, 1, 1)], pa.timestamp('us'))}); "
                + $"pq.write_table(t, r'{path}', compression='none', {Profile})");

            ParquetFileReader.ReadFile(path).Column("ts").AsTimestamps()
                .ShouldBe([new DateTime(2026, 8, 13, 17, 4, 5, DateTimeKind.Utc), DateTime.UnixEpoch]);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [FactIfPyArrow]
    public void Reads_Non_Ascii_Written_By_PyArrow()
    {
        var path = TempPath();
        try
        {
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq; "
                + "t = pa.table({'text': pa.array(['\\u0645\\u0631\\u062d\\u0628\\u0627', "
                + "'na\\u00efve', '\\u65e5\\u672c\\u8a9e', 'plain'])}); "
                + $"pq.write_table(t, r'{path}', compression='none', {Profile})");

            ParquetFileReader.ReadFile(path).Column("text").StringValues
                .ShouldBe(["مرحبا", "naïve", "日本語", "plain"]);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Row groups must be concatenated in file order. Getting this wrong
    // reorders rows without raising anything.
    [FactIfPyArrow]
    public void Reads_Rows_Across_PyArrows_Row_Groups_In_Order()
    {
        var path = TempPath();
        try
        {
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq; "
                + "t = pa.table({'seq': pa.array(list(range(10)), pa.int64())}); "
                + $"pq.write_table(t, r'{path}', row_group_size=3, compression='none', {Profile})");

            ParquetFileReader.ReadFile(path).Column("seq").Int64Values
                .ShouldBe([0L, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Several data pages inside ONE column chunk — pyarrow splits at a byte
    // threshold. A reader that stops after the first page silently drops the
    // tail of every large column, which on a restore is missing data that
    // reports success.
    [FactIfPyArrow]
    public void Reads_Every_Page_When_A_Chunk_Holds_Several()
    {
        var path = TempPath();
        try
        {
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq; "
                + "t = pa.table({'seq': pa.array(list(range(20000)), pa.int64())}); "
                + $"pq.write_table(t, r'{path}', data_page_size=1024, compression='none', {Profile})");

            var values = ParquetFileReader.ReadFile(path).Column("seq").Int64Values!;
            values.Length.ShouldBe(20000, "stopping after the first page loses the rest of the chunk");
            values[0].ShouldBe(0L);
            values[^1].ShouldBe(19999L);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- refusals ----
    //
    // These are as important as the successes. Each is something a real Parquet
    // file may contain that we cannot decode, and the requirement is to fail
    // saying so rather than return plausible wrong values into a restore.

    [FactIfPyArrow]
    public void Rejects_Dictionary_Encoding_Rather_Than_Misreading_It()
    {
        var path = TempPath();
        try
        {
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq; "
                + "t = pa.table({'name': pa.array(['a', 'b', 'a', 'b'])}); "
                + $"pq.write_table(t, r'{path}', use_dictionary=True, compression='none', "
                + "version='1.0', data_page_version='1.0')");

            // Dictionary indexes read as values would be a column of small
            // integers reinterpreted as data — decodes fine, means nothing.
            Should.Throw<NotSupportedException>(() => ParquetFileReader.ReadFile(path))
                  .Message.ShouldContain("dictionary");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [FactIfPyArrow]
    public void Rejects_V2_Data_Pages()
    {
        var path = TempPath();
        try
        {
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq; "
                + "t = pa.table({'seq': pa.array([1, 2, 3], pa.int64())}); "
                + $"pq.write_table(t, r'{path}', use_dictionary=False, compression='none', "
                + "version='2.6', data_page_version='2.0')");

            Should.Throw<NotSupportedException>(() => ParquetFileReader.ReadFile(path))
                  .Message.ShouldContain("v2");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [FactIfPyArrow]
    public void Rejects_An_Unsupported_Codec()
    {
        var path = TempPath();
        try
        {
            PyArrow.Exec("import pyarrow as pa, pyarrow.parquet as pq; "
                + "t = pa.table({'seq': pa.array([1, 2, 3], pa.int64())}); "
                + $"pq.write_table(t, r'{path}', compression='gzip', {Profile})");

            Should.Throw<NotSupportedException>(() => ParquetFileReader.ReadFile(path))
                  .Message.ShouldContain("codec");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Rejects_A_File_That_Is_Not_Parquet()
    {
        using var ms = new MemoryStream("this is not a parquet file at all, not even close"u8.ToArray());
        Should.Throw<InvalidDataException>(() => ParquetFileReader.Read(ms));
    }

    [Fact]
    public void Rejects_A_Truncated_File()
    {
        // Valid leading magic, nothing else — the shape a half-finished export
        // leaves behind, and one a reader must not treat as empty-but-fine.
        using var ms = new MemoryStream([.. "PAR1"u8.ToArray(), .. new byte[16]]);
        Should.Throw<InvalidDataException>(() => ParquetFileReader.Read(ms));
    }

    // ---- round trip (the weaker direction, kept to pin the pair together) ----

    [Fact]
    public void Our_Writer_Round_Trips_Through_Our_Reader()
    {
        var path = TempPath();
        try
        {
            var writer = new ParquetFileWriter(
            [
                new("seq",     ParquetType.Int64,     null),
                new("name",    ParquetType.ByteArray, ConvertedType.Utf8, Optional: true),
                new("active",  ParquetType.Boolean,   null),
                new("updated", ParquetType.Int64,     ConvertedType.TimestampMicros),
            ]);

            long[] seq = [1, 2, 3, 4, 5];
            string?[] names = ["a", null, "c", null, "e"];
            bool[] active = [true, false, true, true, false];
            var stamps = Enumerable.Range(0, 5)
                .Select(i => new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc).AddSeconds(i))
                .ToArray();

            using (var fs = File.Create(path))
                writer.Write(fs,
                [
                    new ParquetFileWriter.ColumnPage(ParquetFileWriter.PlainInt64(seq), null),
                    new ParquetFileWriter.ColumnPage(
                        ParquetFileWriter.PlainByteArray([.. names.Where(n => n is not null).Select(n => n!)]),
                        [.. names.Select(n => n is null ? 0 : 1)]),
                    new ParquetFileWriter.ColumnPage(ParquetFileWriter.PlainBoolean(active), null),
                    new ParquetFileWriter.ColumnPage(ParquetFileWriter.PlainTimestampMicros(stamps), null),
                ], seq.Length);

            var table = ParquetFileReader.ReadFile(path);
            table.RowCount.ShouldBe(5);
            table.Column("seq").Int64Values.ShouldBe([1L, 2, 3, 4, 5]);
            table.Column("name").StringValues.ShouldBe(names);
            table.Column("active").BooleanValues.ShouldBe([true, false, true, true, false]);
            table.Column("updated").AsTimestamps().ShouldBe(stamps.Select(s => (DateTime?)s));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Our_Writer_Round_Trips_Multiple_Row_Groups()
    {
        var path = TempPath();
        try
        {
            var writer = new ParquetFileWriter([new("seq", ParquetType.Int64, null)]);
            long[][] groups = [[1, 2, 3], [4, 5], [6, 7, 8, 9]];

            using (var fs = File.Create(path))
            {
                writer.Start(fs);
                foreach (var g in groups)
                    writer.WriteRowGroup(
                        [new ParquetFileWriter.ColumnPage(ParquetFileWriter.PlainInt64(g), null)], g.Length);
                writer.Finish();
            }

            var table = ParquetFileReader.ReadFile(path);
            table.RowCount.ShouldBe(9);
            table.Column("seq").Int64Values.ShouldBe([1L, 2, 3, 4, 5, 6, 7, 8, 9]);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
