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
    private volatile bool _replicaSynced = true;
    public bool Ready => _ready;
    internal void Set(bool ready) => _ready = ready;

    // On a directory replica (Services/DirectoryReplica): false until its
    // first sync with the primary completes. Until then the local users are
    // whatever an earlier run left, or none, so EVERY lookup and bind answers
    // `unavailable`, not just the index-backed ones. True everywhere else.
    public bool ReplicaSynced => _replicaSynced;
    internal void SetReplicaSynced(bool synced) => _replicaSynced = synced;
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
