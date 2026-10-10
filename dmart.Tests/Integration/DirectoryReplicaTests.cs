using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Ldap;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Dmart.Tests.Infrastructure;
using Dmart.Utils;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// A directory replica (docs/directory-replica.md) against a primary: the
// primary is the suite's database on whichever engine the leg runs, the
// replica a second host on its own SQLite file, reaching the primary's feed
// through the primary's in-memory server.
public sealed class DirectoryReplicaTests(DirectoryReplicaTests.Fixture fx) : IClassFixture<DirectoryReplicaTests.Fixture>
{
    private const string Password = "Repl12345";

    public sealed class Fixture : IAsyncLifetime
    {
        private readonly DmartFactory _factory = new();
        public DmartFactory Factory => _factory;
        private readonly string _replicaDb = Path.Combine(Path.GetTempPath(), $"dmart-replica-{Guid.NewGuid():N}.db");
        public WebApplicationFactory<Program> Primary { get; private set; } = null!;
        public WebApplicationFactory<Program> Replica { get; private set; } = null!;
        public int ReplicaLdapPort { get; private set; }

        private static readonly string Suffix = Guid.NewGuid().ToString("N")[..6];
        public string Reader { get; } = "replbot_" + Suffix;
        public string Alice { get; } = "replalice_" + Suffix;
        public string Bob { get; } = "replbob_" + Suffix;       // soft-deleted later
        public string Carol { get; } = "replcarol_" + Suffix;   // hard-deleted later
        public string Dave { get; } = "repldave_" + Suffix;     // renamed later
        public string Group { get; } = "replgrp_" + Suffix;
        public string AliceMailbox => $"{Alice}@hosted.test";

        public async Task InitializeAsync()
        {
            await ((IAsyncLifetime)_factory).InitializeAsync();
            if (!DmartFactory.HasPg) return;

            Primary = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.Configure<DmartSettings>(o => o.DirectoryFeedReaders = Reader)));
            _ = Primary.CreateClient();

            var users = Primary.Services.GetRequiredService<UserRepository>();
            var hash = await Primary.Services.GetRequiredService<PasswordHasher>().HashAsync(Password);
            await Primary.Services.GetRequiredService<AccessRepository>().UpsertGroupAsync(new Group
            {
                Uuid = Guid.NewGuid().ToString(), Shortname = Group, SpaceName = "management", Subpath = "/groups",
                OwnerShortname = "dmart", IsActive = true, CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
            });
            await users.UpsertAsync(NewUser(Reader, hash, UserType.Bot));
            await users.UpsertAsync(NewUser(Alice, hash, UserType.Web) with
            {
                Mailbox = AliceMailbox, MailAliases = [$"help-{Alice}@hosted.test"], Services = ["mail"], Groups = [Group],
            });
            foreach (var u in new[] { Bob, Carol, Dave }) await users.UpsertAsync(NewUser(u, hash, UserType.Web));

