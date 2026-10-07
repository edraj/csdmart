using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The after-action event for a user move must be keyed on the DESTINATION
// with the source in attributes, as EntryService.MoveAsync's is: a hook
// (webhook, realtime notifier, action_log) that got {shortname: <old name>}
// would be told about a record that no longer exists and could not learn the
// new one. Observed the same way ProfileAfterHookTests does — through the
// audit line PluginManager.AfterActionAsync writes unconditionally to the
// space's .dm/events.jsonl, with SpacesFolder pointed at a per-test directory.
public sealed class MoveUserAfterHookTests
{
    [FactIfPg]
    public async Task Move_User_After_Event_Names_The_Destination_And_The_Source()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dmart-moveaftertest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var auditPath = Path.Combine(tempDir, "management", ".dm", "events.jsonl");

        using var factory = new MoveAfterHookFactory(tempDir);
        await DmartFactory.ResetBootstrapAdminStateAsync(factory.Services);

        using var dmart = new DmartFactory();
        var admin = await dmart.CreateLoggedInUserAsync(host: factory);
        var target = await dmart.CreateTestUserAsync();
        var users = factory.Services.GetRequiredService<Dmart.DataAdapters.Sql.UserRepository>();
        var renamed = $"rn_{Guid.NewGuid():N}"[..16];
        try
        {
            var req = new Request
            {
                RequestType = RequestType.Move, SpaceName = "management",
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.User, Subpath = "users", Shortname = target.Shortname,
                        Attributes = new()
                        {
                            ["src_space_name"] = "management", ["src_subpath"] = "users", ["src_shortname"] = target.Shortname,
                            ["dest_space_name"] = "management", ["dest_subpath"] = "users", ["dest_shortname"] = renamed,
                        },
                    },
                },
            };
            var resp = await admin.Client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
            var body = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(), DmartJsonContext.Default.Response)!;
            body.Status.ShouldBe(Status.Success, JsonSerializer.Serialize(body, DmartJsonContext.Default.Response));

            string[] lines = Array.Empty<string>();
            await WaitFor.UntilAsync(() =>
            {
                if (File.Exists(auditPath)) lines = File.ReadAllLines(auditPath);
                return Task.FromResult(lines.Any(l => l.Contains("\"move\"", StringComparison.Ordinal)));
            }, TimeSpan.FromSeconds(2));

            JsonElement? moveLine = null;
            foreach (var line in lines)
            {
                var root = JsonDocument.Parse(line).RootElement;
                if (root.GetProperty("request").GetString() != "move") continue;
                if (root.GetProperty("user_shortname").GetString() != admin.Shortname) continue;
                moveLine = root.Clone();
                break;
            }
            moveLine.ShouldNotBeNull($"no move audit line for {admin.Shortname} in {lines.Length} lines");

            var resource = moveLine.Value.GetProperty("resource");
            resource.GetProperty("type").GetString().ShouldBe("user");
            resource.GetProperty("shortname").GetString().ShouldBe(renamed, "the event is keyed on the destination");
            var attrs = moveLine.Value.GetProperty("attributes");
            attrs.GetProperty("src_shortname").GetString().ShouldBe(target.Shortname, "and carries the source, like an entry move");
            attrs.GetProperty("src_subpath").GetString().ShouldBe("users");
        }
        finally
        {
            try { await users.DeleteAsync(renamed); } catch { }
            await target.Cleanup();
            await admin.Cleanup();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Same shape as ProfileAfterHookTests' factory: SpacesFolder is read at
    // singleton construction, so it cannot be flipped on the shared factory.
    private sealed class MoveAfterHookFactory : WebApplicationFactory<Program>
    {
        private readonly string _spacesFolder;
        public MoveAfterHookFactory(string spacesFolder) => _spacesFolder = spacesFolder;

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
                    ["Dmart:AdminPassword"] = "Test1234",
                    ["Dmart:AdminEmail"] = "admin@test.local",
                    ["Dmart:AuthRateLimitPerMinute"] = "1000",
                    ["Dmart:SpacesFolder"] = _spacesFolder,
                };
                DmartFactory.ApplyDriverOverrides(overrides);
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }
}
