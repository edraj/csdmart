using System.Net;
using System.Net.Sockets;
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

// The LDAP face with a certificate: LDAPS, StartTLS, the refusal of cleartext
// password binds, and the per-address failed-bind budget. One host, with
// clients told apart by loopback source address:
//   127.0.0.1  an ordinary, untrusted client
//   127.0.0.2  the client that uses up its failed-bind budget
//   127.0.0.3  the one trusted peer (LdapTrustedPeers), like a local Dovecot
//   127.0.0.4  an untrusted client trying a service account
//   127.0.0.5  an untrusted client opening too many connections
public sealed class LdapFaceTlsTests(LdapFaceTlsTests.Fixture fx) : IClassFixture<LdapFaceTlsTests.Fixture>
{
    private const string Base = "dc=ldaptls";
    private const string Password = "Ldap12345";
    private const int FailuresPerMinute = 3;
    private static readonly IPAddress Untrusted = IPAddress.Parse("127.0.0.1");
    private static readonly IPAddress Guesser = IPAddress.Parse("127.0.0.2");
    private static readonly IPAddress Trusted = IPAddress.Parse("127.0.0.3");
    private static readonly IPAddress Outsider = IPAddress.Parse("127.0.0.4");
    private static readonly IPAddress Crowd = IPAddress.Parse("127.0.0.5");

    public sealed class Fixture : IAsyncLifetime
    {
        private readonly DmartFactory _factory = new();
        private readonly string _dir = Directory.CreateTempSubdirectory("ldapface-tls-").FullName;
        public WebApplicationFactory<Program> Host { get; private set; } = null!;
        public int Port { get; private set; }
        public int TlsPort { get; private set; }
        internal TestCertificates.Pem Certificate { get; } = TestCertificates.SelfSigned();
        public string Alice { get; } = "lstls_" + Guid.NewGuid().ToString("N")[..6];
        public string Service { get; } = "lstsvc_" + Guid.NewGuid().ToString("N")[..6];
        public string NotABot { get; } = "lstweb_" + Guid.NewGuid().ToString("N")[..6];   // listed, but a person

        public async Task InitializeAsync()
        {
            await ((IAsyncLifetime)_factory).InitializeAsync();
            if (!DmartFactory.HasPg) return;

            var certFile = Path.Combine(_dir, "fullchain.pem");
            var keyFile = Path.Combine(_dir, "privkey.pem");
            await File.WriteAllTextAsync(certFile, Certificate.CertificatePem);
            await File.WriteAllTextAsync(keyFile, Certificate.KeyPem);
            Port = FreePort();
            TlsPort = FreePort();

            Host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Configure<DmartSettings>(o =>
            {
                o.LdapPort = Port;
                o.LdapsPort = TlsPort;
                o.LdapHost = "127.0.0.1";
                o.LdapBaseDn = Base;
                o.LdapTlsCertFile = certFile;
                o.LdapTlsKeyFile = keyFile;
                o.LdapTrustedPeers = Trusted.ToString();
                o.LdapServiceAccounts = $"{Service},{NotABot}";
                o.AuthRateLimitPerMinute = FailuresPerMinute;
            })));
            _ = Host.CreateClient();

