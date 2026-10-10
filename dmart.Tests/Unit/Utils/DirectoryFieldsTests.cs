using Dmart.Utils;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

public sealed class DirectoryFieldsTests
{
    [Theory]
    [InlineData("  Alice@Example.ORG ", "alice@example.org")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Addresses_Are_Trimmed_And_Folded(string? raw, string? expected)
        => DirectoryFields.NormalizeAddress(raw).ShouldBe(expected);

    [Fact]
    public void Address_Lists_Drop_Blanks_And_Duplicates_Keeping_First_Order()
        => DirectoryFields.NormalizeAddresses(["B@x.org", "a@x.org", " b@X.org", ""])
            .ShouldBe(new[] { "b@x.org", "a@x.org" });

    [Fact]
    public void Services_Are_Folded_And_Deduplicated()
        => DirectoryFields.NormalizeServices(["Mail", "matrix", " mail "]).ShouldBe(new[] { "mail", "matrix" });

    [Theory]
    [InlineData("mail", true)]
    [InlineData("gitea-ci_2", true)]
    [InlineData("2fa", false)]          // must start with a letter
    [InlineData("Mail", false)]         // validated after folding, so never seen folded-out
    [InlineData("bad service", false)]
    public void Service_Names_Are_Slugs(string name, bool valid)
        => DirectoryFields.IsValidService(name).ShouldBe(valid);

    [Theory]
    [InlineData("a@example.org", "example.org")]
    [InlineData("no-at-sign", null)]
    [InlineData("trailing@", null)]
    public void Domain_Of_An_Address(string address, string? domain)
        => DirectoryFields.DomainOf(address).ShouldBe(domain);
}
