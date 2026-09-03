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
// The fraction is inspected in the RAW token, not after parsing. decimal
// rounds at 28-29 significant digits, so `10240.000...001` with enough zeros
// parses to exactly 10240 and would have slipped through a
// `decimal.Truncate(v) == v` test — quietly returning 10240 for a value the
// schema refuses, which is the one thing this converter promises not to do.
//
// They also respect the caller's JsonNumberHandling. Registering a converter
// replaces System.Text.Json's built-in handling for that type wholesale, so a
// converter that ignores NumberHandling silently cancels it — and the README
// tells callers to register these on their own JsonSerializerOptions, where
// they may well have set it for a loose upstream API.
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


// Shared by both converters below.
internal static class IntegralNumberReader
{
    /// <summary>
    /// True when the raw token carries a non-zero digit after the decimal
    /// point — i.e. a fraction that survives regardless of what decimal
    /// rounding does to it later.
    /// </summary>
    /// <remarks>
    /// Only the plain `123.45` shape is judged here. With an exponent the
    /// point moves and "digits after the dot" stops meaning anything, so those
    /// fall through to the decimal comparison, which is exact for every
    /// magnitude decimal can hold.
    /// </remarks>
    internal static bool HasNonZeroFraction(ref Utf8JsonReader reader)
    {
        // ValueSequence only appears when the token straddles a buffer
        // boundary in a streaming read; copying it is the documented way to
        // get a contiguous view, and System.Buffers.BuffersExtensions.ToArray
        // is not on netstandard2.1, so it is copied by hand.
        ReadOnlySpan<byte> raw;
        byte[]? rented = null;
        if (reader.HasValueSequence)
        {
            rented = new byte[checked((int)reader.ValueSequence.Length)];
            var offset = 0;
            foreach (var segment in reader.ValueSequence)
            {
                segment.Span.CopyTo(rented.AsSpan(offset));
                offset += segment.Length;
            }
            raw = rented;
        }
        else
        {
            raw = reader.ValueSpan;
        }

        var dot = raw.IndexOf((byte)'.');
        if (dot < 0) return false;

        for (var i = dot + 1; i < raw.Length; i++)
        {
            var b = raw[i];
            if (b == (byte)'e' || b == (byte)'E') return false;   // exponent: defer
            if (b != (byte)'0') return true;
        }

        _ = rented;
        return false;
    }

    /// <summary>
    /// Honours <see cref="JsonNumberHandling.AllowReadingFromString"/>: returns
    /// the quoted number's text when the caller asked for that, else null.
    /// </summary>
    internal static string? ReadFromStringOrNull(ref Utf8JsonReader reader, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
           && (options.NumberHandling & JsonNumberHandling.AllowReadingFromString) != 0
            ? reader.GetString()
            : null;
}

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
        // A quoted number, when the caller opted into that. Registering a
        // converter takes over the type completely, so without this the
        // caller's AllowReadingFromString would stop working for int alone.
        if (IntegralNumberReader.ReadFromStringOrNull(ref reader, options) is { } quoted)
        {
            return int.TryParse(quoted, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var fromString)
                ? fromString
                : throw new JsonException(
                    $"The quoted value '{quoted}' is not a whole number representable as System.Int32.");
        }

        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException($"Expected a number for {typeToConvert.Name}, found {reader.TokenType}.");

        // The common case never leaves the integer path.
        if (reader.TryGetInt32(out var exact)) return exact;

        // Checked before decimal sees it: decimal rounds, and a rounded-away
        // fraction is exactly the value this converter must refuse.
        if (IntegralNumberReader.HasNonZeroFraction(ref reader))
            throw new JsonException(
                "The JSON value has a fractional part and is not an integer. "
                + "Only the decimal-point spelling of a whole number (for example 10240.0) is accepted.");

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
    {
        // WriteAsString is the caller's choice about the whole document; a
        // converter that always wrote a bare number would silently exempt
        // int from it.
        if ((options.NumberHandling & JsonNumberHandling.WriteAsString) != 0)
            writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else
            writer.WriteNumberValue(value);
    }
}

/// <summary>
/// <see cref="IntegralInt32Converter"/> for <see cref="long"/>.
/// </summary>
public sealed class IntegralInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // A quoted number, when the caller opted into that. Registering a
        // converter takes over the type completely, so without this the
        // caller's AllowReadingFromString would stop working for long alone.
        if (IntegralNumberReader.ReadFromStringOrNull(ref reader, options) is { } quoted)
        {
            return long.TryParse(quoted, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var fromString)
                ? fromString
                : throw new JsonException(
                    $"The quoted value '{quoted}' is not a whole number representable as System.Int64.");
        }

        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException($"Expected a number for {typeToConvert.Name}, found {reader.TokenType}.");

        if (reader.TryGetInt64(out var exact)) return exact;

        // Checked before decimal sees it: decimal rounds, and a rounded-away
        // fraction is exactly the value this converter must refuse.
        if (IntegralNumberReader.HasNonZeroFraction(ref reader))
            throw new JsonException(
                "The JSON value has a fractional part and is not an integer. "
                + "Only the decimal-point spelling of a whole number (for example 10240.0) is accepted.");

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
    {
        // WriteAsString is the caller's choice about the whole document; a
        // converter that always wrote a bare number would silently exempt
        // long from it.
        if ((options.NumberHandling & JsonNumberHandling.WriteAsString) != 0)
            writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else
            writer.WriteNumberValue(value);
    }
}
