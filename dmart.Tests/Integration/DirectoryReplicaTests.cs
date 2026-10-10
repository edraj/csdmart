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
using FeedService = Dmart.Services.DirectoryFeedService;
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
        // The uuid came across too, and finds the user on either engine.
        (await PrimaryUsers.GetShortnameByUuidAsync(alice.Uuid)).ShouldBe(fx.Alice);
        (await ReplicaUsers.GetShortnameByUuidAsync(alice.Uuid)).ShouldBe(fx.Alice);
        (await PrimaryUsers.GetShortnameByUuidAsync("not-a-uuid")).ShouldBeNull();
        alice.Mailbox.ShouldBe(fx.AliceMailbox);
        alice.Services.ShouldBe(new[] { "mail" });
        alice.Groups.ShouldBe(new[] { fx.Group });
        (await ReplicaUsers.FindAddressOwnerAsync(fx.AliceMailbox)).ShouldBe((fx.Alice, "mailbox"));

        // The replica answers binds itself, with the primary's hash.
        var replicaUsers = fx.Replica.Services.GetRequiredService<UserService>();
        (await replicaUsers.VerifyDirectoryBindAsync(fx.Alice, Password)).User.ShouldNotBeNull();
        (await replicaUsers.VerifyDirectoryBindAsync(fx.Alice, "Wrong12345")).User.ShouldBeNull();

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
        (await replicaUsers.VerifyDirectoryBindAsync(fx.Alice, "Changed12345")).User.ShouldNotBeNull();
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

    // A replica on a host of its own, whose primary never answers, over the
    // SQLite file `db` (kept between hosts, as a restart keeps it).
    private WebApplicationFactory<Program> CutOffReplica(string db, int ldapPort, int maxStalenessHours = 24)
        => fx.Primary.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dmart:DatabaseDriver"] = "sqlite",
                ["Dmart:SqlitePath"] = db,
                ["Dmart:PostgresConnection"] = null,
                ["Dmart:DatabaseHost"] = null,
                ["Dmart:DatabasePassword"] = null,
                ["Dmart:DatabaseName"] = null,
                ["Dmart:DirectoryReplicaOf"] = "http://unreachable.test/",
                ["Dmart:DirectoryReplicaShortname"] = fx.Reader,
                ["Dmart:DirectoryReplicaPassword"] = Password,
                ["Dmart:DirectoryReplicaIntervalSeconds"] = "3600",
                ["Dmart:DirectoryReplicaMaxStalenessHours"] = maxStalenessHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Dmart:LdapPort"] = ldapPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Dmart:LdapBaseDn"] = "dc=cutoff",
            }));
            b.ConfigureServices(s => s.AddHttpClient(DirectoryReplica.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new RefusingHandler()));
        });

    // A replica restarted while its primary is down serves the copy it
    // synced before, until that copy is older than its staleness limit. Past
    // it, binds answer `unavailable` and lookups still answer.
    [FactIfPg]
    public async Task A_Restarted_Replica_Serves_Its_Copy_Until_It_Is_Too_Old()
    {
        var db = Path.Combine(Path.GetTempPath(), $"dmart-cutoff-{Guid.NewGuid():N}.db");
        var hash = await fx.Primary.Services.GetRequiredService<PasswordHasher>().HashAsync(Password);
        var alice = Fixture.NewUser("cutalice_" + Guid.NewGuid().ToString("N")[..6], hash, UserType.Web);
        try
        {
            // The first run: a copy, synced an hour ago.
            await using (var first = CutOffReplica(db, Fixture.FreePort()))
            {
                _ = first.CreateClient();
                var users = first.Services.GetRequiredService<UserRepository>();
                await users.UpsertReplicatedAsync(alice);
                await users.SetReplicaWatermarkAsync(TimeUtils.Now().AddHours(-1), DateTime.UtcNow.AddHours(-1));
            }

            var port = Fixture.FreePort();
            await using (var restarted = CutOffReplica(db, port))
            {
                _ = restarted.CreateClient();
                var status = restarted.Services.GetRequiredService<DirectoryIndexStatus>();
                for (var i = 0; i < 50 && !status.ReplicaSynced; i++) await Task.Delay(100);
                status.ReplicaSynced.ShouldBeTrue("the copy on disk serves without the primary");
                status.ReplicaStale.ShouldBeFalse();
                await using var c = await TestLdapClient.ConnectAsync(port);
                (await c.BindAsync($"uid={alice.Shortname},ou=people,dc=cutoff", Password)).ShouldBe(LdapResult.Success);
            }

            // The same copy, against a limit it has outlived.
            port = Fixture.FreePort();
            await using (var stale = CutOffReplica(db, port, maxStalenessHours: 1))
            {
                var http = stale.CreateClient();
                var status = stale.Services.GetRequiredService<DirectoryIndexStatus>();
                for (var i = 0; i < 50 && !status.ReplicaSynced; i++) await Task.Delay(100);
                status.ReplicaStale.ShouldBeTrue();
                await using var c = await TestLdapClient.ConnectAsync(port);
                (await c.BindAsync($"uid={alice.Shortname},ou=people,dc=cutoff", Password)).ShouldBe(LdapResult.Unavailable);
                (await http.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            }
        }
        finally
        {
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                try { File.Delete(f); } catch { }
        }
    }

    // A replica and its primary may run different engines; PostgreSQL's
    // default collation sorts 'Bob' between 'alice' and 'carol', SQLite's
    // bytes put it first. The full walk's ranges must mean the same on both,
    // or the replica deletes users that fall between them.
    [FactIfPg]
    public async Task Keyset_Walks_Order_Shortnames_Bytewise_On_Both_Engines()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var names = new[] { $"Zbyte{tag}", $"abyte{tag}", $"Mbyte{tag}", $"_byte{tag}" };
        var hash = await fx.Primary.Services.GetRequiredService<PasswordHasher>().HashAsync(Password);
        try
        {
            foreach (var n in names) await PrimaryUsers.UpsertAsync(Fixture.NewUser(n, hash, UserType.Web));
            var walked = (await PrimaryUsers.ListShortnamesBetweenAsync("", null)).Where(names.Contains).ToList();
            walked.ShouldBe(names.Order(StringComparer.Ordinal).ToList());

            var listed = new List<string>();
            string? after = null;
            while (true)
            {
                var page = await PrimaryUsers.ListForDirectoryAsync(after, 500);
                listed.AddRange(page.Select(u => u.Shortname).Where(names.Contains));
                if (page.Count < 500) break;
                after = page[^1].Shortname;
            }
            listed.ShouldBe(names.Order(StringComparer.Ordinal).ToList());
        }
        finally
        {
            foreach (var n in names) await TestUserCleanup.DeleteUserAndOwnedAsync(fx.Primary.Services, n);
        }
    }

    // The feed hands out password hashes: only to a listed BOT, and with
    // nothing a replica does not serve.
    [FactIfPg]
    public async Task The_Feed_Reads_Only_For_Listed_Bots_And_Carries_Only_What_A_Replica_Serves()
    {
        var sp = fx.Primary.Services;
        var person = "replperson_" + Guid.NewGuid().ToString("N")[..6];
        var hash = await sp.GetRequiredService<PasswordHasher>().HashAsync(Password);
        await PrimaryUsers.UpsertAsync(Fixture.NewUser(person, hash, UserType.Web) with
        {
            Notes = "admin notes", DeviceId = "device-1", GoogleId = "g-1",
            LastLogin = new() { ["at"] = "yesterday" },
        });
        try
        {
            FeedService MakeFeed(string readers) => new(sp.GetRequiredService<UserRepository>(),
                sp.GetRequiredService<AccessRepository>(), sp.GetRequiredService<IDbConnectionFactory>(),
                Microsoft.Extensions.Options.Options.Create(new DmartSettings { DirectoryFeedReaders = readers }));
            (await MakeFeed(person).MayReadAsync(person, CancellationToken.None)).ShouldBeFalse("listed, but a person");
            (await MakeFeed(fx.Reader).MayReadAsync(fx.Reader, CancellationToken.None)).ShouldBeTrue();

            var feed = MakeFeed(fx.Reader);
            DirectoryFeedUser? fed = null;
            string? after = null;
            while (fed is null)
            {
                var page = await feed.FullAsync(after, DirectoryFeedService.MaxPage, CancellationToken.None);
                fed = page.Users.FirstOrDefault(u => u.User.Shortname == person);
                if (!page.More) break;
                after = page.After;
            }
            fed.ShouldNotBeNull();
            fed!.PasswordHash.ShouldBe(hash);
            fed.User.Notes.ShouldBeNull();
            fed.User.DeviceId.ShouldBeNull();
            fed.User.GoogleId.ShouldBeNull();
            fed.User.LastLogin.ShouldBeNull();
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(sp, person);
        }
    }

    // A group's owner must be a local user; one the feed never carries (soft
    // deleted on the primary) is replaced by the replica's own account rather
    // than failing the whole sync.
    [FactIfPg]
    public async Task A_Group_Whose_Owner_The_Replica_Lacks_Still_Arrives()
    {
        var owner = "replowner_" + Guid.NewGuid().ToString("N")[..6];
        var group = "replogrp_" + Guid.NewGuid().ToString("N")[..6];
        var hash = await fx.Primary.Services.GetRequiredService<PasswordHasher>().HashAsync(Password);
        var access = fx.Primary.Services.GetRequiredService<AccessRepository>();
        await PrimaryUsers.UpsertAsync(Fixture.NewUser(owner, hash, UserType.Web));
        await access.UpsertGroupAsync(new Group
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = group, SpaceName = "management", Subpath = "/groups",
            OwnerShortname = owner, IsActive = true, CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });
        await PrimaryUsers.SoftDeleteAsync(owner);
        try
        {
            await SyncAsync();
            var copy = await fx.Replica.Services.GetRequiredService<AccessRepository>().GetGroupAsync(group);
            copy.ShouldNotBeNull();
            copy!.OwnerShortname.ShouldBe(fx.Reader);
        }
        finally
        {
            try { await access.DeleteGroupAsync(group); } catch { }
            await TestUserCleanup.DeleteUserAndOwnedAsync(fx.Primary.Services, owner);
        }
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }
}
