using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.Options;
using Dmart.Utils;

namespace Dmart;

// Single writer to LOG_FILE, shared by both the generic ILogger pipeline
// (via FileLoggerProvider) and the per-request access log emitted by
// RequestLoggingMiddleware. Having one file handle + one lock avoids
// interleaved writes that two independent StreamWriters would produce.
//
// When `DmartSettings.LogFile` is empty the sink is "inactive" — all Write*
// calls are no-ops, so callers can depend on this type unconditionally.
// When LogFile is set the sink opens the file in append mode with AutoFlush
// so lines are durable without an explicit flush per message; format is
// always JSON lines (matches Python `.ljson.log`).
//
// Records are serialized with a hand-rolled Utf8JsonWriter walk so the
// writer stays AOT-safe — request/response bodies are captured as nested
// Dictionary<string, object?> / List<object?> trees, which the default
// `JsonSerializer.Serialize<object>` overload can't handle without
// reflection. This writer accepts string/bool/number/null leaves plus
// nested Dictionary and List containers.
public sealed class LogSink : IDisposable
{
    private readonly Stream? _stream;
    private readonly object _lock = new();

    // UnsafeRelaxedJsonEscaping keeps non-ASCII content (Arabic, emoji) as-is
    // rather than escaping each char — readable logs, and matches Python's
    // ensure_ascii=False default.
    private static readonly JsonWriterOptions WriterOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };
    private static readonly byte[] Newline = new[] { (byte)'\n' };

    public bool IsActive => _stream is not null;

    public LogSink(IOptions<DmartSettings> settings)
    {
        var path = settings.Value.LogFile;
        if (string.IsNullOrWhiteSpace(path)) return;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    // Generic ILogger → one JSONL line per event. Shape mirrors Python's
    // base JsonFormatter output so operators see a uniform schema whether
    // the line came from a plugin log or a request handler.
    public void WriteLog(string category, LogLevel level, string message)
    {
        if (_stream is null) return;
        var record = new Dictionary<string, object?>
        {
            ["hostname"] = Environment.MachineName,
            ["time"] = TimeUtils.Now().ToString("yyyy-MM-dd HH:mm:ss,fff"),
            ["level"] = PythonLevel(level),
            ["category"] = category,
            ["message"] = message,
            ["thread"] = "MainThread",
            ["process"] = Environment.ProcessId,
        };
        WriteObject(record);
    }

    // Per-request access record. Already-built dictionary so the middleware
    // can attach custom props (request body, response body, headers map)
    // without a chain of overloads. Level is derived from status code so
    // grepping for "WARNING"/"ERROR" in the log still works.
    public void WriteAccessRecord(Dictionary<string, object?> record)
    {
        if (_stream is null) return;
        WriteObject(record);
    }

    private void WriteObject(Dictionary<string, object?> record)
    {
        using var buf = new MemoryStream(256);
        using (var writer = new Utf8JsonWriter(buf, WriterOpts))
        {
            WriteValue(writer, record);
        }
        buf.Write(Newline, 0, Newline.Length);
        var bytes = buf.ToArray();
        lock (_lock)
        {
            _stream!.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }
    }

    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case float f:
                w.WriteNumberValue(f);
                break;
            case decimal m:
                w.WriteNumberValue(m);
                break;
            case Dictionary<string, object?> dict:
                w.WriteStartObject();
                foreach (var (k, v) in dict)
                {
                    w.WritePropertyName(k);
                    WriteValue(w, v);
                }
                w.WriteEndObject();
                break;
            case IReadOnlyList<object?> list:
                w.WriteStartArray();
                foreach (var item in list) WriteValue(w, item);
                w.WriteEndArray();
                break;
            default:
                // Fallback — emit as string so the record is still valid JSON.
                w.WriteStringValue(value.ToString() ?? "");
                break;
        }
    }

    public static string PythonLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "DEBUG",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "INFO",
    };

    public void Dispose() => _stream?.Dispose();
}
