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
        var json = Run($"import pyarrow.parquet as pq, json; "
                     + $"print(json.dumps(pq.read_table(r'{path}').to_pydict()))")
            ?? throw new InvalidOperationException("pyarrow read failed");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

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
