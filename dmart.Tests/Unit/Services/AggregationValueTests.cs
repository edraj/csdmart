using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins what an aggregation cell may be turned into on its way to the wire.
//
// Both conversions this helper used to perform existed only to dodge a gap in
// Models/Json/DmartJsonContext.cs — the source-gen context did not know every
// runtime type that can land in a Dictionary<string, object>, so QueryService
// narrowed values to types it did know. Both were lossy, and neither was needed
// once the context registers the types:
//
//   long -> int      `long` was ALREADY registered, so this bought nothing and
//                    silently wrapped anything past int.MaxValue.
//   decimal -> double  PostgreSQL returns SUM/AVG over numeric as `numeric`,
//                    which Npgsql hands back as decimal. Going through double
//                    destroys exactness that money aggregates depend on:
//                    12345678901234567.89 comes back 12345678901234568.
public class AggregationValueTests
{
    [Fact]
    public void Long_Is_Not_Narrowed_To_Int()
    {
        // A COUNT past int.MaxValue must not wrap to a negative number.
        AggregationValue.ForWire(3_000_000_000L).ShouldBe(3_000_000_000L);
        AggregationValue.ForWire(long.MaxValue).ShouldBe(long.MaxValue);
        AggregationValue.ForWire(4L).ShouldBe(4L);
    }

    [Fact]
    public void Decimal_Keeps_Its_Exact_Value()
    {
        // The cents survive. (double) loses them outright — and the type has to
        // stay decimal, or Shouldly's numeric coercion would hide the loss.
        static decimal Wire(decimal d) => AggregationValue.ForWire(d).ShouldBeOfType<decimal>();

        Wire(12345678901234567.89m).ShouldBe(12345678901234567.89m);
        Wire(0.1m + 0.2m).ShouldBe(0.3m);
        Wire(decimal.MaxValue).ShouldBe(decimal.MaxValue);
        Wire(decimal.MinValue).ShouldBe(decimal.MinValue);
    }

    // PostgreSQL's AVG(numeric) returns scale 16 — avg of 10,20,30,30 is
    // literally "22.5000000000000000". decimal PRESERVES trailing-zero scale
    // through System.Text.Json (unlike double), so emitting it raw would change
    // a long-standing wire shape from 22.5 to 22.5000000000000000. Stripping the
    // trailing zeros keeps the shape callers already see while keeping the value
    // exact — the point of moving off double in the first place.
    [Theory]
    [InlineData("22.5000000000000000", "22.5")]
    [InlineData("90", "90")]
    [InlineData("1000.0", "1000")]
    [InlineData("0.30000000000000000000", "0.3")]
    [InlineData("0.000", "0")]
    [InlineData("-22.5000000000000000", "-22.5")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    public void Decimal_Trailing_Zeros_Are_Stripped(string input, string expected)
    {
        var value = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
        var wire = AggregationValue.ForWire(value).ShouldBeOfType<decimal>();

        wire.ShouldBe(value, "normalising scale must never change the value");
        wire.ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
    }

    [Fact]
    public void Other_Types_Pass_Through_Untouched()
    {
        AggregationValue.ForWire("g").ShouldBe("g");
        AggregationValue.ForWire(true).ShouldBe(true);
        AggregationValue.ForWire(2.5d).ShouldBe(2.5d);
        AggregationValue.ForWire(7).ShouldBe(7);
    }
}
