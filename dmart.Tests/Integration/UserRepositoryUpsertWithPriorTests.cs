using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins.Native;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the COALESCE-on-payload semantics of UserRepository.UpsertWithPriorAsync:
//
//   payload = COALESCE(EXCLUDED.payload, users.payload)
//
// Every other column in the ON CONFLICT clause is a straight EXCLUDED.field
// overwrite, so a regression that drops the COALESCE would silently wipe
// payload data on any plugin-driven user update — see Plugins/Native/
// NativePluginCallbacks.cs::EmitUpdateUser, where a plugin that doesn't need
// to touch the payload simply passes a User with Payload=null and expects the
// existing payload to survive.
//
// Joins PluginInvocationContextCollection because the EmitUpdateUser test
// mutates ThreadStatic plugin context — must serialize against any other class
// that touches it (same reasoning as PluginCallbackHistoryTests).
[Collection(PluginInvocationContextCollection.Name)]
public sealed class UserRepositoryUpsertWithPriorTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserRepositoryUpsertWithPriorTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task UpsertWithPriorAsync_NonNullPayload_OverwritesExistingPayload()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();

        var sn = "uwp_" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            // Insert with payload A.
            var initial = NewUser(sn, payload: BuildPayload("""{"profile_key":"value_A"}"""));
            var (prior1, inserted1) = await users.UpsertWithPriorAsync(initial);
            prior1.ShouldBeNull("fresh shortname → no prior row");
            inserted1.ShouldBeTrue("first upsert is the insert path");

            // Re-upsert same shortname with payload B.
            var update = initial with { Payload = BuildPayload("""{"profile_key":"value_B"}""") };
            var (prior2, inserted2) = await users.UpsertWithPriorAsync(update);
            inserted2.ShouldBeFalse("second upsert hits the update branch");
            prior2.ShouldNotBeNull("update branch returns the prior row");
            prior2!.Payload.ShouldNotBeNull();
            // prior row carries the pre-update payload.
            prior2.Payload!.Body!.Value.GetRawText().ShouldContain("value_A");

            // DB now reflects payload B (non-null incoming payload overwrites
            // EXCLUDED.payload).
            var fetched = await users.GetByShortnameAsync(sn);
            fetched.ShouldNotBeNull();
            fetched!.Payload.ShouldNotBeNull();
            fetched.Payload!.Body!.Value.GetRawText().ShouldContain("value_B");
        }
        finally
        {
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task UpsertWithPriorAsync_NullPayload_PreservesExistingPayload()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();

        var sn = "uwp_" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            // Insert with payload A.
            var initial = NewUser(sn, payload: BuildPayload("""{"profile_key":"value_A"}"""));
            var (_, inserted1) = await users.UpsertWithPriorAsync(initial);
            inserted1.ShouldBeTrue();

            // Re-upsert with Payload=null but an unrelated field changed — proof
            // the update actually ran (and didn't no-op because we resent the
            // same row).
            var newEmail = $"new_{sn}@test.local";
            var update = initial with { Payload = null, Email = newEmail };
            var (prior2, inserted2) = await users.UpsertWithPriorAsync(update);
            inserted2.ShouldBeFalse("second upsert hits the update branch");
            prior2.ShouldNotBeNull();
            prior2!.Payload.ShouldNotBeNull();
            prior2.Payload!.Body!.Value.GetRawText().ShouldContain("value_A");

            // Email reflects the change (proof the row was written), AND
            // payload still holds value_A (proof COALESCE short-circuited to
            // users.payload rather than overwriting with NULL).
            var fetched = await users.GetByShortnameAsync(sn);
            fetched.ShouldNotBeNull();
            fetched!.Email.ShouldBe(newEmail, "non-payload fields still overwrite normally");
            fetched.Payload.ShouldNotBeNull("payload survives a null-payload upsert");
            // COALESCE(EXCLUDED.payload, users.payload) kept the prior value.
            fetched.Payload!.Body!.Value.GetRawText().ShouldContain("value_A");
        }
        finally
        {
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task EmitUpdateUser_NullPayload_PreservesExistingPayload()
    {
        var sp = _factory.Services;
        // Force the WebApplicationFactory to build the host so
        // NativePluginCallbacks.Services is wired up by Program.cs.
        _factory.CreateClient();
        var users = sp.GetRequiredService<UserRepository>();

        var sn = "uwp_" + Guid.NewGuid().ToString("N")[..10];

        // Seed the existing user with a payload.
        var initial = NewUser(sn, payload: BuildPayload("""{"profile_key":"value_A"}"""));
        await users.UpsertAsync(initial);

        // Set the ambient plugin context the dispatcher would set on a real
        // hook invocation. Don't overwrite NativePluginCallbacks.Services here
        // — Program.cs already wired it at factory boot, and nulling it in
        // finally would poison cross-class state.
        PluginInvocationContext.CurrentShortname = "test_plugin";
        PluginInvocationContext.CurrentActor = _factory.AdminShortname;
        try
        {
            // Simulate a plugin calling update_user with Payload=null — the
            // real-world case where the plugin only wants to flip is_email_verified
            // (or similar) and shouldn't have to round-trip the existing payload.
            var newEmail = $"plug_{sn}@test.local";
            var mutated = initial with
            {
                Payload = null,
                Email = newEmail,
                IsEmailVerified = false,
            };
            NativePluginCallbacks.EmitUpdateUser(mutated, logger: null).ShouldBe(0);

            // The plugin's non-payload changes propagated...
            var fetched = await users.GetByShortnameAsync(sn);
            fetched.ShouldNotBeNull();
            fetched!.Email.ShouldBe(newEmail);
            fetched.IsEmailVerified.ShouldBeFalse();
            // ...and the payload survived the plugin boundary.
            fetched.Payload.ShouldNotBeNull("EmitUpdateUser must preserve payload when caller sends null");
            fetched.Payload!.Body!.Value.GetRawText().ShouldContain("value_A");
        }
        finally
        {
            PluginInvocationContext.CurrentShortname = null;
            PluginInvocationContext.CurrentActor = null;
            try { await users.DeleteAsync(sn); } catch { }
        }
    }

    private static User NewUser(string shortname, Payload? payload = null) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/users",
        OwnerShortname = shortname,
        IsActive = true,
        Email = $"{shortname}@test.local",
        IsEmailVerified = true,
        Roles = new(),
        Groups = new(),
        Type = UserType.Web,
        Language = Language.En,
        Payload = payload,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Payload BuildPayload(string bodyJson) => new()
    {
        ContentType = ContentType.Json,
        Body = JsonSerializer.Deserialize<JsonElement>(bodyJson),
    };
}
