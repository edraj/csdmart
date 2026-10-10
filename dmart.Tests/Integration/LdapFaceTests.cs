using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Ldap;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Tests.Infrastructure;
using Dmart.Utils;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Filter = Dmart.Tests.Infrastructure.TestLdapFilter;

namespace Dmart.Tests.Integration;

// The LDAP directory face end to end: a real host with LDAP_PORT set, real
// rows in whichever database the leg runs on, and a client speaking BER over a
// socket. Interop with libldap, Postfix, Dovecot, Dex and Gitea is covered by
// bench/ldap-face-interop.sh; this pins the server's own contract on both
// engines, including the two queries that differ between them (the keyset scan
// and the group-membership JSON containment).
public sealed class LdapFaceTests(LdapFaceTests.Fixture fx) : IClassFixture<LdapFaceTests.Fixture>
{
    private const string Base = "dc=ldaptest";
    private const string Password = "Ldap12345";

    public sealed class Fixture : IAsyncLifetime
    {
        private readonly DmartFactory _factory = new();
        public WebApplicationFactory<Program> Host { get; private set; } = null!;
        public int Port { get; private set; }
        // The LDAP server's clock, for the time-limit test. Still unless a test
        // sets Step.
        internal SteppingClock Clock { get; } = new();

        private static readonly string Suffix = Guid.NewGuid().ToString("N")[..6];
        public string Service { get; } = "ldsvc_" + Suffix;
        public string Alice { get; } = "ldalice_" + Suffix;   // mailbox, alias, the service
        public string Bob { get; } = "ldbob_" + Suffix;       // the service, but deactivated
        public string Carol { get; } = "ldcarol_" + Suffix;   // no mailbox: `mail` is her contact email
        public string Group { get; } = "ldgrp_" + Suffix;
        public string Granted { get; } = "ldmail" + Suffix;   // a service slug
        public string AliceMailbox => $"{Alice}@Hosted.test";
        public string AliceAlias => $"alias-{Alice}@hosted.test";
        public string AliceContact => $"{Alice}@elsewhere.test";
        public string CarolContact => $"{Carol}@elsewhere.test";

        public async Task InitializeAsync()
        {
            await ((IAsyncLifetime)_factory).InitializeAsync();
            if (!DmartFactory.HasPg) return;

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Configure<DmartSettings>(o =>
            {
                o.LdapPort = Port;
                o.LdapHost = "127.0.0.1";
                o.LdapBaseDn = Base;
                o.LdapServiceAccounts = Service;
                // Small enough that an unindexed search over the shared test
                // database runs out, which is what the budget tests need.
                o.LdapMaxScan = 3;
            }).AddSingleton<TimeProvider>(Clock)));
            _ = Host.CreateClient();   // starts the host, and with it the listener

            var users = Host.Services.GetRequiredService<UserRepository>();
            var hash = await Host.Services.GetRequiredService<PasswordHasher>().HashAsync(Password);
            await Host.Services.GetRequiredService<AccessRepository>().UpsertGroupAsync(new Group
            {
                Uuid = Guid.NewGuid().ToString(), Shortname = Group, SpaceName = "management", Subpath = "/groups",
                OwnerShortname = "dmart", IsActive = true, CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
            });
            await users.UpsertAsync(NewUser(Service, hash, UserType.Bot, active: true, groups: [], email: null));
            await users.UpsertAsync(NewUser(Alice, hash, UserType.Web, active: true, groups: [Group], email: AliceContact) with
            {
                Mailbox = AliceMailbox, MailAliases = [AliceAlias], Services = [Granted],
            });
            await users.UpsertAsync(NewUser(Bob, hash, UserType.Web, active: false, groups: [Group], email: null) with
            {
                Services = [Granted],
            });
            await users.UpsertAsync(NewUser(Carol, hash, UserType.Web, active: true, groups: [], email: CarolContact));

