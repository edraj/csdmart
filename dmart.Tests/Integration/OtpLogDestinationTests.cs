using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// What POST /user/otp-request writes as `dest=` on its silent no-op branches.
//
// This is the only assertion on that line anywhere, and it is worth having
// because the value has been wrong three times in three different ways, none of
// which any other test could see: an opaque fingerprint nobody could use for
// support, the raw email spelling while the rate limits keyed on the lowercased
// one, and a blank `dest=` for input that stripped to nothing. Every one of
// those still answered 200 Ok and still minted or withheld the code correctly,
// so the whole suite stayed green.
//
// The rule being pinned: the logged destination is the one the resend cooldown,
// the daily cap and the otps row are keyed on. That is the only reason to log it
// — so that grepping for a destination explains a cap that tripped. A log that
// says something the budget does not is worse than no log.
//
// Both cases use an identifier that matches no user, so they land on the
// `unknown-user` branch without needing a fixture. The destination is resolved
// before that branch is reached, which is exactly the path under test.
public sealed class OtpLogDestinationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OtpLogDestinationTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Email_Is_Logged_Lowercased_So_It_Matches_The_Rate_Limit_Key()
    {
        var capture = new CaptureLoggerProvider();
        using var derived = _factory.WithWebHostBuilder(b => b.ConfigureLogging(l =>
        {
            l.AddProvider(capture);
            l.SetMinimumLevel(LogLevel.Information);
        }));

        var mixed = $"CaseTest{Guid.NewGuid():N}"[..20] + "@Example.COM";
        var resp = await derived.CreateClient().PostAsJsonAsync("/user/otp-request",
            new { purpose = "login", email = mixed });
        resp.EnsureSuccessStatusCode();

        var line = capture.Entries.LastOrDefault(e => e.Message.Contains("otp-request: silent no-op"));
        line.ShouldNotBeNull("the request should have hit a silent no-op branch and logged one line");

        // Compare the logged value ORDINALLY. Shouldly's ShouldContain and
        // ShouldNotContain are case-INSENSITIVE by default, which is precisely
        // the distinction under test here — a case-insensitive assertion passes
        // whether or not the lowercasing happened, so it would pin nothing.
        var i = line!.Message.IndexOf("dest=", StringComparison.Ordinal);
        i.ShouldBeGreaterThan(-1, $"no dest= in: {line.Message}");
        var logged = line.Message[(i + "dest=".Length)..].Trim();

        // The cooldown, the daily cap and the otps row are all keyed on the
        // lowercased address. An operator grepping that to explain a daily-cap
        // warning must find this request.
        logged.ShouldBe(mixed.ToLowerInvariant());
        logged.ShouldNotBe(mixed);
    }

    [FactIfPg]
    public async Task Blank_Identifier_Logs_None_Rather_Than_An_Empty_Dest()
    {
        var capture = new CaptureLoggerProvider();
        using var derived = _factory.WithWebHostBuilder(b => b.ConfigureLogging(l =>
        {
            l.AddProvider(capture);
            l.SetMinimumLevel(LogLevel.Information);
        }));
        var client = derived.CreateClient();

        // shortname is free-form and validated nowhere on this endpoint, so
        // each of these counts as "one identifier provided" and reaches the log.
        // Spaces are not control characters, which is why stripping alone is
        // not enough to keep the line unambiguous.
        foreach (var junk in new[] { "   ", " ", "" })
        {
            var resp = await client.PostAsJsonAsync("/user/otp-request",
                new { purpose = "login", shortname = junk });
            resp.EnsureSuccessStatusCode();
        }

        var lines = capture.Entries
            .Where(e => e.Message.Contains("otp-request: silent no-op"))
            .ToList();
        lines.ShouldNotBeEmpty("the blank-identifier requests should have logged silent no-ops");

        foreach (var e in lines)
        {
            var i = e.Message.IndexOf("dest=", StringComparison.Ordinal);
            if (i < 0) continue;
            var value = e.Message[(i + "dest=".Length)..];
            // A `dest=` that is empty or all blanks cannot be told from an
            // absent one, which is the ambiguity the (none) guard removes.
            string.IsNullOrWhiteSpace(value).ShouldBeFalse(
                $"logged a blank destination: '{e.Message}'");
        }
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
