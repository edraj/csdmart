using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Covers the three behaviours added on top of the bare lock/unlock round-trip:
//   1. Enforcement — a live lock blocks update/delete by ANY other user.
//   2. Owner-extend — the lock holder can re-acquire (refresh) their own lock.
//   3. Read surfacing — RetrieveLockStatus attaches the lock owner to reads.
public class LockEnforcementDbTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public LockEnforcementDbTests(DmartFactory factory) => _factory = factory;

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
                Attributes = new() { ["displayname"] = "lock enforcement probe" },
            },
        },
    };

    private static Request UpdateContent(string subpath, string shortname, string displayname) => new()
    {
        RequestType = RequestType.Update,
        SpaceName = Space,
        Records = new()
        {
            new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = shortname,
                Attributes = new() { ["displayname"] = displayname },
            },
        },
    };

    private static Request DeleteContent(string subpath, string shortname) => new()
    {
        RequestType = RequestType.Delete,
        SpaceName = Space,
        Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = subpath, Shortname = shortname } },
    };

    [FactIfPg]
    public async Task Update_By_NonOwner_Is_Blocked_While_Locked()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var other = await _factory.CreateLoggedInUserAsync();
        var subpath = "lockenf";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(subpath, shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await owner.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // A different authenticated user (also super_admin) must be refused.
            var resp = await other.Client.PostAsJsonAsync("/managed/request", UpdateContent(subpath, shortname, "intruder edit"), DmartJsonContext.Default.Request);
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // aggregate per-record failure
            var bodyText = await resp.Content.ReadAsStringAsync();
            bodyText.ShouldContain("This entry is locked");
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}");
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(subpath, shortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
            await other.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Delete_By_NonOwner_Is_Blocked_While_Locked()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var other = await _factory.CreateLoggedInUserAsync();
        var subpath = "lockenf";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(subpath, shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await owner.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var resp = await other.Client.PostAsJsonAsync("/managed/request", DeleteContent(subpath, shortname), DmartJsonContext.Default.Request);
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var bodyText = await resp.Content.ReadAsStringAsync();
            bodyText.ShouldContain("This entry is locked");
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}");
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(subpath, shortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
            await other.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Owner_Can_Update_Own_Locked_Entry()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var subpath = "lockenf";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(subpath, shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await owner.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // The holder is never blocked by their own lock.
            (await owner.Client.PostAsJsonAsync("/managed/request", UpdateContent(subpath, shortname, "owner edit"), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}");
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(subpath, shortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Re_Lock_By_Owner_Succeeds_To_Extend()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var subpath = "lockext";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            var first = await owner.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null);
            first.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Re-locking a still-valid lock as the SAME owner refreshes the TTL
            // and returns success (LockAction.extend in the Python reference) —
            // it must NOT 423 "already locked by yourself".
            var second = await owner.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null);
            second.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}");
            await owner.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Query_With_RetrieveLockStatus_Surfaces_Lock_Owner()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var subpath = "locksurf";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(subpath, shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await owner.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var query = new Query
            {
                Type = QueryType.Search,
                SpaceName = Space,
                Subpath = subpath,
                ExactSubpath = true,
                FilterShortnames = new() { shortname },
                RetrieveLockStatus = true,
                Limit = 10,
            };
            var resp = await owner.Client.PostAsJsonAsync("/managed/query", query, DmartJsonContext.Default.Query);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            var rec = body!.Records!.ShouldHaveSingleItem();
            rec.Attributes.ShouldNotBeNull();
            rec.Attributes!.ShouldContainKey("locked");
            var locked = (JsonElement)rec.Attributes!["locked"];
            locked.GetProperty("owner_shortname").GetString().ShouldBe(owner.Shortname);
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}");
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(subpath, shortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Query_Without_RetrieveLockStatus_Omits_Lock()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var subpath = "locksurf";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(subpath, shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await owner.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var query = new Query
            {
                Type = QueryType.Search,
                SpaceName = Space,
                Subpath = subpath,
                ExactSubpath = true,
                FilterShortnames = new() { shortname },
                Limit = 10,
            };
            var resp = await owner.Client.PostAsJsonAsync("/managed/query", query, DmartJsonContext.Default.Query);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            var rec = body!.Records!.ShouldHaveSingleItem();
            (rec.Attributes is null || !rec.Attributes.ContainsKey("locked")).ShouldBeTrue();
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}");
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(subpath, shortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
        }
    }

    private static Request MoveContent(string subpath, string shortname, string destSubpath, string destShortname) => new()
    {
        RequestType = RequestType.Move,
        SpaceName = Space,
        Records = new()
        {
            new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = shortname,
                Attributes = new()
                {
                    ["dest_subpath"] = destSubpath,
                    ["dest_shortname"] = destShortname,
                },
            },
        },
    };

    [FactIfPg]
    public async Task Move_By_NonOwner_Is_Blocked_While_Locked()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var other = await _factory.CreateLoggedInUserAsync();
        var subpath = "lockmove";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(subpath, shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await owner.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // Move is an update-class mutation: the same lock gate as
            // update/delete applies — otherwise any editor could relocate a
            // locked entry out from under its holder.
            var resp = await other.Client.PostAsJsonAsync("/managed/request",
                MoveContent(subpath, shortname, subpath, $"{shortname}_mv"), DmartJsonContext.Default.Request);
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await resp.Content.ReadAsStringAsync()).ShouldContain("This entry is locked");
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}");
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(subpath, shortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
            await other.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Move_By_Holder_Relocates_Lock()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var subpath = "lockmove";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);
        var movedShortname = $"{shortname}_mv";

        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(subpath, shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await owner.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // The holder may move their own locked entry; the lock row moves
            // with it so the lease keeps guarding the entry at its new path.
            (await owner.Client.PostAsJsonAsync("/managed/request",
                MoveContent(subpath, shortname, subpath, movedShortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var query = new Query
            {
                Type = QueryType.Search,
                SpaceName = Space,
                Subpath = subpath,
                ExactSubpath = true,
                FilterShortnames = new() { movedShortname },
                RetrieveLockStatus = true,
                Limit = 10,
            };
            var resp = await owner.Client.PostAsJsonAsync("/managed/query", query, DmartJsonContext.Default.Query);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            var rec = body!.Records!.ShouldHaveSingleItem();
            rec.Attributes.ShouldNotBeNull();
            rec.Attributes!.ShouldContainKey("locked");
            var locked = (JsonElement)rec.Attributes!["locked"];
            locked.GetProperty("owner_shortname").GetString().ShouldBe(owner.Shortname);
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{movedShortname}");
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(subpath, movedShortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Entry_Get_With_RetrieveLockStatus_Surfaces_Lock_Owner()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var subpath = "locksurf";
        var shortname = $"lk_{Guid.NewGuid():N}".Substring(0, 12);

        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(subpath, shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await owner.Client.PutAsync($"/managed/lock/content/{Space}/{subpath}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var resp = await owner.Client.GetAsync($"/managed/entry/content/{Space}/{subpath}/{shortname}?retrieve_lock_status=true");
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var bodyText = await resp.Content.ReadAsStringAsync();
            bodyText.ShouldContain("\"locked\"");
            bodyText.ShouldContain(owner.Shortname);
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{subpath}/{shortname}");
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(subpath, shortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
        }
    }
}