            ReplicaLdapPort = FreePort();
            Replica = Primary.WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Its own database, whichever engine the primary runs on.
                    ["Dmart:DatabaseDriver"] = "sqlite",
                    ["Dmart:SqlitePath"] = _replicaDb,
                    ["Dmart:PostgresConnection"] = null,
                    ["Dmart:DatabaseHost"] = null,
                    ["Dmart:DatabasePassword"] = null,
                    ["Dmart:DatabaseName"] = null,
                    ["Dmart:DirectoryFeedReaders"] = "",
                    ["Dmart:DirectoryReplicaOf"] = "http://primary.test/",
                    ["Dmart:DirectoryReplicaShortname"] = Reader,
                    ["Dmart:DirectoryReplicaPassword"] = Password,
                    // The first sync runs at startup; the tests drive the rest.
                    ["Dmart:DirectoryReplicaIntervalSeconds"] = "3600",
                    ["Dmart:LdapPort"] = ReplicaLdapPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Dmart:LdapBaseDn"] = "dc=replica",
                }));
                b.ConfigureServices(s => s.AddHttpClient(DirectoryReplica.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Primary.Server.CreateHandler()));
            });
            _ = Replica.CreateClient();

            var status = Replica.Services.GetRequiredService<DirectoryIndexStatus>();
            for (var i = 0; i < 300 && !status.ReplicaSynced; i++) await Task.Delay(100);
            if (!status.ReplicaSynced)
                // Surfaces the reason: the background loop only logs it.
                await Replica.Services.GetRequiredService<DirectoryReplica>().SyncOnceAsync(CancellationToken.None);
        }

        internal static User NewUser(string shortname, string hash, UserType type) => new()
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = shortname, SpaceName = "management", Subpath = "/users",
            OwnerShortname = shortname, IsActive = true, Password = hash, Type = type, Language = Language.En,
            Roles = new(), Groups = new(), CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        };

        internal static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public async Task DisposeAsync()
        {
            if (Replica is not null) await Replica.DisposeAsync();
            if (Primary is not null)
            {
                foreach (var u in new[] { Reader, Alice, Bob, Carol, Dave, Dave + "x" })
                    await TestUserCleanup.DeleteUserAndOwnedAsync(Primary.Services, u);
                try { await Primary.Services.GetRequiredService<AccessRepository>().DeleteGroupAsync(Group); } catch { }
                await Primary.DisposeAsync();
            }
            await ((IAsyncLifetime)_factory).DisposeAsync();
            foreach (var f in new[] { _replicaDb, _replicaDb + "-wal", _replicaDb + "-shm" })
                try { File.Delete(f); } catch { }
        }
    }

    private UserRepository PrimaryUsers => fx.Primary.Services.GetRequiredService<UserRepository>();
    private UserRepository ReplicaUsers => fx.Replica.Services.GetRequiredService<UserRepository>();
    private Task SyncAsync() => fx.Replica.Services.GetRequiredService<DirectoryReplica>().SyncOnceAsync(CancellationToken.None);

    [FactIfPg]
    public async Task The_First_Sync_Copies_Users_With_Their_Hashes_Fields_And_Groups()
    {
        var alice = (await ReplicaUsers.GetByShortnameAsync(fx.Alice)).ShouldNotBeNull();
        alice.Mailbox.ShouldBe(fx.AliceMailbox);
        alice.Services.ShouldBe(new[] { "mail" });
        alice.Groups.ShouldBe(new[] { fx.Group });
        (await ReplicaUsers.FindAddressOwnerAsync(fx.AliceMailbox)).ShouldBe((fx.Alice, "mailbox"));

        // The replica answers binds itself, with the primary's hash.
        var replicaUsers = fx.Replica.Services.GetRequiredService<UserService>();
        (await replicaUsers.VerifyDirectoryBindAsync(fx.Alice, Password)).ShouldNotBeNull();
        (await replicaUsers.VerifyDirectoryBindAsync(fx.Alice, "Wrong12345")).ShouldBeNull();

        (await fx.Replica.Services.GetRequiredService<AccessRepository>().GetGroupAsync(fx.Group)).ShouldNotBeNull();
        (await ReplicaUsers.GetReplicaWatermarkAsync()).ShouldNotBeNull();

        // Over LDAP, as Dovecot on the replica host would ask.
        await using var c = await TestLdapClient.ConnectAsync(fx.ReplicaLdapPort);
        (await c.BindAsync($"uid={fx.Alice},ou=people,dc=replica", Password)).ShouldBe(LdapResult.Success);
    }

    [FactIfPg]
    public async Task Changes_Deletions_And_Renames_On_The_Primary_Reach_The_Replica()
    {
        var hash = await fx.Primary.Services.GetRequiredService<PasswordHasher>().HashAsync("Changed12345");
        var alice = (await PrimaryUsers.GetByShortnameAsync(fx.Alice))!;
        await PrimaryUsers.UpsertAsync(alice with { Services = ["mail", "gitea"], Password = hash, UpdatedAt = TimeUtils.Now() });
        await PrimaryUsers.SoftDeleteAsync(fx.Bob);
        await PrimaryUsers.DeleteAsync(fx.Carol);
        (await PrimaryUsers.RenameAsync(fx.Dave, fx.Dave + "x")).ShouldBeTrue();
        // A lockout counted on the replica stays the replica's.
        await ReplicaUsers.UpsertAsync((await ReplicaUsers.GetByShortnameAsync(fx.Alice))! with { AttemptCount = 2 });

        await SyncAsync();

        var copy = (await ReplicaUsers.GetByShortnameAsync(fx.Alice))!;
        copy.Services.ShouldBe(new[] { "mail", "gitea" });
        copy.AttemptCount.ShouldBe(2);
        var replicaUsers = fx.Replica.Services.GetRequiredService<UserService>();
        (await replicaUsers.VerifyDirectoryBindAsync(fx.Alice, "Changed12345")).ShouldNotBeNull();
        (await ReplicaUsers.GetByShortnameAsync(fx.Bob)).ShouldBeNull();
        (await ReplicaUsers.GetByShortnameAsync(fx.Carol)).ShouldBeNull();
        (await ReplicaUsers.GetByShortnameAsync(fx.Dave)).ShouldBeNull();
        (await ReplicaUsers.GetByShortnameAsync(fx.Dave + "x")).ShouldNotBeNull();
    }

    [FactIfPg]
    public async Task The_Feed_Answers_Only_Its_Listed_Readers()
    {
        var admin = await fx.Factory.CreateLoggedInUserAsync(fx.Primary);
        try
        {
            var resp = await admin.Client.GetAsync("/managed/directory-feed?mode=full&limit=1");
            resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a super admin is not a feed reader");
        }
        finally { await admin.Cleanup(); }

        using var anonymous = fx.Primary.CreateClient();
        (await anonymous.GetAsync("/managed/directory-feed?mode=full")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [FactIfPg]
    public async Task An_Unsynced_Replica_Answers_Unavailable_Not_No_Such_User()
    {
        // A replica whose primary cannot be reached never completes a sync.
        var port = Fixture.FreePort();
        await using var orphan = fx.Primary.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dmart:DatabaseDriver"] = "sqlite",
                ["Dmart:SqlitePath"] = Path.Combine(Path.GetTempPath(), $"dmart-orphan-{Guid.NewGuid():N}.db"),
                ["Dmart:PostgresConnection"] = null,
                ["Dmart:DatabaseHost"] = null,
                ["Dmart:DatabasePassword"] = null,
                ["Dmart:DatabaseName"] = null,
                ["Dmart:DirectoryReplicaOf"] = "http://unreachable.test/",
                ["Dmart:DirectoryReplicaShortname"] = fx.Reader,
                ["Dmart:DirectoryReplicaPassword"] = Password,
                ["Dmart:DirectoryReplicaIntervalSeconds"] = "3600",
                ["Dmart:LdapPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Dmart:LdapBaseDn"] = "dc=orphan",
            }));
            b.ConfigureServices(s => s.AddHttpClient(DirectoryReplica.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new RefusingHandler()));
        });
        _ = orphan.CreateClient();
        orphan.Services.GetRequiredService<DirectoryIndexStatus>().ReplicaSynced.ShouldBeFalse();

        await using var c = await TestLdapClient.ConnectAsync(port);
        (await c.BindAsync($"uid={fx.Alice},ou=people,dc=orphan", Password)).ShouldBe(LdapResult.Unavailable);
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }
}
