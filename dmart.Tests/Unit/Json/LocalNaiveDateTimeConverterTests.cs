using System;
using System.Globalization;
using System.Text.Json;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Json;

// Pins the Python-parity DateTime wire shape: server-local wall clock, NO
// offset (no `Z`, no `+03:00`). The DB stores correct UTC instants — Npgsql
// returns DateTime with Kind=Utc — and this converter rewrites that to the
// local-zone wall clock on the way out, matching pydantic naive isoformat().
public sealed class LocalNaiveDateTimeConverterTests
{
    private static JsonSerializerOptions Options() => new()
    {
        Converters = { new LocalNaiveDateTimeConverter() },
    };

    [Fact]
    public void Serializes_Utc_As_Local_Wall_Clock_No_Offset()
    {
        // Pick a known UTC instant and verify the JSON is the same instant
        // expressed in the system's local wall clock, with no offset suffix.
        var utc = new DateTime(2026, 4, 26, 4, 13, 14, 123, DateTimeKind.Utc).AddTicks(4560);
        var expectedLocal = utc.ToLocalTime();

        var json = JsonSerializer.Serialize(utc, Options());

        // Strip the JSON quotes for the format check.
        var s = json.Trim('"');
        s.ShouldNotContain("Z");
        s.ShouldNotMatch(@"[+-]\d{2}:\d{2}$");
        // The string round-trips to the same instant when re-parsed as local.
        var parsed = DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
        parsed.ShouldBe(expectedLocal, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Roundtrips_Naive_String_To_Utc()
    {
        // Python emits naive ISO strings; on read we parse them as local
        // wall clock and adjust to UTC for in-process arithmetic.
        var local = new DateTime(2026, 4, 26, 7, 13, 14, 123, DateTimeKind.Local);
        var pythonStyle = local.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        var json = "\"" + pythonStyle + "\"";

        var parsed = JsonSerializer.Deserialize<DateTime>(json, Options());

        parsed.Kind.ShouldBe(DateTimeKind.Utc);
        parsed.ShouldBe(local.ToUniversalTime(), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Serializes_Local_Kind_Verbatim_No_Offset()
    {
        // A Kind=Local DateTime — what TimeUtils.Now() returns — emits the
        // same wall clock with no offset.
        var local = new DateTime(2026, 4, 26, 7, 13, 14, DateTimeKind.Local);

        var s = JsonSerializer.Serialize(local, Options()).Trim('"');

        s.ShouldStartWith("2026-04-26T07:13:14");
        s.ShouldNotContain("Z");
        s.ShouldNotMatch(@"[+-]\d{2}:\d{2}$");
    }

    // The source-generated DmartJsonContext picks up the converter via
    // [JsonSourceGenerationOptions(Converters = ...)] — verify the wire
    // shape end-to-end through the context that production code uses.
    [Fact]
    public void DmartJsonContext_Serializes_Entry_CreatedAt_Without_Z_Or_Offset()
    {
        var entry = new Dmart.Models.Core.Entry
        {
            Uuid = "00000000-0000-0000-0000-000000000000",
            Shortname = "probe",
            SpaceName = "test",
            Subpath = "/",
            ResourceType = Dmart.Models.Enums.ResourceType.Content,
            OwnerShortname = "dmart",
            CreatedAt = new DateTime(2026, 4, 26, 4, 13, 14, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 4, 26, 4, 13, 14, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(entry, DmartJsonContext.Default.Entry);

        json.ShouldContain("\"created_at\":");
        json.ShouldContain("\"updated_at\":");
        // No `Z` and no explicit offset in either timestamp.
        var doc = JsonDocument.Parse(json);
        var created = doc.RootElement.GetProperty("created_at").GetString()!;
        created.ShouldNotContain("Z");
        created.ShouldNotMatch(@"[+-]\d{2}:\d{2}$");
    }
}
