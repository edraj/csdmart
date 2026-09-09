namespace Dmart.DataAdapters.Sql;

// One-shot upgrade repair for accounts locked by the PRE-counter-only release.
//
// The old auto-lockout wrote two things when an account crossed
// MAX_FAILED_LOGIN_ATTEMPTS: it set attempt_count at/above the threshold AND
// flipped is_active to false. The lock is the counter alone now, and is_active
// means "an admin deactivated this account" — which the cool-down auto-unlock
// deliberately refuses to touch (UserRepository.UnlockAfterCooldownAsync).
//
// Those two rules meet badly on an upgraded database. A row the old release
// locked still carries is_active=false; the cool-down clears its counter, then
// RejectIfNotActive rejects it on the is_active flag instead — with
// USER_ACCOUNT_LOCKED, forever, on every future attempt. The account is locked
// out permanently and no login-side path can recover it.
//
// The repair matches the old lock's signature: `is_active = false AND
// attempt_count >= MAX_FAILED_LOGIN_ATTEMPTS`. Deactivating an ACTIVE account
// cannot produce that pair, because RejectIfNotActive runs before the credential
// check in LoginAsync — a deactivated account never reaches the counter to raise
// it. (Deactivating an already-locked one can; see the caveat at the bottom.)
//
// Safe to run at boot, which is where it runs (LegacyLockoutRepair), because
// no current code path can produce that pair: the managed user update clears
// attempt_count when it deactivates an account, exactly as it clears it when it
// reactivates one, and nothing else writes is_active=false on a user. So the
// signature belongs to the old writer alone, the repair heals the database on
// the first boot after an upgrade, and every boot after that is a no-op.
//
// It stays callable from `dmart migrate` too, for operators who would rather
// keep startup read-only (RepairLegacyLockoutsOnStart=false) or want to see the
// count before the server accepts traffic.
//
// The one case it cannot get right is inherent to the data, not to the timing.
// The old release deactivated an account when it locked it, so an admin who
// looked at one of those and decided "this one stays off" left a row identical
// to the ones nobody ever looked at — same two columns, and nothing recording
// who set the flag or why. Those get reactivated. The alternative is to repair
// nothing and strand every genuinely auto-locked user, which is both worse and
// far more common; the startup log names the count so an operator can audit.
//
// Timing matters in the other direction, though, and it is why this runs at
// startup. A locked-out user who retries once past the cool-down has their
// counter cleared by RejectIfAttemptLockedAsync — the row keeps is_active=false
// but drops below the threshold, so it stops matching and the account becomes
// unrepairable. Hosted services complete before the host begins listening, so
// nothing can retry in front of the repair. An operator who disables it and
// serves traffic before running `dmart migrate` loses exactly those rows.
internal static class LegacyLockoutBackfill
{
    // Returns the number of accounts reactivated. Idempotent in practice: the
    // rows it matches no longer match after it runs.
    public static async Task<int> RunAsync(
        IDbConnectionFactory db, int maxFailedLoginAttempts, CancellationToken ct = default)
    {
        // With the lockout disabled there is no threshold to compare against
        // and no legacy lock to undo.
        if (maxFailedLoginAttempts <= 0) return 0;

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("""
            UPDATE users
               SET is_active = $1, attempt_count = 0, last_failed_login = NULL
             WHERE is_active = $2 AND attempt_count >= $3
            """);
        DbParams.Add(cmd, true);
        DbParams.Add(cmd, false);
        DbParams.Add(cmd, maxFailedLoginAttempts);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
