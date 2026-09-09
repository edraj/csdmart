using Microsoft.Extensions.Options;

namespace Dmart.DataAdapters.Sql;

// Runs LegacyLockoutBackfill once the schema is current. Registered AFTER both
// schema initializers — IHostedServices start sequentially in registration
// order — because on a fresh database the `users` table does not exist until
// they have run.
//
// Driver-agnostic: it goes through IDbConnectionFactory rather than picking a
// backend, so the PostgreSQL and SQLite tiers get the same repair from one
// place.
//
// Startup must not fail on this. The repair is a data fix for an upgrade
// hazard, not a precondition for serving traffic, so a database error is logged
// and the host carries on — an operator can always run `dmart migrate`.
public sealed class LegacyLockoutRepair(
    IDbConnectionFactory db,
    IOptions<Dmart.Config.DmartSettings> settings,
    ILogger<LegacyLockoutRepair> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!settings.Value.RepairLegacyLockoutsOnStart) return;
        if (!db.IsConfigured) return;

        try
        {
            var reactivated = await LegacyLockoutBackfill.RunAsync(
                db, settings.Value.MaxFailedLoginAttempts, cancellationToken);

            // Silent on the overwhelmingly common path (nothing to repair) —
            // logged loudly when it does act, because reactivating accounts is
            // a security-relevant change an operator must be able to see.
            if (reactivated > 0)
                log.LogWarning(
                    "Reactivated {Count} account(s) auto-locked by a pre-1.5.6 release: "
                    + "is_active restored, attempt_count cleared. The lockout is the "
                    + "counter alone now; is_active means admin deactivation.",
                    reactivated);
        }
        catch (System.Data.Common.DbException ex)
        {
            log.LogError(ex, "legacy lockout repair failed; run `dmart migrate` to retry");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
