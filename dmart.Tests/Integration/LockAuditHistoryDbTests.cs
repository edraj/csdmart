using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// lock/unlock must (a) write a history row recording the action (Python's
// store_entry_diff {lock_type}) and (b) fire the plugin after-action pipeline,
// observable as a .dm/events.jsonl audit line (PluginManager.AfterActionAsync →
// SpaceEventLogger). SpacesFolder is pointed at a per-test temp dir so the
// audit trail is isolated.
public sealed class LockAuditHistoryDbTests
{
    private const string Space = "test";

    private static Request CreateContent(string subpath, string shortname) => new()
    {
        RequestType = RequestType.Create,
        SpaceName = Space,
        Records = new()
        {
            new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = shortname,
                Attributes = new() { ["displayname"] = "lock audit probe" },
            },
        },
    };

    private static async Task<long> CountHistoryAsync(Db db, string shortname, string lockType)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM histories WHERE space_name = $1 AND shortname = $2 AND diff->>'lock_type' = $3",
            conn);
        cmd.Parameters.Add(new() { Value = Space });
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = lockType });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static bool AuditHasAction(string auditPath, string action, string shortname)
    {
        if (!File.Exists(auditPath)) return false;
        foreach (var line in File.ReadAllLines(auditPath))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("request").GetString() != action) continue;
            if (root.GetProperty("resource").GetProperty("shortname").GetString() != shortname) continue;
            return true;
        }
        return false;
    }

    [FactIfPg]
    public async Task Lock_Writes_History_Row_And_Fires_After_Hook()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dmart-lockaudit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var auditPath = Path.Combine(tempDir, Space, ".dm", "events.jsonl");

        using var factory = new LockAuditFactory(tempDir);
        await DmartFactory.ResetBootstrapAdminStateAsync(factory.Services);
        using var dmart = new DmartFactory();
        var user = await dmart.CreateLoggedInUserAsync(host: factory);
        var db = factory.Services.GetRequiredService<Db>();

        var subpath = "lockaudit";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);
        try
        {
            (await user.Client.PostAsJsonAsync("/managed/request", CreateContent(subpath, shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await user.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CountHistoryAsync(db, shortname, "lock")).ShouldBe(1);
            AuditHasAction(auditPath, "lock", shortname).ShouldBeTrue($"no 'lock' audit line at {auditPath}");
        }
        finally
        {
            await user.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}");
            await user.Client.PostAsJsonAsync("/managed/request",
                new Request { RequestType = RequestType.Delete, SpaceName = Space, Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = subpath, Shortname = shortname } } },
                DmartJsonContext.Default.Request);
            await user.Cleanup();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [FactIfPg]
    public async Task Unlock_Writes_History_Row_And_Fires_After_Hook()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dmart-lockaudit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var auditPath = Path.Combine(tempDir, Space, ".dm", "events.jsonl");

        using var factory = new LockAuditFactory(tempDir);
        await DmartFactory.ResetBootstrapAdminStateAsync(factory.Services);
        using var dmart = new DmartFactory();
        var user = await dmart.CreateLoggedInUserAsync(host: factory);
        var db = factory.Services.GetRequiredService<Db>();

        var subpath = "lockaudit";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);
        try
        {
            (await user.Client.PostAsJsonAsync("/managed/request", CreateContent(subpath, shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await user.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await user.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}"))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CountHistoryAsync(db, shortname, "cancel")).ShouldBe(1);
            AuditHasAction(auditPath, "unlock", shortname).ShouldBeTrue($"no 'unlock' audit line at {auditPath}");
        }
        finally
        {
            await user.Client.PostAsJsonAsync("/managed/request",
                new Request { RequestType = RequestType.Delete, SpaceName = Space, Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = subpath, Shortname = shortname } } },
                DmartJsonContext.Default.Request);
            await user.Cleanup();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // Dedicated factory so SpacesFolder is a per-test temp dir (mirrors
    // ProfileAfterHookFactory) — keeps the audit trail isolated and on the
    // shared Postgres for the history-row assertions.
    private sealed class LockAuditFactory : WebApplicationFactory<Program>
    {
        private readonly string _spacesFolder;
        public LockAuditFactory(string spacesFolder) => _spacesFolder = spacesFolder;

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
                if (!string.IsNullOrEmpty(DmartFactory.PgConn))
                {
                DmartFactory.ApplyDriverOverrides(overrides);
                    overrides["Dmart:DatabaseHost"] = null;
                    overrides["Dmart:DatabasePassword"] = null;
                    overrides["Dmart:DatabaseName"] = null;
                }
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }
}
