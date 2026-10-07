using Dmart.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// V-06 regression: /public/query type=attachments and type=history must honour
// the PARENT entry's ACL, not hand an anonymous caller every attachment/history
// row in the space.
//
// The hole: an attachment/history row carries no usable ACL of its own
// (histories has no acl column at all), so the row-level filter was skipped
// entirely — an anonymous caller at the tree root saw the metadata of every
// entry in the space, including ones it could never read.
//
// Setup: a world permission over all subpaths, gated on is_active. Two entries
// live side by side under /items — `pub` (is_active=true, world-readable) and
// `prot` (is_active=false, NOT world-readable). Each has an attachment and both
// an entry-history and an attachment-history row. Querying at the tree root,
// anonymous must see pub's metadata and NOT prot's.
//
// Runs on PostgreSQL and (via DMART_TEST_DRIVER=sqlite) SQLite, so the
// cross-engine parent-split SQL is exercised on both legs.
[Collection(AnonymousWorldCollection.Name)]
public sealed class PublicQueryAttachmentHistoryAclTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PublicQueryAttachmentHistoryAclTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task PublicQuery_Anonymous_Attachments_Honour_Parent_Entry_Acl()
    {
        await using var w = await Seed();

        var body = (await Post(w, QueryType.Attachments))!;
        var shortnames = (body.Records ?? new()).Select(r => r.Shortname).ToList();

        shortnames.ShouldContain("pubatt",
            "the attachment of a world-readable entry must stay visible to anonymous");
        shortnames.ShouldNotContain("protatt",
            "the attachment of a non-readable entry must NOT leak to anonymous (V-06)");
        ((JsonElement)body.Attributes!["total"]).GetInt32().ShouldBe(1,
            "the count must match the ACL-filtered set, not the whole space");
    }

    [FactIfPg]
    public async Task PublicQuery_Anonymous_History_Honours_Parent_Entry_Acl()
    {
        await using var w = await Seed();

        var body = (await Post(w, QueryType.History))!;
        var shortnames = (body.Records ?? new()).Select(r => r.Shortname).ToList();

        // Both the entry-history row (pub) and the attachment-history row
        // (pubatt) hang off a world-readable entry and must remain visible.
        shortnames.ShouldContain("pub", "entry-history of a readable entry must stay visible");
        shortnames.ShouldContain("pubatt", "attachment-history of a readable entry must stay visible");
        // Neither the protected entry's history nor its attachment's may leak.
        shortnames.ShouldNotContain("prot", "entry-history of a non-readable entry must NOT leak (V-06)");
        shortnames.ShouldNotContain("protatt", "attachment-history of a non-readable entry must NOT leak (V-06)");
        ((JsonElement)body.Attributes!["total"]).GetInt32().ShouldBe(2,
            "the count must match the ACL-filtered set, not the whole space");
    }

    private static async Task<Response?> Post(Harness w, QueryType type)
    {
        var resp = await w.Client.PostAsJsonAsync("/public/query",
            new Query
            {
                Type = type,
                SpaceName = w.Space,
                Subpath = "/",            // tree root — the reported leak vector
                FilterSchemaNames = new(),
                Limit = 50,
                RetrieveTotal = true,
            },
            DmartJsonContext.Default.Query);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        return body;
    }

    // ---- shared seeding -------------------------------------------------

    private async Task<Harness> Seed()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        var users = sp.GetRequiredService<UserRepository>();
        var access = sp.GetRequiredService<AccessRepository>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var attachments = sp.GetRequiredService<AttachmentRepository>();
        var history = sp.GetRequiredService<HistoryRepository>();

        const string anonUser = "anonymous";
        const string worldPerm = "world";
        var anonRole = $"itest_ahacl_role_{Guid.NewGuid():N}"[..24];
        var space = $"itest_ahacl_{Guid.NewGuid():N}"[..20];

        var priorAnon = await users.GetByShortnameAsync(anonUser);
        var priorWorld = await access.GetPermissionAsync(worldPerm);

        // World over ALL subpaths, gated on is_active. At the tree root this
        // yields the policy that matches is_active=true rows only, so `pub`
        // resolves and `prot` (is_active=false) does not.
        await WorldPermissionFixture.UpsertAsync(access, priorWorld,
            subpaths: new() { [space] = new() { PermissionService.AllSubpathsMw } },
            actions: new() { "view", "query" },
            resourceTypes: new() { "content" },
            conditions: new() { "is_active" });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = anonRole,
            SpaceName = "management",
            Subpath = "roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { worldPerm },
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        });
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = anonUser,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = anonUser,
            IsActive = true,
            Roles = new() { anonRole },
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        });

        // pub: world-readable (is_active). prot: present but is_active=false, so
        // the is_active-gated world policy does not resolve it.
        await UpsertEntry(entries, space, "/items", "pub", isActive: true);
        await UpsertEntry(entries, space, "/items", "prot", isActive: false);

        // One attachment per entry. An attachment's subpath is
        // "<parent subpath>/<parent shortname>".
        var pubAtt = await UpsertAttachment(attachments, space, "/items/pub", "pubatt");
        var protAtt = await UpsertAttachment(attachments, space, "/items/prot", "protatt");

        // Entry-history (coords = the entry's) and attachment-history
        // (coords = the attachment's) for each entry.
        var diff = new Dictionary<string, object> { ["note"] = "seed" };
        await history.AppendAsync(space, "/items", "pub", "dmart", null, diff);
        await history.AppendAsync(space, "/items/pub", "pubatt", "dmart", null, diff);
        await history.AppendAsync(space, "/items", "prot", "dmart", null, diff);
        await history.AppendAsync(space, "/items/prot", "protatt", "dmart", null, diff);

        await access.InvalidateAllCachesAsync();

        return new Harness
        {
            Client = _factory.CreateClient(),
            Space = space,
            Users = users,
            Access = access,
            Entries = entries,
            Attachments = attachments,
            AnonUser = anonUser,
            AnonRole = anonRole,
            WorldPerm = worldPerm,
            PriorAnon = priorAnon,
            PriorWorld = priorWorld,
            AttachmentUuids = new() { pubAtt, protAtt },
        };
    }

    private static Task UpsertEntry(
        EntryRepository entries, string space, string subpath, string shortname, bool isActive)
        => entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            OwnerShortname = "dmart",
            ResourceType = ResourceType.Content,
            IsActive = isActive,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse("{\"rank\":1}").RootElement.Clone(),
            },
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        });

    private static async Task<Guid> UpsertAttachment(
        AttachmentRepository attachments, string space, string subpath, string shortname)
    {
        var uuid = Guid.NewGuid();
        await attachments.UpsertAsync(new Attachment
        {
            Uuid = uuid.ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            OwnerShortname = "dmart",
            ResourceType = ResourceType.Media,
            IsActive = true,
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        });
        return uuid;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required HttpClient Client { get; init; }
        public required string Space { get; init; }
        public required UserRepository Users { get; init; }
        public required AccessRepository Access { get; init; }
        public required EntryRepository Entries { get; init; }
        public required AttachmentRepository Attachments { get; init; }
        public required string AnonUser { get; init; }
        public required string AnonRole { get; init; }
        public required string WorldPerm { get; init; }
        public required User? PriorAnon { get; init; }
        public required Permission? PriorWorld { get; init; }
        public required List<Guid> AttachmentUuids { get; init; }

        public async ValueTask DisposeAsync()
        {
            foreach (var uuid in AttachmentUuids)
                try { await Attachments.DeleteAsync(uuid); } catch { }
            try { await Entries.DeleteAsync(Space, "/items", "pub", ResourceType.Content); } catch { }
            try { await Entries.DeleteAsync(Space, "/items", "prot", ResourceType.Content); } catch { }
            // Restore the shared anonymous/world rows the same way WorldScopeHarness does.
            var deleted = false;
            try { await Users.DeleteAsync(AnonUser); deleted = true; } catch { }
            if (!deleted)
            {
                var current = await Users.GetByShortnameAsync(AnonUser);
                if (current is not null)
                    await Users.UpsertAsync(current with { Roles = new(), Groups = new() });
            }
            try { await Access.DeleteRoleAsync(AnonRole); } catch { }
            try { await Access.DeletePermissionAsync(WorldPerm); } catch { }
            if (PriorAnon is not null) await Users.UpsertAsync(PriorAnon);
            if (PriorWorld is not null) await Access.UpsertPermissionAsync(PriorWorld);
            await Access.InvalidateAllCachesAsync();
            Client.Dispose();
        }
    }
}
