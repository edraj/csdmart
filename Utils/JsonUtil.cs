using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Dmart;

// Small AOT-safe JSON helpers reused across the codebase. Kept reflection-free
// so they compile cleanly under `PublishAot=true`.
internal static class JsonUtil
{
    // UnsafeRelaxedJsonEscaping mirrors LogSink's default: non-ASCII (Arabic,
    // emoji, …) flows through without \uXXXX escaping so plugin configs
    // stay human-readable in the log.
    private static readonly JsonWriterOptions CompactOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    // Parse JSON into a standalone JsonElement. `JsonDocument.Parse(x).RootElement`
    // is the tempting one-liner, but a JsonDocument rents its buffer from the
    // ArrayPool and only returns it on Dispose — the chained form never
    // disposes, so every call leaks one pooled buffer to the GC (.NET 11's
    // analyzer flags it as CA2026). Clone() copies the element out of the
    // document, so the document can be disposed immediately. .NET 11 ships
    // JsonElement.Parse for exactly this; swap the body for it when the TFM
    // moves.
    public static JsonElement ParseElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static JsonElement ParseElement(ReadOnlyMemory<byte> utf8Json)
    {
        using var doc = JsonDocument.Parse(utf8Json);
        return doc.RootElement.Clone();
    }

    // Strip whitespace from a JSON buffer so it fits cleanly inside a
    // single-line structured log message (no embedded \n escapes). Falls
    // back to the raw text if the buffer doesn't parse as JSON — callers
    // logging user-supplied content shouldn't fail just because the input
    // was malformed.
    public static string Compact(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            using var ms = new MemoryStream(bytes.Length);
            using (var w = new Utf8JsonWriter(ms, CompactOpts))
                doc.WriteTo(w);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    public static string Compact(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream(Encoding.UTF8.GetByteCount(json));
            using (var w = new Utf8JsonWriter(ms, CompactOpts))
                doc.WriteTo(w);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json;
        }
    }
}
