using Dmart.Config;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Unit tests for the REGISTRATION_ENABLED_CHANNELS CSV parsing on
// DmartSettings — trimming, case-insensitivity, and the empty-string form
// (which UserService.CreateAsync treats as "self-registration disabled").
public class RegistrationChannelsSettingsTests
{
    [Fact]
    public void Default_Enables_Both_Channels()
    {
        var s = new DmartSettings();
        s.IsRegistrationChannelEnabled("email").ShouldBeTrue();
        s.IsRegistrationChannelEnabled("msisdn").ShouldBeTrue();
    }

    [Theory]
    [InlineData("email,msisdn")]
    [InlineData(" EMAIL , Msisdn ")]
    [InlineData("Email,MSISDN,")]
    public void Parsing_Trims_And_Lowercases(string csv)
    {
        var s = new DmartSettings { RegistrationEnabledChannels = csv };
        s.ParseRegistrationEnabledChannels().ShouldBe(new[] { "email", "msisdn" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void Empty_Forms_Disable_All_Channels(string csv)
    {
        var s = new DmartSettings { RegistrationEnabledChannels = csv };
        s.ParseRegistrationEnabledChannels().ShouldBeEmpty();
        s.IsRegistrationChannelEnabled("email").ShouldBeFalse();
        s.IsRegistrationChannelEnabled("msisdn").ShouldBeFalse();
    }

    [Fact]
    public void Single_Channel_Only_Enables_That_Channel()
    {
        var s = new DmartSettings { RegistrationEnabledChannels = "msisdn" };
        s.IsRegistrationChannelEnabled("msisdn").ShouldBeTrue();
        s.IsRegistrationChannelEnabled("email").ShouldBeFalse();
    }

    [Fact]
    public void Channel_Lookup_Is_Case_Insensitive()
    {
        var s = new DmartSettings { RegistrationEnabledChannels = "email" };
        s.IsRegistrationChannelEnabled("EMAIL").ShouldBeTrue();
        s.IsRegistrationChannelEnabled("Email").ShouldBeTrue();
    }
}
