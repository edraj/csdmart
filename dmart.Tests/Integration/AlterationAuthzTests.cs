using Dmart.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// POST /managed/apply-alteration/{space}/{name} executes a saved target_query
// on behalf of the caller. It used to run that query through the repository's
// actor-less (server-unrestricted) overload, unclamped and unconstrained to the
// route's space, then echo the shortname/subpath of every row the caller was
// DENIED on — a cross-space enumeration and payload-value oracle for anyone
// who could create an alteration anywhere. Now: the alteration is loaded
// read-gated, the query is pinned to the alteration's space and executed via
// QueryService (caller's row ACL + MaxQueryLimit), so `matched` and `failed`
// can only ever name rows the caller could already read.
public sealed class AlterationAuthzTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public AlterationAuthzTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task ReadOnly_Caller_Sees_Only_Readable_Rows_And_Updates_Nothing()
    {
        await using var w = await Seed(targetSpaceIsOwnSpace: true);

        var resp = await w.User.Client.PostAsync($"/managed/apply-alteration/{w.Space}/alt", null);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);

        // The caller can read the two /items rows (view+query grant) but holds no
        // update: both are matched, neither is updated, and each failure is a
        // NOT_ALLOWED for a row the caller could legitimately see.
        ((JsonElement)body.Attributes!["matched"]).GetInt32().ShouldBe(2);
        ((JsonElement)body.Attributes["updated"]).GetInt32().ShouldBe(0);
        var failed = (JsonElement)body.Attributes["failed"];
        failed.GetArrayLength().ShouldBe(2);
        foreach (var f in failed.EnumerateArray())
            f.GetProperty("code").GetInt32().ShouldBe(InternalErrorCode.NOT_ALLOWED);

        // Nothing the caller cannot read may surface: the /secret row is absent.
        failed.EnumerateArray().Select(f => f.GetProperty("shortname").GetString())
            .ShouldNotContain("hidden");
    }

    [FactIfPg]
    public async Task Alteration_Targeting_Another_Space_Is_Refused()
    {
        await using var w = await Seed(targetSpaceIsOwnSpace: false);

        var resp = await w.User.Client.PostAsync($"/managed/apply-alteration/{w.Space}/alt", null);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error!.Code.ShouldBe(InternalErrorCode.NOT_ALLOWED,
            "a saved query must not reach across spaces on the caller's behalf");
    }

    [FactIfPg]
    public async Task Caller_Without_Read_On_The_Alteration_Cannot_Execute_It()
    {
        await using var w = await Seed(targetSpaceIsOwnSpace: true);
        var stranger = await _factory.CreateLoggedInUserAsync(roles: new());

        var resp = await stranger.Client.PostAsync($"/managed/apply-alteration/{w.Space}/alt", null);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error!.Code.ShouldBe(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST,
            "an alteration the caller cannot read must be indistinguishable from an absent one");
    }

    // ---- seeding ---------------------------------------------------------

    private async Task<Harness> Seed(bool targetSpaceIsOwnSpace)
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        var access = sp.GetRequiredService<AccessRepository>();
        var entries = sp.GetRequiredService<EntryRepository>();

        var space = $"itest_alt_{Guid.NewGuid():N}"[..20];
        var other = $"itest_alo_{Guid.NewGuid():N}"[..20];
        var role = $"itest_alt_role_{Guid.NewGuid():N}"[..24];
        var perm = $"itest_alt_perm_{Guid.NewGuid():N}"[..24];

        // Two readable rows and one the grant's is_active condition excludes —
        // all under the SAME subpath, so `hidden` is dropped by the caller's
        // row ACL, not by query scope. The unrestricted query used to return it.
        await UpsertEntry(entries, space, "/items", "a");
        await UpsertEntry(entries, space, "/items", "b");
        await UpsertEntry(entries, space, "/items", "hidden", isActive: false);

        // The alteration itself, as an Alteration entry under /alterations.
        var target = targetSpaceIsOwnSpace ? space : other;
        var altBody = JsonDocument.Parse(
            $"{{\"target_query\":{{\"type\":\"search\",\"space_name\":\"{target}\",\"subpath\":\"/items\",\"limit\":100}}," +
            "\"patch\":{\"description\":{\"en\":\"patched\"}}}").RootElement.Clone();
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = "alt", SpaceName = space, Subpath = "/alterations",
            OwnerShortname = "dmart", ResourceType = ResourceType.Alteration, IsActive = true,
            Payload = new Payload { ContentType = ContentType.Json, Body = altBody },
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });

        // Grant: view+query (no update) on /items and /alterations of `space` only.
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = perm, SpaceName = "management", Subpath = "/permissions",
            OwnerShortname = "dmart", IsActive = true,
            Subpaths = new() { [space] = new() { "/items", "/alterations" } },
            ResourceTypes = new() { "content", "alteration" },
            Actions = new() { "view", "query" },
            Conditions = new() { "is_active" },
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = role, SpaceName = "management", Subpath = "roles",
            OwnerShortname = "dmart", IsActive = true, Permissions = new() { perm },
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });
        await access.InvalidateAllCachesAsync();

        var user = await _factory.CreateLoggedInUserAsync(roles: new() { role });
        return new Harness { Space = space, User = user, Access = access, Role = role, Perm = perm };
    }

    private static Task UpsertEntry(
        EntryRepository entries, string space, string subpath, string shortname, bool isActive = true)
        => entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = shortname, SpaceName = space, Subpath = subpath,
            OwnerShortname = "dmart", ResourceType = ResourceType.Content, IsActive = isActive,
            Payload = new Payload { ContentType = ContentType.Json, Body = JsonDocument.Parse("{\"v\":1}").RootElement.Clone() },
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });

    private sealed class Harness : IAsyncDisposable
    {
        public required string Space { get; init; }
        public required DmartFactory.TestUser User { get; init; }
        public required AccessRepository Access { get; init; }
        public required string Role { get; init; }
        public required string Perm { get; init; }

        public async ValueTask DisposeAsync()
        {
            try { await Access.DeleteRoleAsync(Role); } catch { }
            try { await Access.DeletePermissionAsync(Perm); } catch { }
            await Access.InvalidateAllCachesAsync();
            User.Client.Dispose();
        }
    }
}
