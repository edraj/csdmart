using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dmart.DataAdapters.Parquet;

// Column helpers shared by every table mapper.
//
// Extracted rather than abstracted: dmart's exported types share an 18-column
// "Metas" core (shortname, uuid, timestamps, acl, payload, …) and these
// builders were already written once for entries. Copying them into six more
// files would mean six places for the same definition-level bug to hide, and
// nulls are the thing most likely to be silently wrong.
//
// Each table still declares its own schema and mapping explicitly. The shared
// part is only the mechanical "value -> page" step, which has one correct
// implementation.
internal static class Pq
{
    // ---- schema ----

    public static ParquetFileWriter.ColumnSpec Required(string name) =>
        new(name, ParquetType.ByteArray, ConvertedType.Utf8);

    public static ParquetFileWriter.ColumnSpec Optional(string name) =>
        new(name, ParquetType.ByteArray, ConvertedType.Utf8, Optional: true);

    public static ParquetFileWriter.ColumnSpec Timestamp(string name) =>
        new(name, ParquetType.Int64, ConvertedType.TimestampMicros);

    public static ParquetFileWriter.ColumnSpec OptionalTimestamp(string name) =>
        new(name, ParquetType.Int64, ConvertedType.TimestampMicros, Optional: true);

    public static ParquetFileWriter.ColumnSpec Flag(string name) =>
        new(name, ParquetType.Boolean, null);

    public static ParquetFileWriter.ColumnSpec OptionalFlag(string name) =>
        new(name, ParquetType.Boolean, null, Optional: true);

    public static ParquetFileWriter.ColumnSpec OptionalInt(string name) =>
        new(name, ParquetType.Int64, null, Optional: true);

    // ---- write ----

    public static ParquetFileWriter.ColumnPage Str<T>(IReadOnlyList<T> rows, Func<T, string> f) =>
        new(ParquetFileWriter.PlainByteArray([.. rows.Select(f)]), null);

    public static ParquetFileWriter.ColumnPage NullableStr<T>(IReadOnlyList<T> rows, Func<T, string?> f)
    {
        var present = new List<string>(rows.Count);
        var levels = new List<int>(rows.Count);
        foreach (var r in rows)
        {
            var v = f(r);
            levels.Add(v is null ? 0 : 1);
            if (v is not null) present.Add(v);
        }
        return new(ParquetFileWriter.PlainByteArray(present), levels);
    }

    public static ParquetFileWriter.ColumnPage Bool<T>(IReadOnlyList<T> rows, Func<T, bool> f) =>
        new(ParquetFileWriter.PlainBoolean([.. rows.Select(f)]), null);

    public static ParquetFileWriter.ColumnPage NullableBool<T>(IReadOnlyList<T> rows, Func<T, bool?> f)
    {
        var present = new List<bool>(rows.Count);
        var levels = new List<int>(rows.Count);
        foreach (var r in rows)
        {
            var v = f(r);
            levels.Add(v is null ? 0 : 1);
            if (v is not null) present.Add(v.Value);
        }
        return new(ParquetFileWriter.PlainBoolean(present), levels);
    }

    public static ParquetFileWriter.ColumnPage Ts<T>(IReadOnlyList<T> rows, Func<T, DateTime> f) =>
        new(ParquetFileWriter.PlainTimestampMicros([.. rows.Select(f)]), null);

    public static ParquetFileWriter.ColumnPage NullableTs<T>(IReadOnlyList<T> rows, Func<T, DateTime?> f)
    {
        var present = new List<DateTime>(rows.Count);
        var levels = new List<int>(rows.Count);
        foreach (var r in rows)
        {
            var v = f(r);
            levels.Add(v is null ? 0 : 1);
            if (v is not null) present.Add(v.Value);
        }
        return new(ParquetFileWriter.PlainTimestampMicros(present), levels);
    }

    public static ParquetFileWriter.ColumnPage NullableInt<T>(IReadOnlyList<T> rows, Func<T, int?> f)
    {
        var present = new List<long>(rows.Count);
        var levels = new List<int>(rows.Count);
        foreach (var r in rows)
        {
            var v = f(r);
            levels.Add(v is null ? 0 : 1);
            if (v is not null) present.Add(v.Value);
        }
        return new(ParquetFileWriter.PlainInt64(present), levels);
    }

    /// <summary>Serializes to an opaque JSON string, or null. Source-generated only.</summary>
    public static string? Json<T>(T? value, JsonTypeInfo<T> info) where T : class
        => value is null ? null : JsonSerializer.Serialize(value, info);

    /// <summary>
    /// Serializes a non-nullable collection. Always a string, never null: an
    /// empty list must round-trip as empty, and writing null would collapse
    /// "none" and "unknown" into one value on restore.
    /// </summary>
    public static string JsonAlways<T>(T value, JsonTypeInfo<T> info)
        => JsonSerializer.Serialize(value, info);

    // ---- read ----

    public static string?[] Strings(ParquetFileReader.ParquetTable t, string name) =>
        t.Column(name).StringValues
        ?? throw new InvalidDataException($"column '{name}' is not a string column");

    public static bool?[] Bools(ParquetFileReader.ParquetTable t, string name) =>
        t.Column(name).BooleanValues
        ?? throw new InvalidDataException($"column '{name}' is not a boolean column");

    public static long?[] Longs(ParquetFileReader.ParquetTable t, string name) =>
        t.Column(name).Int64Values
        ?? throw new InvalidDataException($"column '{name}' is not an int64 column");

    public static T? FromJson<T>(string? json, JsonTypeInfo<T> info) where T : class
        => json is null ? null : JsonSerializer.Deserialize(json, info);

    /// <summary>
    /// The file stores UTC instants; dmart stores local-naive DateTimes (a
    /// timestamp-without-tz column, matching Python's naive datetimes). Handing
    /// back a UTC-kind value would round-trip the INSTANT correctly and still
    /// change the value every consumer reads.
    /// </summary>
    public static DateTime ToLocalNaive(DateTime? utc) =>
        utc is null ? default : DateTime.SpecifyKind(utc.Value.ToLocalTime(), DateTimeKind.Unspecified);

    public static DateTime? ToLocalNaiveOrNull(DateTime? utc) =>
        utc is null ? null : DateTime.SpecifyKind(utc.Value.ToLocalTime(), DateTimeKind.Unspecified);
}