            var hash = await Host.Services.GetRequiredService<PasswordHasher>().HashAsync(Password);
            foreach (var (name, type) in new[] { (Alice, UserType.Web), (Service, UserType.Bot), (NotABot, UserType.Web) })
                await Host.Services.GetRequiredService<UserRepository>().UpsertAsync(new User
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = name, SpaceName = "management", Subpath = "/users",
                    OwnerShortname = name, IsActive = true, Password = hash, Type = type, Language = Language.En,
                    Roles = new(), Groups = new(), CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
                });

            foreach (var port in new[] { Port, TlsPort })
                for (var i = 0; i < 100; i++)
                {
                    try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port); break; }
                    catch (SocketException) { await Task.Delay(100); }
                }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public async Task DisposeAsync()
        {
            if (Host is not null)
            {
                foreach (var name in new[] { Alice, Service, NotABot })
                    await TestUserCleanup.DeleteUserAndOwnedAsync(Host.Services, name);
                await Host.DisposeAsync();
            }
            await ((IAsyncLifetime)_factory).DisposeAsync();
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string AliceDn => $"uid={fx.Alice},ou=people,{Base}";

    [FactIfPg]
    public async Task The_Root_Dse_Advertises_StartTls()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port, Untrusted);
        var (code, entries) = await c.SearchAsync("", Filter.Present("objectClass"), 0, "supportedExtension");
        code.ShouldBe(LdapResult.Success);
        entries.ShouldHaveSingleItem().Attrs["supportedExtension"].ShouldContain(LdapOid.StartTls);
    }

    [FactIfPg]
    public async Task A_Password_Bind_In_The_Clear_Is_Refused_Unless_The_Peer_Is_Trusted()
    {
        await using (var c = await TestLdapClient.ConnectAsync(fx.Port, Untrusted))
        {
            (await c.BindAsync(AliceDn, Password)).ShouldBe(LdapResult.ConfidentialityRequired);
            (await c.BindAsync("", "")).ShouldBe(LdapResult.Success, "an anonymous bind carries no password");
        }
        await using (var c = await TestLdapClient.ConnectAsync(fx.Port, Trusted))
            (await c.BindAsync(AliceDn, Password)).ShouldBe(LdapResult.Success);
    }

    [FactIfPg]
    public async Task Ldaps_Carries_Binds_And_Searches()
    {
        await using var c = await TestLdapClient.ConnectTlsAsync(fx.TlsPort, fx.Certificate.Public, Untrusted);
        c.Secure.ShouldBeTrue();
        (await c.BindAsync(AliceDn, Password)).ShouldBe(LdapResult.Success);
        var (code, entries) = await c.SearchAsync(Base, Filter.Present("objectClass"), "uid");
        code.ShouldBe(LdapResult.Success);
        entries.ShouldHaveSingleItem().Dn.ShouldBe(AliceDn);
    }

    [FactIfPg]
    public async Task StartTls_Upgrades_The_Connection_And_Starts_Over_Anonymous()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port, Trusted, fx.Certificate.Public);
        (await c.BindAsync(AliceDn, Password)).ShouldBe(LdapResult.Success);

        (await c.StartTlsAsync()).ShouldBe(LdapResult.Success);
        c.Secure.ShouldBeTrue();
        // The cleartext bind does not carry over into the TLS session.
        (await c.SearchAsync($"ou=people,{Base}", Filter.Present("objectClass")))
            .Code.ShouldBe(LdapResult.InsufficientAccessRights);
        (await c.BindAsync(AliceDn, Password)).ShouldBe(LdapResult.Success);

        (await c.ExtendedAsync(LdapOid.StartTls)).Code.ShouldBe(LdapResult.OperationsError, "already TLS");
    }

    [FactIfPg]
    public async Task Failed_Binds_Are_Limited_Per_Address_But_Not_For_A_Trusted_Peer()
    {
        // Failures against a DN that names nobody, so no account's lockout is touched.
        var nobody = $"uid=nobody_{Guid.NewGuid():N},ou=people,{Base}";
        await using (var c = await TestLdapClient.ConnectTlsAsync(fx.TlsPort, fx.Certificate.Public, Guesser))
        {
            for (var i = 0; i < FailuresPerMinute; i++)
                (await c.BindAsync(nobody, Password)).ShouldBe(LdapResult.InvalidCredentials);
            // Out of failures: refused before the password is looked at, right or not.
            (await c.BindAsync(AliceDn, Password)).ShouldBe(LdapResult.Busy);
        }

        // Another address has its own budget.
        await using (var c = await TestLdapClient.ConnectTlsAsync(fx.TlsPort, fx.Certificate.Public, Untrusted))
            (await c.BindAsync(AliceDn, Password)).ShouldBe(LdapResult.Success);

        // The trusted peer binds for everyone, so it has no budget to exhaust.
        await using (var c = await TestLdapClient.ConnectAsync(fx.Port, Trusted))
        {
            for (var i = 0; i < FailuresPerMinute + 2; i++)
                (await c.BindAsync(nobody, Password)).ShouldBe(LdapResult.InvalidCredentials);
            (await c.BindAsync(AliceDn, Password)).ShouldBe(LdapResult.Success);
        }
    }

    private string ServiceDn(string name) => $"cn={name},ou=services,{Base}";

    // A service account reads every user, so its credential is accepted only
    // from the deployment's own hosts, only under ou=services, and only for a
    // bot.
    [FactIfPg]
    public async Task Service_Accounts_Bind_Only_As_Bots_From_Trusted_Peers()
    {
        await using (var c = await TestLdapClient.ConnectAsync(fx.Port, Trusted))
        {
            (await c.BindAsync(ServiceDn(fx.Service), Password)).ShouldBe(LdapResult.Success);
            (await c.BindAsync($"uid={fx.Service},ou=people,{Base}", Password))
                .ShouldBe(LdapResult.InvalidCredentials, "a service is not a person");
            (await c.BindAsync(ServiceDn(fx.NotABot), Password))
                .ShouldBe(LdapResult.InvalidCredentials, "listed as a service, but not a bot");
        }
        await using (var c = await TestLdapClient.ConnectTlsAsync(fx.TlsPort, fx.Certificate.Public, Outsider))
        {
            (await c.BindAsync(ServiceDn(fx.Service), Password))
                .ShouldBe(LdapResult.InvalidCredentials, "the right password, from outside the trusted peers");
            (await c.BindAsync(AliceDn, Password)).ShouldBe(LdapResult.Success);
        }
    }

    [FactIfPg]
    public async Task A_Uid_Binds_Whatever_Its_Case()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port, Trusted);
        (await c.BindAsync($"uid={fx.Alice.ToUpperInvariant()},ou=people,{Base}", Password)).ShouldBe(LdapResult.Success);
    }

    // Before a bind, a connection from outside the trusted peers may send
    // small messages only: a length header announcing more is refused before
    // the server reads, or reserves, any of it.
    [FactIfPg]
    public async Task A_Large_Message_Before_A_Bind_Ends_The_Connection()
    {
        await using var c = await TestLdapClient.ConnectAsync(fx.Port, Untrusted);
        await c.SendRawAsync([0x30, 0x83, 0x01, 0x86, 0xA0]);   // a 100,000-byte SEQUENCE
        var notice = await c.ReceiveFrameOrCloseAsync();
        notice.ShouldNotBeNull("a notice of disconnection");
        (await c.ReceiveFrameOrCloseAsync()).ShouldBeNull();
    }

    [FactIfPg]
    public async Task One_Address_Cannot_Take_Every_Connection()
    {
        var open = new List<TestLdapClient>();
        try
        {
            for (var i = 0; i < 32; i++) open.Add(await TestLdapClient.ConnectAsync(fx.Port, Crowd));
            // Each one is served.
            (await open[^1].BindAsync("", "")).ShouldBe(LdapResult.Success);

            await using var extra = await TestLdapClient.ConnectAsync(fx.Port, Crowd);
            (await extra.ReceiveFrameOrCloseAsync()).ShouldBeNull("the 33rd connection is closed at once");

            await using var other = await TestLdapClient.ConnectAsync(fx.Port, Untrusted);
            (await other.BindAsync("", "")).ShouldBe(LdapResult.Success, "another address is unaffected");
        }
        finally
        {
            foreach (var c in open) await c.DisposeAsync();
        }
    }
}
