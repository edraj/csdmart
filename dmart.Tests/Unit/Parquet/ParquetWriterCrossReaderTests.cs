using System.Diagnostics;
using System.Text.Json;
using Dmart.DataAdapters.Parquet;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Parquet;

// The encoder is hand-written against the Parquet spec, so the only test that
// proves anything is whether an INDEPENDENT implementation agrees.
//
// Round-tripping our writer through our own reader would prove almost nothing:
// both sides can share a misunderstanding of the spec and agree with each
// other forever. So verification crosses pyarrow — see
// docs/parquet-export-design.md §2.3, which requires this in both directions
// once the reader exists.
//
// Skips when python3/pyarrow is absent rather than failing: the encoder is
// still built and unit-testable without it, and a machine without pyarrow
// should not turn a missing tool into a red build. The skip reason says so, so
// a silently-never-running test is visible rather than assumed-green.
public class ParquetWriterCrossReaderTests
{
    [FactIfPyArrow]
    public void PyArrow_Reads_What_We_Wrote()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-pq-{Guid.NewGuid():N}.parquet");
        try
        {
            var columns = new List<ParquetFileWriter.ColumnSpec>
            {
                new("seq",  ParquetType.Int64,     null),
                new("name", ParquetType.ByteArray, ConvertedType.Utf8),
            };
            var writer = new ParquetFileWriter(columns);

            long[] seq = [10, 20, 30, 40];
            string[] names = ["alpha", "beta", "gamma", "delta"];

            using (var fs = File.Create(path))
                writer.Write(fs,
                    [ParquetFileWriter.PlainInt64(seq), ParquetFileWriter.PlainByteArray(names)],
                    seq.Length);

            var read = PyArrow.ReadTable(path);

            // Values, not just "it parsed". A reader that returns the wrong
            // numbers still parses.
            read.GetProperty("seq").EnumerateArray().Select(x => x.GetInt64())
                .ShouldBe(seq);
            read.GetProperty("name").EnumerateArray().Select(x => x.GetString())
                .ShouldBe(names);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // The UTF8 annotation is what makes a BYTE_ARRAY column read back as a
    // string rather than opaque bytes. Getting it wrong is invisible in our own
    // encoder and obvious to any consumer, which is exactly the class of bug
    // this cross-check exists for.
    [FactIfPyArrow]
    public void Byte_Array_Columns_Are_Typed_As_Strings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-pq-{Guid.NewGuid():N}.parquet");
        try
        {
            var writer = new ParquetFileWriter(
                [new("payload", ParquetType.ByteArray, ConvertedType.Utf8)]);
            string[] payloads = ["{\"a\":1}", "{\"b\":[2,3]}"];
            using (var fs = File.Create(path))
                writer.Write(fs, [ParquetFileWriter.PlainByteArray(payloads)], payloads.Length);

            PyArrow.ReadSchema(path).ShouldContain("payload: string");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Non-ASCII through the whole chain: our UTF-8 encoding, the 4-byte length
    // prefix, and pyarrow's decode. Arabic because dmart's corpus is full of
    // it, and because a length prefix counted in characters rather than bytes
    // passes every ASCII test ever written.
    [FactIfPyArrow]
    public void Non_Ascii_Survives_The_Length_Prefix()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-pq-{Guid.NewGuid():N}.parquet");
        try
        {
            var writer = new ParquetFileWriter(
                [new("text", ParquetType.ByteArray, ConvertedType.Utf8)]);
            string[] values = ["مرحبا", "naïve", "日本語", "plain"];
            using (var fs = File.Create(path))
                writer.Write(fs, [ParquetFileWriter.PlainByteArray(values)], values.Length);

            PyArrow.ReadTable(path).GetProperty("text").EnumerateArray()
                .Select(x => x.GetString()).ShouldBe(values);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Nulls are the thing most likely to be silently wrong. A definition-level
    // bug does not corrupt the file — it decodes, with values shifted into the
    // wrong rows. So this asserts null POSITIONS against a pattern where a
    // shift of one would still produce the right null COUNT.
    [FactIfPyArrow]
    public void Null_Positions_Survive_The_Definition_Levels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-pq-{Guid.NewGuid():N}.parquet");
        try
        {
            var writer = new ParquetFileWriter(
                [new("payload", ParquetType.ByteArray, ConvertedType.Utf8, Optional: true)]);

            //            row: 0     1      2     3      4     5
            string?[] rows = ["a", null, null, "b", null, "c"];
            var present = rows.Where(r => r is not null).Select(r => r!).ToArray();
            var levels = rows.Select(r => r is null ? 0 : 1).ToList();

            using (var fs = File.Create(path))
                writer.Write(fs,
                    [new ParquetFileWriter.ColumnPage(
                        ParquetFileWriter.PlainByteArray(present), levels)],
                    rows.Length);

            var back = PyArrow.ReadTable(path).GetProperty("payload").EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.Null ? null : x.GetString())
                .ToArray();

            back.ShouldBe(rows, "a shifted definition level keeps the null count and moves the values");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // An optional column containing no nulls still needs its levels section,
    // because the reader decides how to parse the page from the SCHEMA. Omit
    // it and the values are read as levels.
    [FactIfPyArrow]
    public void Optional_Column_With_No_Nulls_Still_Round_Trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-pq-{Guid.NewGuid():N}.parquet");
        try
        {
            var writer = new ParquetFileWriter(
                [new("n", ParquetType.Int64, null, Optional: true)]);
            long[] values = [7, 8, 9];
            using (var fs = File.Create(path))
                writer.Write(fs,
                    [new ParquetFileWriter.ColumnPage(
                        ParquetFileWriter.PlainInt64(values), [1, 1, 1])],
                    values.Length);

            PyArrow.ReadTable(path).GetProperty("n").EnumerateArray()
                .Select(x => x.GetInt64()).ShouldBe(values);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // BOOLEAN is bit-packed in PLAIN, not one byte per value. Nine values on
    // purpose: it crosses a byte boundary, where an off-by-one in the packing
    // shows up and an eight-value test would not.
    [FactIfPyArrow]
    public void Booleans_Are_Bit_Packed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-pq-{Guid.NewGuid():N}.parquet");
        try
        {
            var writer = new ParquetFileWriter([new("flag", ParquetType.Boolean, null)]);
            bool[] flags = [true, false, true, true, false, false, true, false, true];
            using (var fs = File.Create(path))
                writer.Write(fs, [ParquetFileWriter.PlainBoolean(flags)], flags.Length);

            PyArrow.ReadTable(path).GetProperty("flag").EnumerateArray()
                .Select(x => x.GetBoolean()).ShouldBe(flags);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Timestamps must come back as the same instant, not the same wall clock.
    // pyarrow reports TIMESTAMP_MICROS as an ISO string, so this compares the
    // instant it decoded against the UTC value we meant.
    [FactIfPyArrow]
    public void Timestamps_Round_Trip_As_The_Same_Instant()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-pq-{Guid.NewGuid():N}.parquet");
        try
        {
            var writer = new ParquetFileWriter(
                [new("ts", ParquetType.Int64, ConvertedType.TimestampMicros)]);
            DateTime[] stamps =
            [
                new(2026, 8, 13, 17, 4, 5, DateTimeKind.Utc),
                DateTime.UnixEpoch,
            ];
            using (var fs = File.Create(path))
                writer.Write(fs, [ParquetFileWriter.PlainTimestampMicros(stamps)], stamps.Length);

            var back = PyArrow.ReadTable(path).GetProperty("ts").EnumerateArray()
                .Select(x => DateTime.Parse(x.GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal))
                .ToArray();

            back[0].ShouldBe(stamps[0]);
            back[1].ShouldBe(DateTime.UnixEpoch);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Row groups are the unit of writer memory, so the export streams them one
    // at a time. Two things have to hold and only one of them is about values:
    // the rows must read back in write order, AND they must actually be
    // separate row groups. A writer that merged everything into one group would
    // pass every value assertion here while quietly reintroducing the
    // whole-file-in-memory shape this exists to avoid — hence num_row_groups.
    [FactIfPyArrow]
    public void Multiple_Row_Groups_Read_Back_In_Order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-pq-{Guid.NewGuid():N}.parquet");
        try
        {
            var writer = new ParquetFileWriter(
            [
                new("seq",  ParquetType.Int64,     null),
                new("name", ParquetType.ByteArray, ConvertedType.Utf8),
            ]);

            // Deliberately uneven: 4, 4, 2. The last group being short is the
            // normal case for a pager, and a writer that assumes a fixed size
            // reads the tail as garbage.
            long[][] groups = [[1, 2, 3, 4], [5, 6, 7, 8], [9, 10]];

            using (var fs = File.Create(path))
            {
                writer.Start(fs);
                foreach (var g in groups)
                    writer.WriteRowGroup(
                    [
                        new ParquetFileWriter.ColumnPage(ParquetFileWriter.PlainInt64(g), null),
                        new ParquetFileWriter.ColumnPage(
                            ParquetFileWriter.PlainByteArray([.. g.Select(n => $"row{n}")]), null),
                    ], g.Length);
                writer.Finish();
            }

            var all = groups.SelectMany(g => g).ToArray();
            var table = PyArrow.ReadTable(path);
            table.GetProperty("seq").EnumerateArray().Select(x => x.GetInt64()).ShouldBe(all);
            table.GetProperty("name").EnumerateArray().Select(x => x.GetString())
                 .ShouldBe(all.Select(n => $"row{n}"));

            PyArrow.NumRowGroups(path).ShouldBe(3,
                "merging the groups would pass the value checks and lose the memory bound");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Definition levels are per row group, so a null pattern that differs
    // between groups catches level state leaking across the boundary — which
    // decodes cleanly and puts values in the wrong rows.
    [FactIfPyArrow]
    public void Nulls_Are_Scoped_To_Their_Row_Group()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-pq-{Guid.NewGuid():N}.parquet");
        try
        {
            var writer = new ParquetFileWriter(
                [new("payload", ParquetType.ByteArray, ConvertedType.Utf8, Optional: true)]);

            string?[][] groups =
            [
                ["a", null, "b"],       // nulls in the middle
                [null, null, null],     // all null
                ["c", "d"],             // none null
            ];

            using (var fs = File.Create(path))
            {
                writer.Start(fs);
                foreach (var g in groups)
                    writer.WriteRowGroup(
                    [
                        new ParquetFileWriter.ColumnPage(
                            ParquetFileWriter.PlainByteArray([.. g.Where(v => v is not null).Select(v => v!)]),
                            [.. g.Select(v => v is null ? 0 : 1)]),
                    ], g.Length);
                writer.Finish();
            }

            PyArrow.ReadTable(path).GetProperty("payload").EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.Null ? null : x.GetString())
                .ShouldBe(groups.SelectMany(g => g));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // The whole reason offsets are counted rather than read from the stream:
    // an HTTP response body cannot be seeked, and streaming an export straight
    // to a client is the case the format exists for. Using stream.Position
    // instead throws here rather than in production.
    [FactIfPyArrow]
    public void Writes_To_A_Non_Seekable_Destination()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmart-pq-{Guid.NewGuid():N}.parquet");
        try
        {
            var writer = new ParquetFileWriter([new("seq", ParquetType.Int64, null)]);
            long[] a = [1, 2, 3], b = [4, 5];

            using (var fs = File.Create(path))
            using (var forwardOnly = new ForwardOnlyStream(fs))
            {
                writer.Start(forwardOnly);
                writer.WriteRowGroup([new ParquetFileWriter.ColumnPage(ParquetFileWriter.PlainInt64(a), null)], a.Length);
                writer.WriteRowGroup([new ParquetFileWriter.ColumnPage(ParquetFileWriter.PlainInt64(b), null)], b.Length);
                writer.Finish();
            }

            PyArrow.ReadTable(path).GetProperty("seq").EnumerateArray()
                .Select(x => x.GetInt64()).ShouldBe([1, 2, 3, 4, 5]);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Write-only, forward-only: what an HTTP response body behaves like.
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override void WriteByte(byte value) => inner.WriteByte(value);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

// Shells out to pyarrow. Deliberately a separate process: the point is an
// implementation that shares no code with ours.
internal static class PyArrow
{
    internal static bool Available { get; } = Probe();

    private static bool Probe()
    {
        try { return Run("import pyarrow") is not null; }
        catch { return false; }
    }

    internal static JsonElement ReadTable(string path)
    {
        // default=str because json.dumps cannot encode datetime — pyarrow
        // returns real datetime objects for TIMESTAMP columns, and without
        // this the harness fails on a file that is perfectly valid.
        var json = Run($"import pyarrow.parquet as pq, json; "
                     + $"print(json.dumps(pq.read_table(r'{path}').to_pydict(), default=str))")
            ?? throw new InvalidOperationException("pyarrow read failed");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    // Proves the groups are physically separate rather than concatenated —
    // invisible in the values, but the difference between a bounded export and
    // one that holds the whole file.
    internal static int NumRowGroups(string path)
        => int.Parse(
            Run($"import pyarrow.parquet as pq; print(pq.ParquetFile(r'{path}').num_row_groups)")
            ?? throw new InvalidOperationException("pyarrow row-group read failed"),
            System.Globalization.CultureInfo.InvariantCulture);

    internal static string ReadSchema(string path)
        => Run($"import pyarrow.parquet as pq; print(pq.read_table(r'{path}').schema)")
           ?? throw new InvalidOperationException("pyarrow schema read failed");

    private static string? Run(string script)
    {
        var psi = new ProcessStartInfo("python3", ["-c", script])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return null;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"python3 exited {p.ExitCode}: {stderr}");
        return stdout.Trim();
    }
}

public sealed class FactIfPyArrowAttribute : FactAttribute
{
    public FactIfPyArrowAttribute()
    {
        if (!PyArrow.Available)
            Skip = "python3 with pyarrow not available — the cross-reader check "
                 + "cannot run, and our own reader agreeing with our own writer "
                 + "would not substitute for it";
    }
}
