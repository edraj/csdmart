using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dmart.Cli;

// Generates a JSON instance that satisfies a (subset of) JSON Schema, for
// `dmart client-schema-migrate --seed`. Used two ways:
//
//   1. whole-body generation for an empty folder (N fake records)
//   2. single-property generation when repairing an existing record that is
//      missing a required field (see SchemaMigrateCommand.RepairBody)
//
// SCOPE. This covers the keywords dmart schemas actually use — type,
// properties, required, enum, const, format, items, minimum/maximum,
// minLength/maxLength, minItems/maxItems, multipleOf, and the
// oneOf/anyOf/allOf composers. It is NOT a general-purpose JSON Schema
// instance generator: $ref, if/then/else, patternProperties, and `pattern`
// (which would need a regex-to-string synthesiser) are not honoured. A schema
// leaning on those still gets a value of the right TYPE, so the record lands;
// it just may not satisfy that particular constraint. The command reports
// what it wrote, so a validation failure surfaces as a failed create rather
// than silently-wrong data.
//
// DETERMINISM. The generator is seeded per (schema, index) rather than from
// the clock, so re-running `--seed` against the same schema produces the same
// values. Sample data that churns on every run makes diffs unreadable and
// makes "did my change do that?" unanswerable.
internal sealed class FakeDataGenerator(int seed)
{
    private readonly Random _rng = new(seed);

    // Deterministic per (schema shortname, record index) — see header.
    public static FakeDataGenerator For(string schemaShortname, int index)
        => new(unchecked(StableHash(schemaShortname) * 397 + index));

