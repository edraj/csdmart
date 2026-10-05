using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
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

    private static async Task<long> CountHistoryAsync(IDbConnectionFactory db, string shortname, string lockType)
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command("SELECT COUNT(*) FROM histories WHERE space_name = $1 AND shortname = $2 AND diff->>'lock_type' = $3");
        DbParams.Add(cmd, Space);
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, lockType);
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

    // The audited `resource` block for one action, or null when absent. The
    // lock/unlock event is built from a Locator, so these two fields are the
    // ones that go missing when the entry behind it isn't consulted.
    private static (string? Type, string? Schema)? AuditResource(
        string auditPath, string action, string shortname)
    {
        if (!File.Exists(auditPath)) return null;
        foreach (var line in File.ReadAllLines(auditPath))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("request").GetString() != action) continue;
            var res = root.GetProperty("resource");
            if (res.GetProperty("shortname").GetString() != shortname) continue;
            return (res.GetProperty("type").GetString(),
                    res.TryGetProperty("schema_shortname", out var sc) ? sc.GetString() : null);
        }
        return null;
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
        var db = factory.Services.GetRequiredService<IDbConnectionFactory>();

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
        var db = factory.Services.GetRequiredService<IDbConnectionFactory>();

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

    // A lock/unlock event must describe the resource it is about: its real
    // resource_type and its payload schema. Both were absent, and both matter
    // because plugin filters gate on them — PluginManager.MatchedFilters
    // rejects an event whose schema is null against a filter that lists
    // schemas, so a schema-filtered hook silently never observed lock or
    // unlock. The unlock route additionally carries no resource type at all
    // (LockHandler.cs hardwires `content`), so the type had to come from the
    // entry or be wrong for everything that is not content.
    //
    // A folder with a payload schema exercises both in one pass: the lock route
    // states the type, the unlock route does not, and neither knows the schema.
    // Seeded through the repository rather than the API so the probe needs no
    // registered schema entry to validate against (as ParquetExportTests does).
    [FactIfPg]
    public async Task Lock_And_Unlock_Events_Carry_Resource_Type_And_Schema()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dmart-lockaudit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var auditPath = Path.Combine(tempDir, Space, ".dm", "events.jsonl");

        using var factory = new LockAuditFactory(tempDir);
        await DmartFactory.ResetBootstrapAdminStateAsync(factory.Services);
        using var dmart = new DmartFactory();
        var user = await dmart.CreateLoggedInUserAsync(host: factory);
        var entries = factory.Services.GetRequiredService<EntryRepository>();

        var subpath = "lockaudit";
        var shortname = $"lkf_{Guid.NewGuid():N}".Substring(0, 12);
        try
        {
            await entries.UpsertAsync(new Entry
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = shortname,
                SpaceName = Space,
                Subpath = $"/{subpath}",
                ResourceType = ResourceType.Folder,
                IsActive = true,
                // Owned by the caller, so the lock gate authorizes on ownership
                // and the test asserts event shape rather than permissions.
                OwnerShortname = user.Shortname,
                Payload = new Payload
                {
                    ContentType = ContentType.Json,
                    SchemaShortname = "lock_probe",
                },
            });

            (await user.Client.PutAsync($"/managed/lock/folder/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await user.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}"))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var locked = AuditResource(auditPath, "lock", shortname);
            locked.ShouldNotBeNull($"no 'lock' audit line at {auditPath}");
            locked!.Value.Type.ShouldBe("folder");
            locked!.Value.Schema.ShouldBe("lock_probe",
                "the lock event dropped the entry's payload schema, so a schema-filtered hook cannot match it");

            // The unlock route has no resource_type segment: `folder` here can
            // only have come from the entry, not from the request.
            var unlocked = AuditResource(auditPath, "unlock", shortname);
            unlocked.ShouldNotBeNull($"no 'unlock' audit line at {auditPath}");
            unlocked!.Value.Type.ShouldBe("folder",
                "the self-unlock event reported the route's hardcoded `content` instead of the entry's type");
            unlocked!.Value.Schema.ShouldBe("lock_probe",
                "the self-unlock event dropped the entry's payload schema");
        }
        finally
        {
            await user.Client.PostAsJsonAsync("/managed/request",
                new Request { RequestType = RequestType.Delete, SpaceName = Space, Records = new() { new Record { ResourceType = ResourceType.Folder, Subpath = subpath, Shortname = shortname } } },
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
                DmartFactory.ApplyDriverOverrides(overrides);
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }
}
