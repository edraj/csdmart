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

        private static readonly string Suffix = Guid.NewGuid().ToString("N")[..6];
        public string Service { get; } = "ldsvc_" + Suffix;
        public string Alice { get; } = "ldalice_" + Suffix;
        public string Bob { get; } = "ldbob_" + Suffix;       // deactivated
        public string Group { get; } = "ldgrp_" + Suffix;
        public string AliceMail => $"{Alice}@Example.org";

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
            })));
            _ = Host.CreateClient();   // starts the host, and with it the listener

            var users = Host.Services.GetRequiredService<UserRepository>();
            var hash = await Host.Services.GetRequiredService<PasswordHasher>().HashAsync(Password);
            await Host.Services.GetRequiredService<AccessRepository>().UpsertGroupAsync(new Group
            {
                Uuid = Guid.NewGuid().ToString(), Shortname = Group, SpaceName = "management", Subpath = "/groups",
                OwnerShortname = "dmart", IsActive = true, CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
            });
            await users.UpsertAsync(NewUser(Service, hash, UserType.Bot, active: true, groups: [], email: null));
            await users.UpsertAsync(NewUser(Alice, hash, UserType.Web, active: true, groups: [Group], email: AliceMail));
            await users.UpsertAsync(NewUser(Bob, hash, UserType.Web, active: false, groups: [Group], email: null));

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
                foreach (var u in new[] { Service, Alice, Bob })
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
        await using var c = await Client.ConnectAsync(fx.Port);
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
        await using var c = await Client.ConnectAsync(fx.Port);

        (await c.BindAsync(UserDn(fx.Alice), "Wrong12345")).ShouldBe(LdapResult.InvalidCredentials);
        (await users.GetAttemptCountAsync(fx.Alice)).ShouldBeGreaterThan(0);

        (await c.BindAsync(UserDn(fx.Alice), Password)).ShouldBe(LdapResult.Success);
        (await users.GetAttemptCountAsync(fx.Alice)).ShouldBe(0);
    }

    [FactIfPg]
    public async Task A_Service_Account_Finds_A_User_By_Uid_And_By_Mail()
    {
        await using var c = await Client.ConnectAsync(fx.Port);
        (await c.BindAsync(ServiceDn, Password)).ShouldBe(LdapResult.Success);

        var (code, entries) = await c.SearchAsync($"ou=people,{Base}", Filter.And(
            Filter.Eq("objectClass", "inetOrgPerson"), Filter.Eq("isActive", "TRUE"), Filter.Eq("uid", fx.Alice)));
        code.ShouldBe(LdapResult.Success);
        var alice = entries.ShouldHaveSingleItem();
        alice.Dn.ShouldBe(UserDn(fx.Alice));
        alice.Attrs["mail"].ShouldBe(new[] { fx.AliceMail });
        alice.Attrs["authorizedService"].ShouldBe(new[] { fx.Group });
        alice.Attrs.Keys.ShouldNotContain("userPassword");

        // mail is matched case-insensitively, through the email index.
        (await c.SearchAsync($"ou=people,{Base}", Filter.Eq("mail", fx.AliceMail.ToUpperInvariant())))
            .Entries.ShouldHaveSingleItem().Dn.ShouldBe(UserDn(fx.Alice));
    }

    [FactIfPg]
    public async Task An_Unanchored_Filter_Scans_And_Applies_Every_Clause()
    {
        await using var c = await Client.ConnectAsync(fx.Port);
        (await c.BindAsync(ServiceDn, Password)).ShouldBe(LdapResult.Success);

        // authorizedService is not indexed, so this walks the users table in
        // keyset pages — the query that differs between the two engines.
        var (code, entries) = await c.SearchAsync($"ou=people,{Base}", Filter.And(
            Filter.Eq("authorizedService", fx.Group), Filter.Eq("isActive", "TRUE")), "uid");
        code.ShouldBe(LdapResult.Success);
        entries.Select(e => e.Dn).ToArray().ShouldBe(new[] { UserDn(fx.Alice) },
            customMessage: "bob is in the group but deactivated");
    }

    [FactIfPg]
    public async Task A_Group_Entry_Lists_Its_Members()
    {
        await using var c = await Client.ConnectAsync(fx.Port);
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
        await using var c = await Client.ConnectAsync(fx.Port);
        (await c.SearchAsync($"ou=people,{Base}", Filter.Present("objectClass")))
            .Code.ShouldBe(LdapResult.InsufficientAccessRights);

        (await c.BindAsync(UserDn(fx.Alice), Password)).ShouldBe(LdapResult.Success);
        var (code, entries) = await c.SearchAsync(Base, Filter.Present("objectClass"), "uid");
        code.ShouldBe(LdapResult.Success);
        entries.Select(e => e.Dn).ToArray().ShouldBe(new[] { UserDn(fx.Alice) });
    }

    // ---------------- a minimal client ----------------

    private static class Filter
    {
        private static byte[] Encode(Action<AsnWriter> write)
        {
            var w = new AsnWriter(AsnEncodingRules.BER);
            write(w);
            return w.Encode();
        }

        public static byte[] Eq(string attr, string value) => Encode(w =>
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3, isConstructed: true)))
            {
                w.WriteOctetString(Encoding.UTF8.GetBytes(attr));
                w.WriteOctetString(Encoding.UTF8.GetBytes(value));
            }
        });

        public static byte[] Present(string attr)
            => Encode(w => w.WriteOctetString(Encoding.UTF8.GetBytes(attr), new Asn1Tag(TagClass.ContextSpecific, 7)));

        public static byte[] And(params byte[][] children) => Encode(w =>
        {
            using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                foreach (var c in children) w.WriteEncodedValue(c);
        });
    }

    private sealed record Entry(string Dn, Dictionary<string, List<string>> Attrs);

    private sealed class Client : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private int _nextId = 1;

        private Client(TcpClient tcp) { _tcp = tcp; _stream = tcp.GetStream(); }

        public static async Task<Client> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return new Client(tcp);
        }

        private async Task SendAsync(int opTag, Action<AsnWriter> body)
        {
            var w = new AsnWriter(AsnEncodingRules.BER);
            using (w.PushSequence())
            {
                w.WriteInteger(_nextId++);
                using (w.PushSequence(new Asn1Tag(TagClass.Application, opTag, isConstructed: true))) body(w);
            }
            await _stream.WriteAsync(w.Encode());
        }

        private async Task<(int Tag, AsnReader Op)> ReceiveAsync()
        {
            var frame = await LdapCodec.ReadFrameAsync(_stream, 1 << 20, CancellationToken.None)
                ?? throw new InvalidOperationException("server closed the connection");
            var msg = new AsnReader(frame, AsnEncodingRules.BER).ReadSequence();
            msg.TryReadInt32(out _);
            var tag = msg.PeekTag();
            return (tag.TagValue, msg.ReadSequence(tag));
        }

        private static int ResultCode(AsnReader op)
        {
            var bytes = op.ReadEnumeratedBytes().Span;
            var code = 0;
            foreach (var b in bytes) code = (code << 8) | b;
            return code;
        }

        public async Task<int> BindAsync(string dn, string password)
        {
            await SendAsync(LdapOp.BindRequest, w =>
            {
                w.WriteInteger(3);
                w.WriteOctetString(Encoding.UTF8.GetBytes(dn));
                w.WriteOctetString(Encoding.UTF8.GetBytes(password), new Asn1Tag(TagClass.ContextSpecific, 0));
            });
            var (_, op) = await ReceiveAsync();
            return ResultCode(op);
        }

        public async Task<(int Code, List<Entry> Entries)> SearchAsync(string baseDn, byte[] filter, params string[] attrs)
            => await SearchAsync(baseDn, filter, 2, attrs);

        public async Task<(int Code, List<Entry> Entries)> SearchAsync(string baseDn, byte[] filter, int scope, params string[] attrs)
        {
            await SendAsync(LdapOp.SearchRequest, w =>
            {
                w.WriteOctetString(Encoding.UTF8.GetBytes(baseDn));
                w.WriteEncodedValue([0x0A, 0x01, (byte)scope]);
                w.WriteEncodedValue([0x0A, 0x01, 0x00]);
                w.WriteInteger(0);
                w.WriteInteger(0);
                w.WriteBoolean(false);
                w.WriteEncodedValue(filter);
                using (w.PushSequence())
                    foreach (var a in attrs) w.WriteOctetString(Encoding.UTF8.GetBytes(a));
            });

            var entries = new List<Entry>();
            while (true)
            {
                var (tag, op) = await ReceiveAsync();
                if (tag == LdapOp.SearchResultDone) return (ResultCode(op), entries);
                var dn = Encoding.UTF8.GetString(op.ReadOctetString());
                var attrMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var list = op.ReadSequence();
                while (list.HasData)
                {
                    var a = list.ReadSequence();
                    var name = Encoding.UTF8.GetString(a.ReadOctetString());
                    var values = new List<string>();
                    var set = a.ReadSetOf();
                    while (set.HasData) values.Add(Encoding.UTF8.GetString(set.ReadOctetString()));
                    attrMap[name] = values;
                }
                entries.Add(new Entry(dn, attrMap));
            }
        }

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
