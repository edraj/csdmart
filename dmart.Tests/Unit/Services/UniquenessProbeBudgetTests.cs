using System.Collections.Generic;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// The multiply that decides whether a uniqueness compound is allowed to run.
//
// A compound expands into the CARTESIAN PRODUCT of its per-path token lists,
// one search per combination. The path count is operator-controlled
// (`unique_fields` on the folder) but the token counts come from array lengths
// in the REQUEST BODY, so the product is attacker-controlled: a single path
// over a 50MB array of short strings is ~5.8M probes, and a second path
// multiplies rather than adds.
//
// These pin the two properties the caller depends on — that it counts
// correctly below the cap, and that it cannot itself become the expensive or
// wrong step above it.
public class UniquenessProbeBudgetTests
{
    private static List<List<string>> Sets(params int[] sizes)
    {
        var outer = new List<List<string>>();
        foreach (var n in sizes)
        {
            var inner = new List<string>(n);
            for (var i = 0; i < n; i++) inner.Add($"t{i}");
            outer.Add(inner);
        }
        return outer;
    }

    [Theory]
    [InlineData(new[] { 1 }, 1)]
    [InlineData(new[] { 7 }, 7)]
    [InlineData(new[] { 3, 4 }, 12)]
    [InlineData(new[] { 2, 3, 5 }, 30)]
    public void Counts_The_Product_Below_The_Cap(int[] sizes, long expected)
        => UniquenessValidator.ProbeCount(Sets(sizes), 1000).ShouldBe(expected);

    [Fact]
    public void An_Empty_Compound_Is_One_Probe_Not_Zero()
    {
        // The empty product is 1, and the callers guard `perPathTokens.Count == 0`
        // before reaching here. Pinned so a "return 0" refactor can't turn an
        // unguarded call into a silent skip of the whole compound.
        UniquenessValidator.ProbeCount(Sets(), 1000).ShouldBe(1);
    }

    [Fact]
    public void A_Zero_Length_Path_Collapses_The_Product()
    {
        // One path contributing no tokens means no combination exists.
        UniquenessValidator.ProbeCount(Sets(5, 0, 5), 1000).ShouldBe(0);
    }

    [Fact]
    public void Reports_Over_Cap_Rather_Than_The_True_Product()
    {
        // cap+1 is the whole contract above the line: callers only ask
        // "is this over", and the real answer can be astronomically large.
        UniquenessValidator.ProbeCount(Sets(100, 100), 1000).ShouldBe(1001);
    }

    [Fact]
    public void Does_Not_Overflow_On_A_Product_That_Cannot_Fit_In_Long()
    {
        // 64 paths of 4 tokens is 2^128. A non-saturating multiply wraps, and a
        // wrapped value can land BELOW the cap — which would admit exactly the
        // request the cap exists to refuse. This is the case that matters.
        var sizes = new int[64];
        for (var i = 0; i < 64; i++) sizes[i] = 4;

        var n = UniquenessValidator.ProbeCount(Sets(sizes), 1000);

        n.ShouldBe(1001);
        n.ShouldBeGreaterThan(1000);
    }

    [Fact]
    public void Stops_Multiplying_As_Soon_As_It_Passes_The_Cap()
    {
        // Cheap to evaluate however absurd the input: the guard must not itself
        // be a way to burn CPU.
        var sizes = new int[10_000];
        for (var i = 0; i < 10_000; i++) sizes[i] = 2;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        UniquenessValidator.ProbeCount(Sets(sizes), 1000).ShouldBe(1001);
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeLessThan(1000);
    }
}