    // FNV-1a. String.GetHashCode is randomised per-process by design, so it
    // cannot be the basis of anything that must reproduce across runs.
    private static int StableHash(string s)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in s) hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    // Checked in this order — see the composer note in Build.
    private static readonly string[] Composers = ["allOf", "oneOf", "anyOf"];

    private static readonly string[] Words =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
        "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa",
    ];

    // Parallel arrays — index i is the same word in each language, so a record's
    // three translations read as translations of ONE name rather than three
    // unrelated ones. Real Arabic/Kurdish script rather than transliteration:
    // sample data is what the CXB admin UI gets rendered with, and RTL text is
    // exactly the thing a Latin-only placeholder would fail to exercise.
    private static readonly string[] WordsAr =
    [
        "ألفا", "برافو", "تشارلي", "دلتا", "إيكو", "فوكستروت", "غولف", "هوتيل",
        "إنديا", "جولييت", "كيلو", "ليما", "مايك", "نوفمبر", "أوسكار", "بابا",
    ];

    private static readonly string[] WordsKu =
    [
        "ئەلفا", "براڤۆ", "چارلی", "دێلتا", "ئێکۆ", "فۆکستڕۆت", "گۆلف", "هۆتێل",
        "ئیندیا", "جولیێت", "کیلۆ", "لیما", "مایک", "نۆڤەمبەر", "ئۆسکار", "پاپا",
    ];

    /// <summary>
    /// Builds an instance of <paramref name="schema"/>. Returns null when the
    /// schema is not an object (nothing to generate against).
    /// </summary>
    public JsonNode? Generate(JsonElement schema)
        => schema.ValueKind != JsonValueKind.Object ? null : Build(schema, depth: 0);

    /// <summary>
    /// A display name for the entry's `displayname` column, in each language
    /// dmart's Translation carries.
    /// </summary>
    /// <remarks>
    /// These columns sit on the entry itself, not inside payload.body, so no
    /// JSON Schema describes them and <see cref="Generate"/> never fills them.
    /// Seeded records left them null, which made every sample row render as a
    /// bare shortname in CXB.
    ///
    /// Draw order matters: this shares the instance RNG with Generate, so
    /// callers must draw in a FIXED order (body, then displayname, then
    /// description) or the values shift between runs and determinism is lost.
    /// </remarks>
    public (string En, string Ar, string Ku) DisplayName()
    {
        var i = _rng.Next(Words.Length);
        var n = _rng.Next(1, 1000);
        return ($"{Capitalize(Words[i])} {n}", $"{WordsAr[i]} {n}", $"{WordsKu[i]} {n}");
    }

    /// <summary>
    /// A one-line description for the entry's `description` column. See the
    /// draw-order note on <see cref="DisplayName"/>.
    /// </summary>
    /// <param name="subject">
    /// The schema shortname, so the text says what the row is a sample OF
    /// instead of being interchangeable filler.
    /// </param>
    public (string En, string Ar, string Ku) Description(string subject)
    {
        var i = _rng.Next(Words.Length);
        return ($"Sample {subject} record ({Words[i]}), generated by dmart client-schema-migrate.",
                $"سجل {subject} تجريبي ({WordsAr[i]})، تم إنشاؤه بواسطة dmart client-schema-migrate.",
                $"تۆماری نموونەیی {subject} ({WordsKu[i]})، دروستکراوە لەلایەن dmart client-schema-migrate.");
    }

    // Invariant casing: the culture-sensitive overload is a no-op under
    // InvariantGlobalization (which this binary sets) but would still read as
    // locale-dependent to anyone auditing this later.
    private static string Capitalize(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Builds a value for ONE property of an object schema — the repair path,
    /// which fills a required-but-missing field without touching the rest of
    /// the record. Returns null when the schema does not define that property.
    /// </summary>
    public JsonNode? GenerateProperty(JsonElement schema, string propertyName)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;
        if (!schema.TryGetProperty("properties", out var props)
            || props.ValueKind != JsonValueKind.Object) return null;
        if (!props.TryGetProperty(propertyName, out var propSchema)) return null;
        return Build(propSchema, depth: 0);
    }

    // Depth guard: a self-referential schema (object → object → …) would
    // otherwise recurse until the stack gives out. At the limit we emit the
    // empty value for the type rather than descending again.
    private const int MaxDepth = 6;

    private JsonNode? Build(JsonElement schema, int depth)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;

        // `const` and `enum` pin the value regardless of type, so they win over
        // every other keyword and are checked first.
        if (schema.TryGetProperty("const", out var constVal))
            return JsonNodeFrom(constVal);

        if (schema.TryGetProperty("enum", out var enumVal)
            && enumVal.ValueKind == JsonValueKind.Array)
        {
            var choices = enumVal.EnumerateArray().ToList();
            if (choices.Count > 0) return JsonNodeFrom(choices[_rng.Next(choices.Count)]);
        }

        // Composers: generate against the first branch that yields something.
        // Picking a RANDOM branch would make output non-reproducible across
        // schema edits — adding a branch would reshuffle every later draw.
        //
        // Only consulted when the schema does not describe itself directly. A
        // schema carrying both `properties` and an `allOf` refinement is
        // described by its own properties; deferring to the branch there would
        // silently drop every field the schema declares inline.
        if (!schema.TryGetProperty("properties", out _) && !schema.TryGetProperty("type", out _))
        {
            foreach (var composer in Composers)
            {
                if (!schema.TryGetProperty(composer, out var branches)
                    || branches.ValueKind != JsonValueKind.Array) continue;
                foreach (var branch in branches.EnumerateArray())
                {
                    var built = Build(branch, depth + 1);
                    if (built is not null) return built;
                }
            }
        }

        return TypeOf(schema) switch
        {
            "object" => BuildObject(schema, depth),
            "array" => BuildArray(schema, depth),
            "string" => JsonValue.Create(BuildString(schema)),
            "integer" => JsonValue.Create(BuildInteger(schema)),
            "number" => JsonValue.Create(BuildNumber(schema)),
            "boolean" => JsonValue.Create(_rng.Next(2) == 1),
            "null" => null,
            // No `type` at all: emit a short string. Untyped schemas accept it,
            // and it is the least surprising thing to see in sample data.
            _ => JsonValue.Create(Word()),
        };
    }

    // `type` may be a string or an array of strings ("nullable" idiom:
    // ["string", "null"]). For the array form take the first non-"null" entry —
    // emitting null for a field the sample data is meant to demonstrate would
    // defeat the purpose of seeding it.
    private static string? TypeOf(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var t)) return null;
        if (t.ValueKind == JsonValueKind.String) return t.GetString();
        if (t.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in t.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String
                    && item.GetString() is { } s && s != "null") return s;
        }
        return null;
    }

    private JsonObject BuildObject(JsonElement schema, int depth)
    {
        var obj = new JsonObject();
        if (depth >= MaxDepth) return obj;
        if (!schema.TryGetProperty("properties", out var props)
            || props.ValueKind != JsonValueKind.Object) return obj;

        // Required fields are always emitted. Optional ones are emitted too:
        // sample data whose optional fields are all absent is a poor
        // demonstration of the schema, and any consumer that trips over an
        // optional field being present was going to trip over it in real data.
        foreach (var prop in props.EnumerateObject())
            obj[prop.Name] = Build(prop.Value, depth + 1);

        return obj;
    }

    private JsonArray BuildArray(JsonElement schema, int depth)
    {
        var arr = new JsonArray();
        if (depth >= MaxDepth) return arr;

        var min = IntKeyword(schema, "minItems") ?? 1;
        var max = IntKeyword(schema, "maxItems") ?? Math.Max(min, 3);
        var count = min >= max ? min : _rng.Next(min, max + 1);
        // A schema with minItems:0 and no maxItems still gets one element —
        // an always-empty array demonstrates nothing about the item shape.
        if (count == 0) count = 1;

        if (!schema.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Object) return arr;

        // The (JsonNode?) cast forces the non-generic Add(JsonNode?) overload:
        // the generic JsonArray.Add<T> is RequiresUnreferencedCode/-DynamicCode
        // and fails the AOT zero-warning build (same note FolderRenderingFixer
        // carries).
        for (var i = 0; i < count; i++) arr.Add((JsonNode?)Build(items, depth + 1));
        return arr;
    }

    // `format` drives the shape where present — a "date-time" field holding
    // "alpha-7" is worse than useless as sample data, because it looks like a
    // parser bug rather than a placeholder.
    private string BuildString(JsonElement schema)
    {
        var format = schema.TryGetProperty("format", out var f)
            && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

        var value = format switch
        {
            "email" => $"{Word()}.{Word()}@example.com",
            "date" => Date().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // The trailing 'Z' is REQUIRED, not decoration: JSON Schema's
            // date-time is RFC 3339, which mandates a timezone offset. Emitting
            // a bare "2024-05-13T14:23:45" fails validation on every record.
            "date-time" => Date().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            // Likewise for full-time: RFC 3339 requires the offset here too.
            "time" => Date().ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            "uuid" => DeterministicGuid().ToString(),
            "uri" or "url" or "iri" => $"https://example.com/{Word()}",
            "hostname" => $"{Word()}.example.com",
            "ipv4" => $"10.{_rng.Next(256)}.{_rng.Next(256)}.{_rng.Next(1, 255)}",
            _ => $"{Word()}-{_rng.Next(1000)}",
        };

        // Length bounds are applied AFTER formatting: truncating an email to
        // maxLength yields something that is no longer a valid email, but a
        // schema that sets both has already made that trade-off itself, and
        // the length constraint is the one the validator will actually reject.
        var minLength = IntKeyword(schema, "minLength");
        var maxLength = IntKeyword(schema, "maxLength");
        if (maxLength is { } max && value.Length > max) value = value[..Math.Max(0, max)];
        if (minLength is { } min && value.Length < min) value = value.PadRight(min, 'x');
        return value;
    }

    private long BuildInteger(JsonElement schema)
    {
        // Exclusive bounds are converted to inclusive ones so both spellings
        // funnel into a single range draw. The `(long?)` casts are load-bearing:
        // without them the conditional's branches are `long` and `null`, which
        // has no common type (CS0173).
        var min = LongKeyword(schema, "minimum")
            ?? (LongKeyword(schema, "exclusiveMinimum") is { } exMin ? exMin + 1 : (long?)null)
            ?? 1;
        var max = LongKeyword(schema, "maximum")
            ?? (LongKeyword(schema, "exclusiveMaximum") is { } exMax ? exMax - 1 : (long?)null)
            ?? min + 999;
        if (max < min) max = min;

        var value = min == max ? min : min + (long)(_rng.NextDouble() * (max - min));

        // multipleOf: snap DOWN to the nearest multiple, then step back up if
        // that fell below the minimum. Snapping up first could exceed maximum.
        if (LongKeyword(schema, "multipleOf") is { } step && step > 0)
        {
            value -= value % step;
            while (value < min) value += step;
        }
        return value;
    }

    private double BuildNumber(JsonElement schema)
    {
        var min = DoubleKeyword(schema, "minimum") ?? DoubleKeyword(schema, "exclusiveMinimum") ?? 1d;
        var max = DoubleKeyword(schema, "maximum") ?? DoubleKeyword(schema, "exclusiveMaximum") ?? min + 999d;
        if (max < min) max = min;
        // Two decimals: dmart sample data is overwhelmingly money/quantity, and
        // full double precision produces values that read as noise.
        return Math.Round(min + _rng.NextDouble() * (max - min), 2);
    }

    private string Word() => Words[_rng.Next(Words.Length)];

    // Fixed epoch, not DateTime.Now — see the determinism note in the header.
    private DateTime Date() => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        .AddDays(_rng.Next(730)).AddSeconds(_rng.Next(86400));

    private Guid DeterministicGuid()
    {
        var bytes = new byte[16];
        _rng.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static int? IntKeyword(JsonElement schema, string name)
        => schema.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i) ? i : null;

    private static long? LongKeyword(JsonElement schema, string name)
        => schema.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var l) ? l : null;

    private static double? DoubleKeyword(JsonElement schema, string name)
        => schema.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) ? d : null;

    // JsonNode.Parse over the raw text rather than JsonNode conversion helpers:
    // the generic JsonValue.Create<T> overloads are RequiresDynamicCode and
    // fail the AOT zero-warning build (same constraint FolderRenderingFixer
    // documents for JsonArray.Add).
    private static JsonNode? JsonNodeFrom(JsonElement el)
        => el.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(el.GetRawText());
}
