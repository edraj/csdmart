using System.Data.Common;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Dmart.Utils;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// A user's mailbox, mail_aliases and services, and the user_addresses /
// user_services index tables behind them (docs/user-directory-fields.md).
// Every test runs on both engines: the index maintenance is engine-specific SQL.
public sealed class UserDirectoryFieldsTests(DmartFactory factory) : IClassFixture<DmartFactory>
{
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private UserRepository Users => factory.Services.GetRequiredService<UserRepository>();

    private static async Task<(bool Ok, string Raw)> ManagedAsync(HttpClient client, string requestType, string shortname, string attributesJson)
    {
        var body = $$"""
            {"space_name":"management","request_type":"{{requestType}}","records":[
              {"resource_type":"user","subpath":"users","shortname":"{{shortname}}","attributes":{{attributesJson}}}]}
            """;
        var resp = await client.PostAsync("/managed/request", new StringContent(body, Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        var parsed = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response)!;
        return (parsed.Status == Status.Success, raw);
    }

    [FactIfPg]
    public async Task Create_Normalizes_Stores_And_Indexes_The_Fields()
    {
        var admin = await factory.CreateLoggedInUserAsync();
        var sn = Unique("dfa");
        try
        {
            var (ok, raw) = await ManagedAsync(admin.Client, "create", sn, $$"""
                {"is_active":true,"mailbox":" {{sn}}@Example.ORG ",
                 "mail_aliases":["Postmaster-{{sn}}@example.org","postmaster-{{sn}}@EXAMPLE.org"],
                 "services":["Mail","matrix","mail"]}
                """);
            ok.ShouldBeTrue(raw);

            var u = (await Users.GetByShortnameAsync(sn))!;
            u.Mailbox.ShouldBe($"{sn}@example.org");
            u.MailAliases.ShouldBe(new[] { $"postmaster-{sn}@example.org" });   // folded, then de-duplicated
            u.Services.ShouldBe(new[] { "mail", "matrix" });

            (await Users.FindAddressOwnerAsync($"{sn}@example.org")).ShouldBe((sn, "mailbox"));
            (await Users.FindAddressOwnerAsync($"postmaster-{sn}@example.org")).ShouldBe((sn, "alias"));
            (await Users.ListByServiceAsync("matrix", null, 1000)).Select(x => x.Shortname).ShouldContain(sn);
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, sn);
            await admin.Cleanup();
        }
    }

