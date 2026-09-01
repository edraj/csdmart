using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dmart.Client.Json;

// dmart validates payload bodies with JSON Schema, and there "type": "integer"
// means "a number with a zero fractional part". 10240.0 satisfies it, so dmart
// accepts, stores and returns that value for a field a schema calls an integer.
// System.Text.Json will not read it back into an `int`:
//
//   JsonException: The JSON value could not be converted to System.Int32.
//
// The result is a client stricter than the server it talks to — a caller whose
// model matches the schema still cannot read the entry. In practice the .0 comes
// from whatever wrote the entry: Python renders every float that way
// (json.dumps(10240.0) -> "10240.0"), and .NET does the same for a decimal
// carrying scale, so one field arrives as 10240.0 while its integer siblings
// arrive clean.
//
// These converters accept exactly the set JSON Schema calls an integer, and
// nothing wider — a value with a real fraction is still refused, because the
// schema would refuse it too. Comparison goes through decimal rather than
// double so integers past 2^53 keep every digit.
//
// Opt in per model:
//     [JsonConverter(typeof(IntegralInt32Converter))]
//     public int Data { get; set; }
//
// or for a whole payload body:
//     var options = new JsonSerializerOptions
//     {
//         Converters = { new IntegralInt32Converter(), new IntegralInt64Converter() },
//     };
//
// System.Text.Json wraps these for the Nullable<T> forms automatically, so
// registering them also covers `int?` and `long?`.

/// <summary>
/// Reads a JSON number into <see cref="int"/>, accepting the decimal-point
/// spelling of a whole number (<c>10240.0</c>) that JSON Schema counts as an
/// integer. A genuine fraction, or a value outside <see cref="int"/>, is still
/// rejected. Writes a plain integer.
/// </summary>
public sealed class IntegralInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException($"Expected a number for {typeToConvert.Name}, found {reader.TokenType}.");

        // The common case never leaves the integer path.
        if (reader.TryGetInt32(out var exact)) return exact;

        // decimal, not double: past 2^53 a double comparison would call two
        // different values equal, and TryGetDecimal also rejects magnitudes
        // decimal cannot hold rather than throwing.
        if (reader.TryGetDecimal(out var value)
            && decimal.Truncate(value) == value
            && value >= int.MinValue && value <= int.MaxValue)
        {
            return (int)value;
        }

        throw new JsonException(
            "The JSON value is not a whole number representable as System.Int32. "
            + "Only the decimal-point spelling of an integer (for example 10240.0) is accepted.");
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>
/// <see cref="IntegralInt32Converter"/> for <see cref="long"/>.
/// </summary>
public sealed class IntegralInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException($"Expected a number for {typeToConvert.Name}, found {reader.TokenType}.");

        if (reader.TryGetInt64(out var exact)) return exact;

        if (reader.TryGetDecimal(out var value)
            && decimal.Truncate(value) == value
            && value >= long.MinValue && value <= long.MaxValue)
        {
            return (long)value;
        }

        throw new JsonException(
            "The JSON value is not a whole number representable as System.Int64. "
            + "Only the decimal-point spelling of an integer (for example 10240.0) is accepted.");
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
