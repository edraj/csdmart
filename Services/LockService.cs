using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

public sealed class LockService(
    LockRepository locks,
    EntryRepository entries,
    EntryService entryService,
    HistoryRepository history,
    PluginManager plugins,
    PermissionService perms,
    IOptions<DmartSettings> settings,
    ILogger<LockService> log)
{
    // Event for the lock/unlock before/after pipeline. Mirrors EntryService's
    // BuildEvent shape so the SpaceEventLogger audit line and any hook plugin
    // see the same Locator block they get for ordinary CRUD.
    private static Event BuildEvent(Locator l, ActionType action, string actor) => new()
    {
        SpaceName = l.SpaceName,
        Subpath = l.Subpath,
        Shortname = l.Shortname,
        ActionType = action,
        ResourceType = l.Type,
        UserShortname = actor,
    };

    public async Task<Response> LockAsync(Locator l, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(actor))
            return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);

        // Load the target entry once — it supplies the permission context (owner
        // / ACL / is_active) and, for tickets, the processed_by marker below.
        // Null when the entry doesn't exist: the gate falls back to a
        // role/subpath-level check (CanAsync tolerates a null resource).
        var entry = await entries.GetAsync(l.SpaceName, l.Subpath, l.Shortname, l.Type, ct);

        // Permission gate: the fine-grained `lock` action OR plain `update`
        // access. `lock` is a new action no pre-existing permission carries,
        // so update must keep implying it or every upgraded deployment loses
        // locking (Python gates the lock route on authentication only; once
        // locks block writes we want at least "may mutate the entry" here).
        // Checked before before_action (EntryService convention) so an
        // unauthorized caller never fires plugin hooks.
        var resourceCtx = entry is null ? null : PermissionService.FromEntry(entry);
        if (!await perms.CanAsync(actor, "lock", l, resourceCtx, null, ct) &&
            !await perms.CanAsync(actor, "update", l, resourceCtx, null, ct))
            return Response.Fail(InternalErrorCode.NOT_ALLOWED, "no lock access", ErrorTypes.Auth);

        // before_action lets a hook plugin veto the lock (Python's
        // plugin_manager.before_action at the top of lock_entry). A rejection
        // surfaces as a failed Response rather than a 500.
        if (await BeforeActionAsync(l, ActionType.Lock, actor, ct) is { } lockBefore)
            return lockBefore;

        // Python ticket locks mark the current processor before taking the
        // lock. Keep the lock operation useful for ticket UIs that read
        // collaborators.processed_by.
        if (l.Type == ResourceType.Ticket)
        {
            var ticket = entry;
            if (ticket is not null)
            {
                var collaborators = ticket.Collaborators is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(ticket.Collaborators);
                if (!string.Equals(collaborators.GetValueOrDefault("processed_by"), actor, StringComparison.Ordinal))
                {
                    collaborators["processed_by"] = actor;
                    var updated = await entryService.UpdateAsync(l,
                        new Dictionary<string, object> { ["collaborators"] = collaborators },
                        actor, ct);
                    if (!updated.IsOk)
                        return Response.Fail(updated.ErrorCode, updated.ErrorMessage!,
                            updated.ErrorType ?? ErrorTypes.Request);
                }
            }
        }

        var period = settings.Value.LockPeriod;
        var outcome = await locks.TryLockAsync(l.SpaceName, l.Subpath, l.Shortname, actor, period, ct);
        if (outcome == LockOutcome.Denied)
        {
            var holder = await locks.GetLockerAsync(l.SpaceName, l.Subpath, l.Shortname, period, ct);
            return Response.Fail(InternalErrorCode.LOCKED_ENTRY, $"already locked by {holder}", ErrorTypes.Db);
        }

        // History + after_action mirror Python's store_entry_diff({lock_type})
        // and plugin_manager.after_action. lock_type distinguishes a fresh lock
        // from a same-owner refresh, matching the redis reference's lock/extend.
        var lockType = outcome == LockOutcome.Extended ? "extend" : "lock";
        await WriteHistoryAsync(l, actor, lockType, ct);
        await plugins.AfterActionAsync(BuildEvent(l, ActionType.Lock, actor), ct);

        // Include lock_period so clients know how long they can hold the
        // lock before refreshing. Matches Python's /managed/lock response.
        return Response.Ok(attributes: new()
        {
            ["locked_by"] = actor,
            ["lock_period"] = period,
        });
    }

    public async Task<Response> UnlockAsync(Locator l, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(actor))
            return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);

        // Fast path: the holder releases their own lock — a single
        // owner-predicate DELETE, the pre-enforcement cost, and race-free (it
        // can only ever remove the caller's own row). A self-release is
        // authorized by definition and cannot be vetoed by a before-hook;
        // after_action still observes it.
        if (await locks.UnlockAsync(l.SpaceName, l.Subpath, l.Shortname, actor, ct))
        {
            await WriteHistoryAsync(l, actor, "cancel", ct);
            await plugins.AfterActionAsync(BuildEvent(l, ActionType.Unlock, actor), ct);
            return Response.Ok();
        }

        // 0 rows: no live lock (never locked, or the lease expired) → nothing
        // to release; unlock is an idempotent success.
        var period = settings.Value.LockPeriod;
        var holder = await locks.GetLockerAsync(l.SpaceName, l.Subpath, l.Shortname, period, ct);
        if (holder is null)
            return Response.Ok();

        // Force path: a lock held by someone else may only be released by a
        // caller granted the `unlock` action. The unlock route carries no
        // resource type (Python parity), so resolve the entry's actual type —
        // otherwise the gate would always ask about the wire default and a
        // grant scoped to any other resource type could never (or wrongly)
        // authorize the force. Locks may outlive their entry; a missing entry
        // keeps the wire default and the role/subpath-level check.
        var entry = await entries.GetAsync(l.SpaceName, l.Subpath, l.Shortname, ct);
        if (entry is not null)
            l = l with { Type = entry.ResourceType };
        if (!await perms.CanAsync(actor, "unlock", l,
                entry is null ? null : PermissionService.FromEntry(entry), null, ct))
            return Response.Fail(InternalErrorCode.NOT_ALLOWED, "no unlock access", ErrorTypes.Auth);

        // before_action runs after authorization (EntryService convention); a
        // hook plugin may still veto the force-unlock.
        if (await BeforeActionAsync(l, ActionType.Unlock, actor, ct) is { } unlockBefore)
            return unlockBefore;

        // Predicate the force delete on the holder we authorized against — a
        // lock that changed hands (or was re-acquired) in the meantime is
        // never deleted out from under its new owner.
        if (!await locks.UnlockAsync(l.SpaceName, l.Subpath, l.Shortname, holder, ct))
        {
            var current = await locks.GetLockerAsync(l.SpaceName, l.Subpath, l.Shortname, period, ct);
            // Lock vanished → nothing remains locked, idempotent success.
            // Lock swapped to a new holder → surface the conflict, don't delete.
            return current is null
                ? Response.Ok()
                : Response.Fail(InternalErrorCode.LOCKED_ENTRY,
                    $"lock holder changed to {current}; retry", ErrorTypes.Db);
        }

        // Python records LockAction.cancel in the unlock history diff.
        await WriteHistoryAsync(l, actor, "cancel", ct);
        await plugins.AfterActionAsync(BuildEvent(l, ActionType.Unlock, actor), ct);
        return Response.Ok();
    }

    // Fires the before-action pipeline; returns a failed Response when a hook
    // rejects (matching EntryService's guarded BeforeActionAsync), else null.
    private async Task<Response?> BeforeActionAsync(Locator l, ActionType action, string actor, CancellationToken ct)
    {
        try
        {
            await plugins.BeforeActionAsync(BuildEvent(l, action, actor), ct);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "before-{Action} plugin hook rejected {Space}/{Subpath}/{Shortname}",
                action, l.SpaceName, l.Subpath, l.Shortname);
            return Response.Fail(InternalErrorCode.INVALID_DATA,
                $"plugin rejected {action.ToString().ToLowerInvariant()}", ErrorTypes.Request);
        }
    }

    // Records the lock/unlock action in the history table, mirroring Python's
    // store_entry_diff(..., {"lock_type": ...}). Subpath is already the
    // leading-slash form (Locator-normalized), matching EntryService history rows.
    // Guarded: by the time this runs the lock table mutation has committed, and
    // a failed audit row must not convert the succeeded operation into a 500 —
    // the caller would believe a lock failed while it silently blocks everyone
    // else's writes for the whole LockPeriod (or believe an unlock failed that
    // in fact released).
    private async Task WriteHistoryAsync(Locator l, string actor, string lockType, CancellationToken ct)
    {
        try
        {
            await history.AppendAsync(l.SpaceName, l.Subpath, l.Shortname, actor, null,
                new Dictionary<string, object> { ["lock_type"] = lockType }, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "lock history append failed for {Space}/{Subpath}/{Shortname} ({LockType})",
                l.SpaceName, l.Subpath, l.Shortname, lockType);
        }
    }

    public Task<string?> GetLockerAsync(Locator l, CancellationToken ct = default)
        => locks.GetLockerAsync(l.SpaceName, l.Subpath, l.Shortname, settings.Value.LockPeriod, ct);
}
