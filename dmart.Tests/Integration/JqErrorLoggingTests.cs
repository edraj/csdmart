using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dmart.Models.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Since V-20 a failing jq_filter answers with a generic "jq_filter failed to
// evaluate" instead of jq's raw stderr, so the cause has to be recoverable from
// the server log. Both jq paths must log it, tagged with the request's
// X-Correlation-ID so an operator can find the line from a client's report:
//   1. the top-level jq_filter (JqEnvelope, shared by /public/query,
//      /managed/query and the saved-query execute routes);
//   2. a join sub-query's jq_filter (QueryService's client-join path).
public sealed class JqErrorLoggingTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public JqErrorLoggingTests(DmartFactory factory) => _factory = factory;

    // jq cannot compile this, so it fails on any input — including the empty
    // result set an anonymous caller gets back.
    private const string BrokenFilter = ".[syntax error";

    private const string UsersQuery = """
        "type": "search", "space_name": "management", "subpath": "/users",
        "filter_types": ["user"], "search": "@shortname:dmart", "limit": 5
        """;

    [FactIfPg]
    public async Task Top_Level_JqFilter_Failure_Logs_Stderr_With_Correlation_Id()
    {
        var capture = new CaptureLoggerProvider();
        using var derived = WithCapture(capture);
        var cid = $"jqlog-{Guid.NewGuid():N}";

        var body = $$"""{ {{UsersQuery}}, "jq_filter": "{{BrokenFilter}}" }""";
        var raw = await PostAsync(derived.CreateClient(), "/public/query", body, cid, token: null);

        AssertGenericJqError(raw);
        AssertLoggedJqStderr(capture, cid);
    }

    [FactIfPg]
    public async Task Join_JqFilter_Failure_Logs_Stderr_With_Correlation_Id()
    {
        var capture = new CaptureLoggerProvider();
        using var derived = WithCapture(capture);
        var client = derived.CreateClient();
        var cid = $"jqlog-{Guid.NewGuid():N}";

        // Base and sub-query both resolve the admin user, so the join has a
        // match to hand jq and the sub-query's filter actually runs.
        var body = $$"""
            {
              {{UsersQuery}},
              "join": [{
                "join_on": "shortname:shortname", "alias": "self",
                "query": { {{UsersQuery}}, "jq_filter": "{{BrokenFilter}}" }
              }]
            }
            """;
        var raw = await PostAsync(client, "/managed/query", body, cid, await AdminTokenAsync(client));

        AssertGenericJqError(raw);
        AssertLoggedJqStderr(capture, cid);
    }

    private WebApplicationFactory<Program> WithCapture(CaptureLoggerProvider capture) =>
        _factory.WithWebHostBuilder(b => b.ConfigureLogging(l =>
        {
            l.AddProvider(capture);
            l.SetMinimumLevel(LogLevel.Information);
        }));

    private async Task<string> AdminTokenAsync(HttpClient client)
    {
        var raw = await (await client.PostAsync("/user/login", new StringContent(
            $$"""{"shortname":"{{_factory.AdminShortname}}","password":"{{_factory.AdminPassword}}"}""",
            Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var token = doc.RootElement.GetProperty("records")[0]
            .GetProperty("attributes").GetProperty("access_token").GetString();
        token.ShouldNotBeNullOrEmpty($"login failed: {raw}");
        return token!;
    }

    private static async Task<string> PostAsync(
        HttpClient client, string path, string body, string correlationId, string? token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Correlation-ID", correlationId);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await client.SendAsync(req);
        return await resp.Content.ReadAsStringAsync();
    }

    // Proves the request reached jq and failed there — and that the caller
    // still gets only the generic message, not the stderr being logged.
    private static void AssertGenericJqError(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().ShouldBe(InternalErrorCode.JQ_ERROR, raw);
        error.GetProperty("message").GetString().ShouldBe("jq_filter failed to evaluate", raw);
    }

    private static void AssertLoggedJqStderr(CaptureLoggerProvider capture, string correlationId)
    {
        var lines = capture.Entries
            .Where(e => e.Message.Contains(correlationId, StringComparison.Ordinal)
                        && e.Message.Contains("jq_filter", StringComparison.Ordinal))
            .ToList();
        var line = lines.ShouldHaveSingleItem("expected one jq failure line carrying the correlation id");
        line.Level.ShouldBe(LogLevel.Warning);
        // jq's own compile-error text, which the response deliberately withholds.
        line.Message.ShouldContain("syntax error", Case.Sensitive);
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public sealed record Entry(string Category, LogLevel Level, string Message);
        public System.Collections.Concurrent.ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class CaptureLogger(
            string category, System.Collections.Concurrent.ConcurrentQueue<Entry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(new Entry(category, logLevel, formatter(state, exception)));
        }
    }
}
