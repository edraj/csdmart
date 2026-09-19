using System.Text.Json;

namespace Dmart.Cli;

internal static class CliConsole
{
    // Pretty-print JSON to stdout, colorizing only when CliColor says we may.
    // Every CLI subcommand that emits JSON goes through here, so
    // `dmart version | jq -r .version` gets parseable bytes rather than
    // SGR-laced ones.
    public static void PrintJson(JsonElement el)
    {
        WriteJson(Console.Out, el, CliColor.Stdout);
        Console.Out.WriteLine();
    }

    // Core emitter. `color` is passed in rather than read from CliColor so
    // the layout can be tested against both settings without touching the
    // process's real stdout.
    //
    // Both modes produce the same 2-space-indented shape — the no-color path
    // is the colorized path with the escapes omitted, not a separate
    // serializer. That's deliberate: a second code path is a second place for
    // the formatting to drift, and the point of the no-color output is that
    // it is the *same* document.
    public static void WriteJson(TextWriter w, JsonElement el, bool color, int indent = 0)
    {
        var pad = new string(' ', indent * 2);
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var props = el.EnumerateObject().ToList();
                if (props.Count == 0) { w.Write("{}"); break; }
                w.WriteLine("{");
                for (var i = 0; i < props.Count; i++)
                {
                    w.Write(pad);
                    w.Write("  ");
                    // Property names are JSON strings too — encode them the
                    // same way values are, or a key containing a quote or a
                    // backslash produces a document that won't parse.
                    w.Write(Paint(color, "36", JsonEncode(props[i].Name)));
                    w.Write(": ");
                    WriteJson(w, props[i].Value, color, indent + 1);
                    w.WriteLine(i < props.Count - 1 ? "," : "");
                }
                w.Write(pad);
                w.Write('}');
                break;

            case JsonValueKind.Array:
                var items = el.EnumerateArray().ToList();
                if (items.Count == 0) { w.Write("[]"); break; }
                w.WriteLine("[");
                for (var i = 0; i < items.Count; i++)
                {
                    w.Write(pad);
                    w.Write("  ");
                    WriteJson(w, items[i], color, indent + 1);
                    w.WriteLine(i < items.Count - 1 ? "," : "");
                }
                w.Write(pad);
                w.Write(']');
                break;

            case JsonValueKind.String:
                // dmart responses carry their outcome in a plain string field
                // (`"status": "success"`), so the two values an operator scans
                // for get the success/error colors rather than the generic
                // string color. Purely cosmetic — the bytes are identical
                // once color is off.
                var s = el.GetString() ?? "";
                var sgr = s switch
                {
                    "success" => "32",
                    "failed" or "error" => "31",
                    _ => "33",
                };
                w.Write(Paint(color, sgr, JsonEncode(s)));
                break;

            case JsonValueKind.Number:
                // GetRawText preserves the source spelling (1e3, big integers)
                // — ToString() would round-trip the value through double.
                w.Write(Paint(color, "35", el.GetRawText()));
                break;

            case JsonValueKind.True:
                w.Write(Paint(color, "32", "true"));
                break;

            case JsonValueKind.False:
                w.Write(Paint(color, "31", "false"));
                break;

            case JsonValueKind.Null:
                w.Write(Paint(color, "90", "null"));
                break;

            default:
                w.Write(el.GetRawText());
                break;
        }
    }

    // Cyan=keys, Yellow=strings, Magenta=numbers, Green=true, Red=false,
    // Gray=null — or the bare text when color is off.
    private static string Paint(bool color, string sgr, string text) =>
        color ? $"\u001b[{sgr}m{text}\u001b[0m" : text;

    // Serialize one string as a JSON string literal, quotes included. The old
    // printer interpolated the raw value between two quote characters, so a
    // value containing `"`, a backslash, or a control character emitted
    // invalid JSON even with color stripped.
    private static string JsonEncode(string value)
    {
        using var ms = new MemoryStream();
        using (var jw = new Utf8JsonWriter(ms))
        {
            jw.WriteStringValue(value);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
