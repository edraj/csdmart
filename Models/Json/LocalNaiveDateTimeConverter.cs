using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dmart.Models.Json;

// JSON converter that mirrors Python dmart_plain's pydantic naive output:
// `created_at`/`updated_at` etc. emit as `2026-04-26T07:13:14.123456` —
// server-local wall clock, NO `Z`, NO `+03:00` offset. Matches Python
// `datetime.now().isoformat()` on a naive datetime byte-for-byte.
//
// Storage stays correct UTC: Npgsql returns DateTime with Kind=Utc, this
// converter converts that to Local on write. The DB still holds true UTC
// instants; only the wire shape changes. On parse, an offsetless string
// is interpreted as local-zone wall clock and adjusted to UTC for in-process
// arithmetic.
public sealed class LocalNaiveDateTimeConverter : JsonConverter<DateTime>
{
    // Matches Python's isoformat() output. `FFFFFFF` trims trailing zeros
    // in the fractional seconds (Python prints up to 6 digits and elides
    // trailing zeros via str(microsecond)); .NET's `FFFFFFF` gives the
    // same elision up to 7 digits. The wire shape stays ISO-parseable.
    private const string Format = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (string.IsNullOrEmpty(s))
            throw new JsonException("Expected a non-empty datetime string");
        // AssumeLocal handles Python-style naive strings (no offset);
        // AdjustToUniversal normalizes any tz-aware input to UTC so the
        // in-process value is comparable across the codebase.
        return DateTime.Parse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var local = value.Kind switch
        {
            DateTimeKind.Utc => value.ToLocalTime(),
            DateTimeKind.Local => value,
            // Unspecified is ambiguous; treat as already-local to match
            // Python's "naive == local" convention.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local),
        };
        writer.WriteStringValue(local.ToString(Format, CultureInfo.InvariantCulture));
    }
}