    [FactIfPg]
    public async Task An_Address_Another_User_Holds_Is_Refused_Case_Insensitively()
    {
        var admin = await factory.CreateLoggedInUserAsync();
        var first = Unique("dfb");
        var second = Unique("dfc");
        try
        {
            (await ManagedAsync(admin.Client, "create", first, $$"""{"mailbox":"{{first}}@example.org"}""")).Ok.ShouldBeTrue();

            var (ok, raw) = await ManagedAsync(admin.Client, "create", second, $$"""
                {"mailbox":"{{second}}@example.org","mail_aliases":["{{first.ToUpperInvariant()}}@EXAMPLE.ORG"]}
                """);
            ok.ShouldBeFalse();
            raw.ShouldContain("already in use");
            (await Users.GetByShortnameAsync(second)).ShouldBeNull("a refused create writes nothing");
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, first);
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, second);
            await admin.Cleanup();
        }
    }

    [FactIfPg]
    public async Task The_Rules_Refuse_Malformed_Requests()
    {
        var admin = await factory.CreateLoggedInUserAsync();
        var sn = Unique("dfd");
        var contactOwner = Unique("dfe");
        try
        {
            (await ManagedAsync(admin.Client, "create", sn, $$"""{"mail_aliases":["a-{{sn}}@example.org"]}"""))
                .Raw.ShouldContain("need a mailbox");
            (await ManagedAsync(admin.Client, "create", sn, """{"mailbox":"not-an-address"}"""))
                .Ok.ShouldBeFalse();
            (await ManagedAsync(admin.Client, "create", sn, """{"services":["Bad Service!"]}"""))
                .Raw.ShouldContain("not a valid service name");
            (await ManagedAsync(admin.Client, "create", sn, $$"""
                {"mailbox":"{{sn}}@example.org","mail_aliases":["{{sn}}@example.org"]}
                """)).Raw.ShouldContain("cannot also be an alias");

            // Another account's contact email cannot become a mailbox: an LDAP
            // `mail=` lookup would then match two people.
            (await ManagedAsync(admin.Client, "create", contactOwner, $$"""{"email":"{{contactOwner}}@elsewhere.org"}""")).Ok.ShouldBeTrue();
            (await ManagedAsync(admin.Client, "create", sn, $$"""{"mailbox":"{{contactOwner}}@elsewhere.org"}"""))
                .Raw.ShouldContain("already in use");

            (await Users.GetByShortnameAsync(sn)).ShouldBeNull();
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, sn);
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, contactOwner);
            await admin.Cleanup();
        }
    }

    [FactIfPg]
    public async Task The_Allowlists_Bound_Services_And_Domains()
    {
        var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Configure<DmartSettings>(o =>
        {
            o.UserServices = "mail,matrix";
            o.UserMailDomains = "example.org";
        })));
        var admin = await factory.CreateLoggedInUserAsync(host);
        var sn = Unique("dff");
        try
        {
            (await ManagedAsync(admin.Client, "create", sn, """{"services":["gitea"]}"""))
                .Raw.ShouldContain("not a service this deployment offers");
            (await ManagedAsync(admin.Client, "create", sn, $$"""{"mailbox":"{{sn}}@example.com"}"""))
                .Raw.ShouldContain("not in a mail domain this deployment hosts");
            (await ManagedAsync(admin.Client, "create", sn, $$"""{"mailbox":"{{sn}}@example.org","services":["matrix"]}"""))
                .Ok.ShouldBeTrue();
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(host.Services, sn);
            await admin.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Updating_Aliases_Releases_The_Old_Ones()
    {
        var admin = await factory.CreateLoggedInUserAsync();
        var a = Unique("dfg");
        var b = Unique("dfh");
        try
        {
            (await ManagedAsync(admin.Client, "create", a, $$"""
                {"mailbox":"{{a}}@example.org","mail_aliases":["old-{{a}}@example.org"],"services":["mail"]}
                """)).Ok.ShouldBeTrue();

            // An update naming only aliases leaves mailbox and services alone.
            (await ManagedAsync(admin.Client, "update", a, $$"""{"mail_aliases":["new-{{a}}@example.org"]}""")).Ok.ShouldBeTrue();
            var u = (await Users.GetByShortnameAsync(a))!;
            u.Mailbox.ShouldBe($"{a}@example.org");
            u.Services.ShouldBe(new[] { "mail" });
            (await Users.FindAddressOwnerAsync($"old-{a}@example.org")).ShouldBeNull();
            (await Users.FindAddressOwnerAsync($"new-{a}@example.org")).ShouldBe((a, "alias"));

            // The released address is free for someone else.
            (await ManagedAsync(admin.Client, "create", b, $$"""
                {"mailbox":"{{b}}@example.org","mail_aliases":["old-{{a}}@example.org"]}
                """)).Ok.ShouldBeTrue();

            // Revoking a service removes it from the index.
            (await ManagedAsync(admin.Client, "update", a, """{"services":[]}""")).Ok.ShouldBeTrue();
            (await Users.ListByServiceAsync("mail", null, 100_000)).Select(x => x.Shortname).ShouldNotContain(a);
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, a);
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, b);
            await admin.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Soft_Delete_Releases_The_Addresses_And_Services()
    {
        var admin = await factory.CreateLoggedInUserAsync();
        var sn = Unique("dfi");
        try
        {
            (await ManagedAsync(admin.Client, "create", sn, $$"""
                {"mailbox":"{{sn}}@example.org","mail_aliases":["x-{{sn}}@example.org"],"services":["mail"]}
                """)).Ok.ShouldBeTrue();

            await Users.SoftDeleteAsync(sn);

            var u = (await Users.GetByShortnameAsync(sn))!;
            u.IsDeleted.ShouldBeTrue();
            u.Mailbox.ShouldBeNull();
            u.MailAliases.ShouldBeEmpty();
            u.Services.ShouldBeEmpty();
            (await Users.FindAddressOwnerAsync($"{sn}@example.org")).ShouldBeNull();
            (await Users.FindAddressOwnerAsync($"x-{sn}@example.org")).ShouldBeNull();
            (await Users.ListByServiceAsync("mail", null, 100_000)).Select(x => x.Shortname).ShouldNotContain(sn);
        }
        finally
        {
            try { await Users.DeleteAsync(sn); } catch { }
            await admin.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Rename_And_Hard_Delete_Carry_The_Index_Rows()
    {
        var admin = await factory.CreateLoggedInUserAsync();
        var from = Unique("dfj");
        var to = Unique("dfk");
        try
        {
            (await ManagedAsync(admin.Client, "create", from, $$"""
                {"mailbox":"{{from}}@example.org","services":["gitea"]}
                """)).Ok.ShouldBeTrue();

            // The index rows reference users(shortname) through a DEFERRED
            // foreign key: a rename that forgot them fails at commit.
            (await Users.RenameAsync(from, to)).ShouldBeTrue();
            (await Users.FindAddressOwnerAsync($"{from}@example.org")).ShouldBe((to, "mailbox"));
            (await Users.ListByServiceAsync("gitea", null, 100_000)).Select(x => x.Shortname).ShouldContain(to);

            await Users.DeleteAsync(to);
            (await Users.FindAddressOwnerAsync($"{from}@example.org")).ShouldBeNull();
        }
        finally
        {
            try { await Users.DeleteAsync(from); } catch { }
            try { await Users.DeleteAsync(to); } catch { }
            await admin.Cleanup();
        }
    }

    [FactIfPg]
    public async Task A_Racing_Writer_Loses_Its_Whole_Write()
    {
        // The handler's pre-check cannot see a writer that commits between the
        // check and the write. The primary key can: the second upsert fails, and
        // its user row is rolled back with the index rows.
        var winner = Unique("dfl");
        var loser = Unique("dfm");
        var address = $"race-{winner}@example.org";
        try
        {
            await Users.UpsertAsync(NewUser(winner, mailbox: address));
            var ex = await Should.ThrowAsync<DbException>(() => Users.UpsertAsync(NewUser(loser, mailbox: address)));
            DbErrors.IsUniqueViolation(ex).ShouldBeTrue();
            (await Users.GetByShortnameAsync(loser)).ShouldBeNull();
            (await Users.FindAddressOwnerAsync(address)).ShouldBe((winner, "mailbox"));
        }
        finally
        {
            try { await Users.DeleteAsync(winner); } catch { }
            try { await Users.DeleteAsync(loser); } catch { }
        }
    }

    [FactIfPg]
    public async Task An_Empty_Index_Is_Rebuilt_From_The_Users()
    {
        var sn = Unique("dfn");
        try
        {
            await Users.UpsertAsync(NewUser(sn, mailbox: $"{sn}@example.org") with { Services = ["matrix"] });

            // Simulate the upgrade: rows written before the index existed.
            // Safe in a shared database only because the suite runs serially
            // (TestParallelization.cs), and the rebuild restores every row.
            var db = factory.Services.GetRequiredService<IDbConnectionFactory>();
            await using (var conn = await db.OpenAsync())
            {
                foreach (var table in new[] { "user_addresses", "user_services" })
                {
                    await using var cmd = conn.Command($"DELETE FROM {table}");
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            (await Users.FindAddressOwnerAsync($"{sn}@example.org")).ShouldBeNull();

            (await Users.RebuildDirectoryIndexIfEmptyAsync()).ShouldBeGreaterThan(0);
            (await Users.FindAddressOwnerAsync($"{sn}@example.org")).ShouldBe((sn, "mailbox"));
            (await Users.ListByServiceAsync("matrix", null, 100_000)).Select(x => x.Shortname).ShouldContain(sn);

            // ...and is a no-op once the index holds anything.
            (await Users.RebuildDirectoryIndexIfEmptyAsync()).ShouldBe(0);
        }
        finally
        {
            try { await Users.DeleteAsync(sn); } catch { }
        }
    }

    [FactIfPg]
    public async Task A_User_Cannot_Grant_Themselves_Services_Or_Addresses()
    {
        var self = await factory.CreateLoggedInUserAsync();
        try
        {
            var resp = await self.Client.PostAsync("/user/profile", new StringContent($$$"""
                {"attributes":{"mailbox":"{{{self.Shortname}}}@example.org","mail_aliases":["root@example.org"],"services":["mail"]}}
                """, Encoding.UTF8, "application/json"));
            _ = await resp.Content.ReadAsStringAsync();

            var u = (await Users.GetByShortnameAsync(self.Shortname))!;
            u.Mailbox.ShouldBeNull();
            u.MailAliases.ShouldBeEmpty();
            u.Services.ShouldBeEmpty();
        }
        finally
        {
            await self.Cleanup();
        }
    }

    private static Dmart.Models.Core.User NewUser(string shortname, string? mailbox) => new()
    {
        Uuid = Guid.NewGuid().ToString(), Shortname = shortname, SpaceName = "management", Subpath = "/users",
        OwnerShortname = shortname, IsActive = true, Type = UserType.Web, Language = Language.En,
        Mailbox = mailbox, CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
    };
}
