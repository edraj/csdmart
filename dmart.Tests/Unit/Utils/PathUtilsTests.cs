using Dmart.Utils;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

public class PathUtilsTests
{
    [Theory]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("foo", "/foo")]
    [InlineData("/foo/", "/foo")]
    [InlineData("/foo/bar/", "/foo/bar")]
    public void Normalize_Strips_And_Prepends(string input, string expected)
    {
        PathUtils.Normalize(input).ShouldBe(expected);
    }

    [Fact]
    public void Join_Concatenates_With_Single_Slashes()
    {
        PathUtils.Join("foo", "bar", "baz").ShouldBe("/foo/bar/baz");
    }

    [Fact]
    public void Join_Skips_Empty_Segments()
    {
        PathUtils.Join("foo", "", "bar").ShouldBe("/foo/bar");
    }
}