            for (var i = 0; i < 100; i++)
            {
                try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, Port); return; }
                catch (SocketException) { await Task.Delay(100); }
            }
            throw new InvalidOperationException("LDAP listener never came up");
        }

        private static User NewUser(string shortname, string hash, UserType type, bool active, List<string> groups, string? email) => new()
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = shortname, SpaceName = "management", Subpath = "/users",
            OwnerShortname = shortname, IsActive = active, Password = hash, Type = type, Language = Language.En,
            Email = email, Displayname = new Translation(En: "Test " + shortname),
            Roles = new(), Groups = groups, CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        };

        public async Task DisposeAsync()
        {
            if (Host is not null)
            {
                foreach (var u in new[] { Service, Alice, Bob, Carol })
                    await TestUserCleanup.DeleteUserAndOwnedAsync(Host.Services, u);
                try { await Host.Services.GetRequiredService<AccessRepository>().DeleteGroupAsync(Group); } catch { }
                await Host.DisposeAsync();
            }
            await ((IAsyncLifetime)_factory).DisposeAsync();
        }
    }

    private string UserDn(string u) => $"uid={u},ou=people,{Base}";
    private string ServiceDn => $"cn={fx.Service},ou=services,{Base}";

    [FactIfPg]
    public async Task A_User_Binds_With_Their_Dmart_Password()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.BindAsync(UserDn(fx.Alice), Password)).ShouldBe(LdapResult.Success);
        (await c.BindAsync(UserDn(fx.Alice), "Wrong12345")).ShouldBe(LdapResult.InvalidCredentials);
        (await c.BindAsync(UserDn(fx.Bob), Password)).ShouldBe(LdapResult.InvalidCredentials, customMessage: "deactivated");
        (await c.BindAsync($"uid=nobody,ou=people,{Base}", Password)).ShouldBe(LdapResult.InvalidCredentials);
        (await c.BindAsync(UserDn(fx.Alice), "")).ShouldBe(LdapResult.UnwillingToPerform, customMessage: "unauthenticated bind");
    }

    [FactIfPg]
    public async Task A_Failed_Bind_Counts_Toward_The_Lockout_And_A_Good_One_Clears_It()
    {
        // The bind path shares dmart's lockout counter: an LDAP client must not
        // be a way to guess passwords that /user/login would lock out.
        var users = fx.Host.Services.GetRequiredService<UserRepository>();
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);

        // A wrong password no other test has used: a repeat would not count.
        var wrong = "Wrong" + Guid.NewGuid().ToString("N")[..8];
        (await c.BindAsync(UserDn(fx.Alice), wrong)).ShouldBe(LdapResult.InvalidCredentials);
        (await users.GetAttemptCountAsync(fx.Alice)).ShouldBeGreaterThan(0);

        (await c.BindAsync(UserDn(fx.Alice), Password)).ShouldBe(LdapResult.Success);
        (await users.GetAttemptCountAsync(fx.Alice)).ShouldBe(0);
    }

    [FactIfPg]
    public async Task A_Stale_Password_Retried_By_A_Device_Counts_Once_And_Does_Not_Lock()
    {
        // A phone still holding the old password retries it for as long as it
        // runs. Counting each retry would lock the account within
        // MaxFailedLoginAttempts tries and keep it locked, since every attempt
        // refreshes the cool-down. The same wrong password counts once.
        var users = fx.Host.Services.GetRequiredService<UserRepository>();
        var max = fx.Host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DmartSettings>>().Value.MaxFailedLoginAttempts;
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.BindAsync(UserDn(fx.Alice), Password)).ShouldBe(LdapResult.Success);

        var stale = "Stale" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < max + 2; i++)
            (await c.BindAsync(UserDn(fx.Alice), stale)).ShouldBe(LdapResult.InvalidCredentials);
        (await users.GetAttemptCountAsync(fx.Alice)).ShouldBe(1);

        // Different wrong passwords are guesses, and each one counts.
        for (var i = 0; i < 2; i++)
            (await c.BindAsync(UserDn(fx.Alice), $"Guess{i}{Guid.NewGuid():N}")).ShouldBe(LdapResult.InvalidCredentials);
        (await users.GetAttemptCountAsync(fx.Alice)).ShouldBe(3);

        (await c.BindAsync(UserDn(fx.Alice), Password)).ShouldBe(LdapResult.Success);
        (await users.GetAttemptCountAsync(fx.Alice)).ShouldBe(0);
    }

    [FactIfPg]
    public async Task A_Service_Account_Finds_A_User_By_Uid_Mailbox_And_Alias()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.BindAsync(ServiceDn, Password)).ShouldBe(LdapResult.Success);

        var (code, entries) = await c.SearchAsync($"ou=people,{Base}", Filter.And(
            Filter.Eq("objectClass", "inetOrgPerson"), Filter.Eq("isActive", "TRUE"), Filter.Eq("uid", fx.Alice)));
        code.ShouldBe(LdapResult.Success);
        var alice = entries.ShouldHaveSingleItem();
        alice.Dn.ShouldBe(UserDn(fx.Alice));
        // `mail` is the hosted mailbox (stored folded), not the contact email.
        alice.Attrs["mail"].ShouldBe(new[] { fx.AliceMailbox.ToLowerInvariant() });
        alice.Attrs["mailAlias"].ShouldBe(new[] { fx.AliceAlias });
        alice.Attrs["authorizedService"].ShouldBe(new[] { fx.Granted });
        alice.Attrs.Keys.ShouldNotContain("userPassword");

        // Both addresses resolve through user_addresses, case-insensitively.
        (await c.SearchAsync($"ou=people,{Base}", Filter.Eq("mail", fx.AliceMailbox.ToUpperInvariant())))
            .Entries.ShouldHaveSingleItem().Dn.ShouldBe(UserDn(fx.Alice));
        (await c.SearchAsync($"ou=people,{Base}", Filter.Eq("mailAlias", fx.AliceAlias.ToUpperInvariant())))
            .Entries.ShouldHaveSingleItem().Dn.ShouldBe(UserDn(fx.Alice));

        // Her contact email is not a directory address.
        (await c.SearchAsync($"ou=people,{Base}", Filter.Eq("mail", fx.AliceContact))).Entries.ShouldBeEmpty();
    }

    [FactIfPg]
    public async Task A_User_Without_A_Mailbox_Is_Found_By_Contact_Email()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.BindAsync(ServiceDn, Password)).ShouldBe(LdapResult.Success);
        var carol = (await c.SearchAsync($"ou=people,{Base}", Filter.Eq("mail", fx.CarolContact)))
            .Entries.ShouldHaveSingleItem();
        carol.Dn.ShouldBe(UserDn(fx.Carol));
        carol.Attrs["mail"].ShouldBe(new[] { fx.CarolContact });
    }

    [FactIfPg]
    public async Task A_Service_Listing_Reads_The_Services_Index()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.BindAsync(ServiceDn, Password)).ShouldBe(LdapResult.Success);

        // authorizedService reads user_services: two rows examined (alice, bob),
        // inside a budget of three that the whole table would blow through.
        var (code, entries) = await c.SearchAsync($"ou=people,{Base}", Filter.And(
            Filter.Eq("authorizedService", fx.Granted), Filter.Eq("isActive", "TRUE")), "uid");
        code.ShouldBe(LdapResult.Success);
        entries.Select(e => e.Dn).ToArray().ShouldBe(new[] { UserDn(fx.Alice) },
            customMessage: "bob holds the service but is deactivated");
    }

    [FactIfPg]
    public async Task An_Index_Still_Being_Rebuilt_Answers_Unavailable_Not_Empty()
    {
        // Postfix treats "no such entry" as a permanent 550 and bounces the
        // mail; `unavailable` makes it defer and retry. While the index is not
        // ready, lookups that depend on it must say the second thing.
        var status = fx.Host.Services.GetRequiredService<DirectoryIndexStatus>();
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.BindAsync(ServiceDn, Password)).ShouldBe(LdapResult.Success);
        status.Set(false);
        try
        {
            (await c.SearchAsync($"ou=people,{Base}", Filter.Eq("mail", fx.AliceMailbox))).Code.ShouldBe(LdapResult.Unavailable);
            (await c.SearchAsync($"ou=people,{Base}", Filter.Eq("mailAlias", fx.AliceAlias))).Code.ShouldBe(LdapResult.Unavailable);
            (await c.SearchAsync($"ou=people,{Base}", Filter.Eq("authorizedService", fx.Granted))).Code.ShouldBe(LdapResult.Unavailable);
            // uid does not touch the index, so Dex and Gitea logins keep working.
            (await c.SearchAsync($"ou=people,{Base}", Filter.Eq("uid", fx.Alice))).Entries.ShouldHaveSingleItem();
        }
        finally
        {
            status.Set(true);
        }
    }

    [FactIfPg]
    public async Task An_Unindexed_Search_Stops_At_The_Scan_Budget_Unless_Paged()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.BindAsync(ServiceDn, Password)).ShouldBe(LdapResult.Success);
        var anyone = Filter.Present("displayName");

        // Unpaged: three rows examined, then adminLimitExceeded.
        (await c.SearchAsync($"ou=people,{Base}", anyone, "uid")).Code.ShouldBe(LdapResult.AdminLimitExceeded);

        // Paged: each request gets its own budget of three, so the same listing
        // completes as a run of short pages — the shape Gitea's user sync takes.
        var (code, entries, pages) = await c.SearchPagedAsync($"ou=people,{Base}", anyone, 1000, "uid");
        code.ShouldBe(LdapResult.Success);
        pages.ShouldBeGreaterThan(1);
        var dns = entries.Select(e => e.Dn).ToList();
        dns.ShouldContain(UserDn(fx.Alice));
        dns.ShouldContain(UserDn(fx.Carol));
        dns.Count.ShouldBe(dns.Distinct().Count(), "a resumed page must not repeat entries");
    }

    [FactIfPg]
    public async Task A_Group_Entry_Lists_Its_Members()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.BindAsync(ServiceDn, Password)).ShouldBe(LdapResult.Success);

        // member values come from the JSON-array containment query on users.groups.
        var (_, entries) = await c.SearchAsync($"cn={fx.Group},ou=groups,{Base}", Filter.Present("objectClass"), scope: 0);
        var members = entries.ShouldHaveSingleItem().Attrs["member"];
        members.ShouldContain(UserDn(fx.Alice));
        members.ShouldContain(UserDn(fx.Bob));

        // ...and the reverse question, which Gitea and Dex ask, is answered.
        (await c.SearchAsync($"ou=groups,{Base}", Filter.Eq("member", UserDn(fx.Alice).ToUpperInvariant())))
            .Entries.Select(e => e.Dn).ShouldContain($"cn={fx.Group},ou=groups,{Base}");
    }

    [FactIfPg]
    public async Task A_User_Sees_Only_Their_Own_Entry_And_Anonymous_Sees_Nothing()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.SearchAsync($"ou=people,{Base}", Filter.Present("objectClass")))
            .Code.ShouldBe(LdapResult.InsufficientAccessRights);

        (await c.BindAsync(UserDn(fx.Alice), Password)).ShouldBe(LdapResult.Success);
        var (code, entries) = await c.SearchAsync(Base, Filter.Present("objectClass"), "uid");
        code.ShouldBe(LdapResult.Success);
        entries.Select(e => e.Dn).ToArray().ShouldBe(new[] { UserDn(fx.Alice) });
    }

    [FactIfPg]
    public async Task The_Subschema_Is_Named_By_The_Root_Dse_And_Readable_Before_Binding()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        var (_, root) = await c.SearchAsync("", Filter.Present("objectClass"), 0, "subschemaSubentry");
        root.ShouldHaveSingleItem().Attrs["subschemaSubentry"].ShouldBe(new[] { "cn=Subschema" });

        var (code, entries) = await c.SearchAsync("cn=Subschema", Filter.Eq("objectClass", "subschema"), 0,
            "objectClasses", "attributeTypes");
        code.ShouldBe(LdapResult.Success);
        var schema = entries.ShouldHaveSingleItem();
        schema.Attrs["objectClasses"].ShouldContain(d => d.Contains("NAME 'freexUser'"));
        schema.Attrs["objectClasses"].ShouldContain(d => d.Contains("NAME 'dmartUser'"));
        schema.Attrs["attributeTypes"].ShouldContain(d => d.Contains("NAME 'mailAlias'") && d.Contains("1.1.2.1.2"));
        // Every OID is defined once.
        var oids = schema.Attrs["objectClasses"].Concat(schema.Attrs["attributeTypes"])
            .Select(d => d.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]).ToList();
        oids.Distinct().Count().ShouldBe(oids.Count);
    }

    [FactIfPg]
    public async Task A_Group_Search_Loads_Member_Lists_Only_When_The_Answer_Needs_Them()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.BindAsync(ServiceDn, Password)).ShouldBe(LdapResult.Success);
        var groupDn = $"cn={fx.Group},ou=groups,{Base}";
        var byAlice = Filter.And(Filter.Eq("objectClass", "groupOfNames"), Filter.Eq("member", UserDn(fx.Alice)));

        // Dex's login-time question: which groups is this user in, for cn.
        var (code, entries) = await c.SearchAsync($"ou=groups,{Base}", byAlice, "cn");
        code.ShouldBe(LdapResult.Success);
        var group = entries.ShouldHaveSingleItem();
        group.Dn.ShouldBe(groupDn);
        group.Attrs.Keys.ToArray().ShouldBe(new[] { "cn" });

        // Asking for member returns every member, not only the one named.
        (_, entries) = await c.SearchAsync($"ou=groups,{Base}", byAlice, "member");
        entries.ShouldHaveSingleItem().Attrs["member"].ShouldBe(
            new[] { UserDn(fx.Alice), UserDn(fx.Bob) }, ignoreOrder: true);

        // A negated or non-equality test of member is decided on the full list.
        (_, entries) = await c.SearchAsync($"ou=groups,{Base}", Filter.And(
            Filter.Eq("cn", fx.Group), Filter.Not(Filter.Eq("member", $"uid=nobody,ou=people,{Base}"))), "cn");
        entries.Select(e => e.Dn).ShouldBe(new[] { groupDn });
        (_, entries) = await c.SearchAsync($"ou=groups,{Base}", Filter.And(
            Filter.Eq("cn", fx.Group), Filter.Not(Filter.Eq("member", UserDn(fx.Bob)))), "cn");
        entries.ShouldBeEmpty();
        (_, entries) = await c.SearchAsync($"ou=groups,{Base}", Filter.And(
            Filter.Eq("cn", fx.Group), Filter.Present("member")), "cn");
        entries.Select(e => e.Dn).ShouldBe(new[] { groupDn });
    }

    [FactIfPg]
    public async Task A_Time_Limit_Ends_A_Search_With_What_It_Has_Sent()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port);
        (await c.BindAsync(ServiceDn, Password)).ShouldBe(LdapResult.Success);

        // Not reached: an indexed lookup well inside its limit.
        (await c.SearchTimedAsync($"ou=people,{Base}", Filter.Eq("uid", fx.Alice), timeLimitSeconds: 5))
            .Code.ShouldBe(LdapResult.Success);

        // Every reading of the server's clock now moves it a second, so a
        // 2-second limit runs out a couple of results in.
        fx.Clock.Step = TimeSpan.FromSeconds(1);
        try
        {
            var (code, _) = await c.SearchTimedAsync($"ou=people,{Base}", Filter.Present("objectClass"), timeLimitSeconds: 2);
            code.ShouldBe(LdapResult.TimeLimitExceeded);
            // ...paged too, which ends the paged search rather than pausing it.
            (code, _) = await c.SearchTimedAsync($"ou=people,{Base}", Filter.Present("objectClass"), timeLimitSeconds: 2, pageSize: 100);
            code.ShouldBe(LdapResult.TimeLimitExceeded);
            // No limit, no effect, however the clock moves.
            (code, _) = await c.SearchTimedAsync($"ou=people,{Base}", Filter.Eq("uid", fx.Alice), timeLimitSeconds: 0);
            code.ShouldBe(LdapResult.Success);
        }
        finally
        {
            fx.Clock.Step = TimeSpan.Zero;
        }
    }
}
