using System.Net;
using Dmart.Config;
using Dmart.Ldap;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Ldap;

public class LdapBindGuardTests
{
    private static LdapBindGuard Guard(string trusted = "127.0.0.0/8,::1", int perMinute = 3)
        => new(Options.Create(new DmartSettings { LdapTrustedPeers = trusted, AuthRateLimitPerMinute = perMinute }));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.9.9.9", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]    // IPv4 on a dual-stack listener
    [InlineData("10.77.0.1", true)]           // a single address listed
    [InlineData("10.77.0.2", false)]
    [InlineData("192.168.1.10", false)]
    public void Trusted_Peers_Are_Addresses_Or_Ranges(string peer, bool trusted)
    {
        using var g = Guard("127.0.0.0/8,::1,10.77.0.1");
        g.IsTrusted(IPAddress.Parse(peer)).ShouldBe(trusted);
    }

    [Fact]
    public void An_Empty_List_Trusts_Nobody_Not_Even_Loopback()
    {
        using var g = Guard("");
        g.IsTrusted(IPAddress.Loopback).ShouldBeFalse();
        g.IsTrusted(null).ShouldBeFalse();
    }

    [Fact]
    public void Failed_Binds_Use_Up_An_Untrusted_Address_Budget()
    {
        using var g = Guard(perMinute: 3);
        var attacker = IPAddress.Parse("203.0.113.7");
        var neighbour = IPAddress.Parse("203.0.113.8");
        for (var i = 0; i < 3; i++)
        {
            g.IsThrottled(attacker).ShouldBeFalse();
            g.RecordFailure(attacker);
        }
        g.IsThrottled(attacker).ShouldBeTrue();
        g.IsThrottled(IPAddress.Parse("::ffff:203.0.113.7")).ShouldBeTrue("the mapped form is the same client");
        g.IsThrottled(neighbour).ShouldBeFalse();
    }

    [Fact]
    public void Trusted_Peers_Are_Never_Throttled()
    {
        using var g = Guard(perMinute: 1);
        for (var i = 0; i < 10; i++) g.RecordFailure(IPAddress.Loopback);
        g.IsThrottled(IPAddress.Loopback).ShouldBeFalse();
    }

    [Fact]
    public void The_Same_Wrong_Password_Is_Recognised_And_A_Different_One_Is_Not()
    {
        using var g = Guard();
        g.IsRepeatedFailure("alice", "Old12345").ShouldBeFalse();
        g.RememberFailure("alice", "Old12345");
        g.IsRepeatedFailure("alice", "Old12345").ShouldBeTrue();
        g.IsRepeatedFailure("alice", "Other12345").ShouldBeFalse();
        g.IsRepeatedFailure("bob", "Old12345").ShouldBeFalse("fingerprints are per account");
    }

    [Fact]
    public void The_Last_Two_Failures_Are_Kept_And_A_Success_Forgets_Them()
    {
        using var g = Guard();
        g.RememberFailure("alice", "One12345");
        g.RememberFailure("alice", "Two12345");
        g.IsRepeatedFailure("alice", "One12345").ShouldBeTrue();
        g.RememberFailure("alice", "Three12345");
        g.IsRepeatedFailure("alice", "One12345").ShouldBeFalse();
        g.IsRepeatedFailure("alice", "Two12345").ShouldBeTrue();

        g.Forget("alice");
        g.IsRepeatedFailure("alice", "Two12345").ShouldBeFalse();
    }
}
