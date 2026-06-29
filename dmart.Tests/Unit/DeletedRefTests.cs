using Dmart.Models.Core;
using Shouldly;
using Xunit;

namespace dmart.Tests.Unit;

public sealed class DeletedRefTests
{
    [Theory]
    [InlineData("myspace", "/", "docs", "myspace/docs")]
    [InlineData("myspace", "/docs", "a", "myspace/docs/a")]
    [InlineData("myspace", "docs/sub", "b", "myspace/docs/sub/b")]
    [InlineData("myspace", "", "x", "myspace/x")]
    public void ToPath_Joins_And_Collapses_Empty_Subpath(
        string space, string subpath, string shortname, string expected)
        => new DeletedRef(space, subpath, shortname).ToPath().ShouldBe(expected);
}
