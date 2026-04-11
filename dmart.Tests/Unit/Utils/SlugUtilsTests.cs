using Dmart.Utils;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

public class SlugUtilsTests
{
    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("  Multiple   Spaces  ", "multiple-spaces")]
    [InlineData("Already-slug", "already-slug")]
    [InlineData("Special!@#chars", "special-chars")]
    [InlineData("123 numbers OK", "123-numbers-ok")]
    public void ToSlug_Normalizes(string input, string expected)
    {
        SlugUtils.ToSlug(input).ShouldBe(expected);
    }

    [Fact]
    public void ToSlug_Empty_Returns_Empty()
    {
        SlugUtils.ToSlug("").ShouldBe("");
    }

    [Fact]
    public void ToSlug_All_Punctuation_Returns_Empty()
    {
        SlugUtils.ToSlug("!!!").ShouldBe("");
    }
}
