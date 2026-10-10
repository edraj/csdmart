namespace Dmart.DataAdapters.Sql;

// Whether user_addresses / user_services can be trusted. False only while a
// rebuild that has to happen has not yet succeeded. Readers that would answer
// from the index (the LDAP face's mail / mailAlias / authorizedService lookups)
// must say "unavailable" rather than "no such user" while it is false: an
// empty answer makes Postfix bounce mail with a permanent 550, where an
// unavailable one makes it defer and retry.
public sealed class DirectoryIndexStatus
{
    private volatile bool _ready = true;
    public bool Ready => _ready;
    internal void Set(bool ready) => _ready = ready;

    // A directory replica's sync state (Services/DirectoryReplica), as UTC
    // ticks of its last complete sync: -1 on anything that is not a replica,
    // 0 on a replica that has never completed one.
    private long _replicaSyncedAt = -1;
    private long _replicaMaxStaleness;   // ticks; 0 = no limit

    // False on a replica that has never completed a sync with its primary.
    // Its local users are then whatever an earlier run left, or none, so
    // EVERY lookup and bind answers `unavailable`, not just the index-backed
    // ones. True everywhere else, and on a replica restarted with a synced
    // copy on disk.
    public bool ReplicaSynced => Interlocked.Read(ref _replicaSyncedAt) != 0;

    // A synced replica whose last sync is older than its limit
    // (DIRECTORY_REPLICA_MAX_STALENESS_HOURS). Its copy may still hold a
    // password the primary has since changed, or an account it has deleted,
    // so binds answer `unavailable` until a sync succeeds. Lookups still
    // answer: mail keeps flowing to the mailboxes the replica knows.
    public bool ReplicaStale
    {
        get
        {
            var at = Interlocked.Read(ref _replicaSyncedAt);
            var max = Interlocked.Read(ref _replicaMaxStaleness);
            return at > 0 && max > 0 && DateTime.UtcNow.Ticks - at > max;
        }
    }

    public DateTime? ReplicaSyncedAt
        => Interlocked.Read(ref _replicaSyncedAt) is > 0 and var at ? new DateTime(at, DateTimeKind.Utc) : null;

    public TimeSpan ReplicaMaxStaleness => TimeSpan.FromTicks(Interlocked.Read(ref _replicaMaxStaleness));

    public bool IsReplica => Interlocked.Read(ref _replicaSyncedAt) >= 0;

    // Called before the replica's first await, so nothing reads the "not a
    // replica" default in between.
    internal void BeginReplica(TimeSpan maxStaleness)
    {
        Interlocked.Exchange(ref _replicaMaxStaleness, Math.Max(0, maxStaleness.Ticks));
        Interlocked.Exchange(ref _replicaSyncedAt, 0);
    }

    internal void SetReplicaSyncedAt(DateTime utc)
        => Interlocked.Exchange(ref _replicaSyncedAt, DateTime.SpecifyKind(utc, DateTimeKind.Utc).Ticks);
}

// Rebuilds user_addresses / user_services from users when they are empty but
// some live user has directory fields (docs/user-directory-fields.md): the
// first start after the upgrade that added them, or a restore that loaded users
// through a path predating the index. Every ordinary write maintains the
// tables itself, so on any other boot this is one cheap probe.
//
// It runs after the schema initializers, which create the tables, and before
// the LDAP listener, which is registered later — hosted services start in
// order. If the rebuild fails it does not stop the host: the status flips to
// not-ready, index-backed lookups answer `unavailable`, and the rebuild is
// retried every minute until it succeeds.
public sealed class DirectoryIndexRepair(
    IDbConnectionFactory db, UserRepository users, DirectoryIndexStatus status,
    ILogger<DirectoryIndexRepair> log) : IHostedService
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(1);
    // Created only when a retry loop is needed, and released by StopAsync.
    // Not owned for the service's lifetime: the host may stop a hosted service
    // after its container has disposed it, and a second stop must be a no-op.
    private CancellationTokenSource? _stopping;
    private Task? _retries;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!db.IsConfigured) return;
        if (await TryRebuildAsync(cancellationToken)) return;

        status.Set(false);
        var stopping = _stopping = new CancellationTokenSource();
        _retries = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try { await Task.Delay(RetryEvery, stopping.Token); }
                catch (OperationCanceledException) { return; }
                if (await TryRebuildAsync(stopping.Token))
                {
                    status.Set(true);
                    return;
                }
            }
        }, CancellationToken.None);
    }

    private async Task<bool> TryRebuildAsync(CancellationToken ct)
    {
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            var rows = await users.RebuildDirectoryIndexIfEmptyAsync(ct);
            if (rows > 0)
                log.LogInformation("Rebuilt the user directory index: {Rows} address and service row(s) in {Seconds:F1}s",
                    rows, TimeProvider.System.GetElapsedTime(started).TotalSeconds);
            return true;
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or TimeoutException)
        {
            // A clash means two users already hold the same address in their
            // stored rows — possible only for data written around the
            // repository — and the operator has to decide who keeps it.
            log.LogError(ex,
                "rebuilding the user directory index failed; lookups by address and service answer "
                + "'unavailable' until it succeeds (retrying every {Minutes} min)", RetryEvery.TotalMinutes);
            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var stopping = Interlocked.Exchange(ref _stopping, null);
        if (stopping is null) return;
        await stopping.CancelAsync();
        if (_retries is not null) await _retries.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        stopping.Dispose();
    }
}
