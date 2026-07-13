using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.SqlAdapter;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Behavioral coverage for the Dmart.SqlAdapter SDK — the first tests in the
// repo that actually instantiate DmartSqlAdapter (the Unit/SqlAdapter tests
// only exercise static helpers). Pins the 0.10.0 IDmartData surface:
//
//   * write methods return a Response whose Records[].Attributes values are
//     JsonElement — the same CLR shapes an HTTP-client-deserialized Response
//     carries, so casts behave identically across backends;
//   * failures throw the shared typed hierarchy (DmartConflictException /
//     DmartNotFoundException), not InvalidOperationException;
//   * GetProfileAsync returns the REDACTED profile (Password == null) with
//     the payload column hydrated.
//
// The adapter talks to the same test database DmartFactory initializes
// (schema + bootstrap admin), constructed WITHOUT the RBAC engine — RBAC
// has its own unit coverage; these tests pin data-shape semantics.
public sealed class DmartSqlAdapterConformanceTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public DmartSqlAdapterConformanceTests(DmartFactory factory) => _factory = factory;

    private static DmartSqlAdapter MakeAdapter() => new(new DmartDb(DmartFactory.PgConn!));

    // Owned by the bootstrap admin ("dmart") so the entries.owner_shortname
    // FK resolves without seeding a user.
    private static Entry NewEntry(string shortname) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/sdk_adapter_tests",
        ResourceType = ResourceType.Content,
        OwnerShortname = "dmart",
        IsActive = true,
        Tags = new List<string> { "sdk", "conformance" },
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Locator LocatorFor(Entry e) =>
        new(e.ResourceType, e.SpaceName, e.Subpath, e.Shortname);

    [FactIfPg]
    public async Task CreateAsync_Returns_Response_With_JsonElement_Attribute_Values()
    {
        var adapter = MakeAdapter();
        var entry = NewEntry($"sdkc_{Guid.NewGuid():N}"[..16]);
        try
        {
            var resp = await adapter.CreateAsync(entry);

            resp.Status.ShouldBe(Status.Success);
            resp.Records.ShouldNotBeNull();
            var rec = resp.Records![0];
            rec.Shortname.ShouldBe(entry.Shortname);
            // Record.Subpath normalizes to the WIRE form (no leading slash),
            // unlike Entry.Subpath's storage form — same as client-side Records.
            rec.Subpath.ShouldBe("sdk_adapter_tests");
            rec.ResourceType.ShouldBe(ResourceType.Content);
            rec.Uuid.ShouldBe(entry.Uuid);

            // The interchangeability contract: every attribute value must be
            // a JsonElement (what System.Text.Json produces when the HTTP
            // client deserializes the server's echo) — not native boxed CLR
            // values that would make casts behave differently per backend.
            rec.Attributes.ShouldNotBeNull();
            foreach (var (key, value) in rec.Attributes!)
                value.ShouldBeOfType<JsonElement>($"attribute '{key}' must be JsonElement-normalized");

            ((JsonElement)rec.Attributes["is_active"]).ValueKind.ShouldBe(JsonValueKind.True);
            ((JsonElement)rec.Attributes["owner_shortname"]).GetString().ShouldBe("dmart");
            ((JsonElement)rec.Attributes["tags"]).ValueKind.ShouldBe(JsonValueKind.Array);
        }
        finally
        {
            try { await adapter.DeleteAsync(LocatorFor(entry)); } catch { }
        }
    }

    [FactIfPg]
    public async Task CreateAsync_On_Existing_Entry_Throws_DmartConflictException()
    {
        var adapter = MakeAdapter();
        var entry = NewEntry($"sdkx_{Guid.NewGuid():N}"[..16]);
        try
        {
            await adapter.CreateAsync(entry);
            var ex = await Should.ThrowAsync<DmartConflictException>(
                () => adapter.CreateAsync(entry));
            ex.ShouldBeAssignableTo<DmartException>();
            ex.StatusCode.ShouldBe(409);
        }
        finally
        {
            try { await adapter.DeleteAsync(LocatorFor(entry)); } catch { }
        }
    }

    [FactIfPg]
    public async Task UpdateAsync_On_Missing_Entry_Throws_DmartNotFoundException()
    {
        var adapter = MakeAdapter();
        var ghost = NewEntry($"sdkg_{Guid.NewGuid():N}"[..16]);
        var ex = await Should.ThrowAsync<DmartNotFoundException>(
            () => adapter.UpdateAsync(ghost));
        ex.StatusCode.ShouldBe(404);
    }

    [FactIfPg]
    public async Task SaveAsync_Upserts_And_Returns_Response_On_Both_Paths()
    {
        var adapter = MakeAdapter();
        var entry = NewEntry($"sdks_{Guid.NewGuid():N}"[..16]);
        try
        {
            // First save = create.
            var created = await adapter.SaveAsync(entry);
            created.Status.ShouldBe(Status.Success);

            // Second save = update; the change must round-trip.
            var renamed = entry with { Displayname = new Translation { En = "Renamed" } };
            var updated = await adapter.SaveAsync(renamed);
            updated.Status.ShouldBe(Status.Success);

            var reloaded = await adapter.LoadAsync(entry.SpaceName, entry.Subpath, entry.Shortname);
            reloaded.ShouldNotBeNull();
            reloaded!.Displayname?.En.ShouldBe("Renamed");
        }
        finally
        {
            try { await adapter.DeleteAsync(LocatorFor(entry)); } catch { }
        }
    }

    [FactIfPg]
    public async Task GetProfileAsync_Returns_Redacted_User_With_Payload()
    {
        // Seed through the server's repository (same database), including a
        // password hash and a payload body: GetProfileAsync must redact the
        // former (0.10.0 behavior change) and hydrate the latter (the payload
        // column newly added to the adapter's user projection).
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var adapter = MakeAdapter();
        var shortname = $"sdkp_{Guid.NewGuid():N}"[..16];
        try
        {
            await users.UpsertAsync(new User
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = shortname,
                SpaceName = "management",
                Subpath = "/users",
                OwnerShortname = shortname,
                IsActive = true,
                Type = UserType.Web,
                Language = Language.En,
                Email = $"{shortname}@x.yz",
                Password = "fake-hash-not-a-real-password",
                Payload = new Payload
                {
                    ContentType = ContentType.Json,
                    Body = JsonSerializer.SerializeToElement(new Dictionary<string, string>
                        { ["theme"] = "dark" }),
                },
                Roles = new(), Groups = new(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            var profile = await adapter.GetProfileAsync(shortname);

            profile.ShouldNotBeNull();
            profile!.Shortname.ShouldBe(shortname);
            profile.Email.ShouldBe($"{shortname}@x.yz");
            profile.Password.ShouldBeNull(
                "GetProfileAsync must return the redacted profile, never the password hash");
            profile.Payload.ShouldNotBeNull("the payload column must be hydrated");
            profile.Payload!.Body.ShouldNotBeNull();
        }
        finally
        {
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }
}
