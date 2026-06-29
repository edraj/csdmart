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
    IOptions<DmartSettings> settings)
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

        // before_action runs first so a hook plugin can veto the lock (Python's
        // plugin_manager.before_action at the top of lock_entry). A rejection
        // surfaces as a failed Response rather than a 500.
        if (await BeforeActionAsync(l, ActionType.Lock, actor, ct) is { } lockBefore)
            return lockBefore;

        // Python ticket locks mark the current processor before taking the
        // lock. Keep the lock operation useful for ticket UIs that read
        // collaborators.processed_by.
        if (l.Type == ResourceType.Ticket)
        {
            var ticket = await entries.GetAsync(l.SpaceName, l.Subpath, l.Shortname, l.Type, ct);
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

        if (await BeforeActionAsync(l, ActionType.Unlock, actor, ct) is { } unlockBefore)
            return unlockBefore;

        var ok = await locks.UnlockAsync(l.SpaceName, l.Subpath, l.Shortname, actor, ct);
        if (!ok)
            return Response.Fail(InternalErrorCode.NOT_ALLOWED, "you don't hold this lock", ErrorTypes.Auth);

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
        catch
        {
            return Response.Fail(InternalErrorCode.INVALID_DATA,
                $"plugin rejected {action.ToString().ToLowerInvariant()}", ErrorTypes.Request);
        }
    }

    // Records the lock/unlock action in the history table, mirroring Python's
    // store_entry_diff(..., {"lock_type": ...}). Subpath is already the
    // leading-slash form (Locator-normalized), matching EntryService history rows.
    private Task WriteHistoryAsync(Locator l, string actor, string lockType, CancellationToken ct)
        => history.AppendAsync(l.SpaceName, l.Subpath, l.Shortname, actor, null,
            new Dictionary<string, object> { ["lock_type"] = lockType }, ct);

    public Task<string?> GetLockerAsync(Locator l, CancellationToken ct = default)
        => locks.GetLockerAsync(l.SpaceName, l.Subpath, l.Shortname, settings.Value.LockPeriod, ct);
}
