using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// PUBLIC_RATE_LIMIT_PER_MINUTE, the per-IP cap on the whole /public group.
//
// /public reaches real work without a credential — QueryService, attachment
// payloads, and (with a jq_filter) a subprocess — so the cap is applied at the
// GROUP rather than per handler: "how many requests may one address make"
// should not depend on which route it lands on.
//
// Own factory, like AuthRateLimitTests: each WebApplicationFactory gets its own
// limiter state, so pinning a low budget here cannot starve other classes.
public sealed class PublicRateLimitTests : IClassFixture<PublicRateLimitTests.LowPublicLimitFactory>
{
    private readonly LowPublicLimitFactory _factory;
    public PublicRateLimitTests(LowPublicLimitFactory factory) => _factory = factory;

    [Fact]
    public async Task Public_Query_Past_The_Limit_Returns_429()
    {
        using var client = _factory.CreateClient();

        // The budget is 3. The body is deliberately junk: the limiter runs
        // BEFORE the handler, so the 4th request must be refused whatever the
        // first three answered.
        int? last = null;
        for (var i = 0; i < 4; i++)
        {
            using var resp = await client.PostAsync("/public/query",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            last = (int)resp.StatusCode;
        }

        last.ShouldBe(429);
    }

    [Fact]
    public async Task The_Rejection_Uses_The_Same_Failure_Envelope_As_Every_Other_Limit()
    {
        // Clients parse one shape for refusals. Pinned because the group-level
        // policy is new and reuses the shared OnRejected handler.
        using var client = _factory.CreateClient();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last?.Dispose();
            last = await client.PostAsync("/public/query",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        }

        last.ShouldNotBeNull();
        ((int)last!.StatusCode).ShouldBe(429);
        var payload = await last.Content.ReadAsStringAsync();
        last.Dispose();

        payload.ShouldContain("\"status\":\"failed\"");
        payload.ShouldContain("\"type\":\"rate_limit\"");
        payload.ShouldContain("\"code\":429");
    }

    [Fact]
    public async Task The_Authenticated_Surface_Is_Not_Caught_By_The_Public_Limit()
    {
        // The cap is on the anonymous group only. /managed shares the process
        // and the client's address, so a blown /public budget must not take the
        // authenticated API down with it -- 401 (no credential), never 429.
        using var client = _factory.CreateClient();

        for (var i = 0; i < 6; i++)
        {
            using var burn = await client.PostAsync("/public/query",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        }

        using var managed = await client.PostAsync("/managed/query",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        ((int)managed.StatusCode).ShouldNotBe(429);
    }

    [Fact]
    public async Task Zero_Disables_The_Limit_Entirely()
    {
        // 0 is a documented configuration, not just "very low". It matters
        // because the partition key is the peer address: behind a reverse proxy
        // with TrustedProxies unset, every visitor shares the proxy's address
        // and one bucket, and turning the cap off is the correct answer until
        // the proxy is configured. Uses GetNoLimiter, so this pins that the
        // no-limiter branch really is unlimited rather than a large limit.
        using var factory = new DisabledPublicLimitFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < 25; i++)
        {
            using var resp = await client.PostAsync("/public/query",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            ((int)resp.StatusCode).ShouldNotBe(429, $"request {i + 1} was throttled with the limit disabled");
        }
    }

    public sealed class DisabledPublicLimitFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");
            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Error));
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var overrides = new Dictionary<string, string?>
                {
                    ["Dmart:JwtSecret"] = "test-secret-test-secret-test-secret-32-bytes",
                    ["Dmart:JwtIssuer"] = "dmart",
                    ["Dmart:JwtAudience"] = "dmart",
                    ["Dmart:JwtAccessExpires"] = "300",
                    ["Dmart:AdminPassword"] = "admin-password-123",
                    ["Dmart:AdminEmail"] = "admin@test.local",
                    ["Dmart:AuthRateLimitPerMinute"] = "1000",
                    ["Dmart:PublicRateLimitPerMinute"] = "0",
                };
                DmartFactory.ApplyDriverOverrides(overrides);
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }

    public sealed class LowPublicLimitFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");
            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Error));
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var overrides = new Dictionary<string, string?>
                {
                    ["Dmart:JwtSecret"] = "test-secret-test-secret-test-secret-32-bytes",
                    ["Dmart:JwtIssuer"] = "dmart",
                    ["Dmart:JwtAudience"] = "dmart",
                    ["Dmart:JwtAccessExpires"] = "300",
                    ["Dmart:AdminPassword"] = "admin-password-123",
                    ["Dmart:AdminEmail"] = "admin@test.local",
                    ["Dmart:AuthRateLimitPerMinute"] = "1000",
                    // The one knob under test.
                    ["Dmart:PublicRateLimitPerMinute"] = "3",
                };
                DmartFactory.ApplyDriverOverrides(overrides);
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }
}
