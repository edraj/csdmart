using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Json;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// Keeps a directory replica's users and groups in step with its primary
// (docs/directory-replica.md), so the replica's LDAP face can answer lookups
// and binds when the primary, or the link to it, is down. The case it exists
// for: i7 delivers mail and authenticates IMAP against a local copy of the
// directory on i1.
//
// It pulls /managed/directory-feed as a bot listed in the primary's
// DIRECTORY_FEED_READERS:
//   - the first time, and whenever the primary has pruned deletions past this
//     replica's watermark: a FULL walk in shortname order, reconciling each
//     page against the local rows (anything the primary no longer has goes);
//   - after that: a CHANGES walk from the watermark, minus Overlap, applying
//     hard deletions first and then every changed row.
// The watermark is the primary's clock at the start of the last complete walk,
// kept in directory_replica_state. Lockout counters stay local: a bind on the
// replica counts against the replica's copy, and a sync does not reset it.
//
// Until the first walk completes, LDAP answers `unavailable` to every lookup
// and bind (DirectoryIndexStatus.ReplicaSynced): Postfix then defers mail
// instead of bouncing it for an address the replica has not heard of yet.
// After that, a failed poll only logs; the replica keeps serving its copy.
public sealed class DirectoryReplica(
    IOptions<DmartSettings> settings,
    IHttpClientFactory httpClients,
    UserRepository users,
    AccessRepository access,
    DirectoryIndexStatus status,
    ILogger<DirectoryReplica> log) : BackgroundService
{
    public const string HttpClientName = "directory-replica";
    // How far before the watermark a changes walk starts. A write stamps
    // updated_at before its transaction commits; one that commits after a walk
    // read its page, with a stamp from before the walk began, is still inside
    // this window next time. An hour and five minutes because dmart stamps
    // host-local time: when the primary's clock falls back an hour at the end
    // of daylight saving, the rows written in the next hour carry stamps from
    // before the watermark. Unchanged rows in the window cost a comparison
    // each (ApplyAsync), not a write.
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(65);
    private const int PageSize = 500;
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";

    private string? _token;

    // The last poll that completed, by this host's clock.
    public DateTime? LastSync { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var s = settings.Value;
        if (!s.IsDirectoryReplica) return;
        status.BeginReplica(TimeSpan.FromHours(s.DirectoryReplicaMaxStalenessHours));
        var interval = TimeSpan.FromSeconds(s.DirectoryReplicaIntervalSeconds);
        log.LogInformation("directory replica of {Primary}, polling every {Seconds}s", s.DirectoryReplicaOf, interval.TotalSeconds);

        // A copy synced before a restart serves at once, so a replica that
        // restarts while its primary is down still answers. How old the copy
        // is still counts toward the staleness limit.
        try
        {
            if (await users.GetReplicaSyncedAtAsync(stoppingToken) is { } before
                && await users.GetReplicaWatermarkAsync(stoppingToken) is not null)
            {
                status.SetReplicaSyncedAt(before);
                LastSync = before;
                log.LogInformation("directory replica: serving the copy synced at {SyncedAt:u} until the primary answers", before);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "directory replica: could not read the last sync time; LDAP answers 'unavailable' until a sync succeeds");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wasServing = status.ReplicaSynced && !status.ReplicaStale;
                var (applied, syncedAt) = await SyncOnceAsync(stoppingToken);
                if (!wasServing)
                    log.LogInformation("directory replica: in sync with the primary ({Count} user row(s) applied); LDAP is serving", applied);
                else if (applied > 0)
                    log.LogInformation("directory replica: applied {Count} user change(s)", applied);
                status.SetReplicaSyncedAt(syncedAt);
                LastSync = syncedAt;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            // Everything, deliberately. An exception escaping a BackgroundService
            // stops the whole host, and on a replica that host is the mail
            // server's directory: one malformed page must cost one poll, not
            // the LDAP face.
            catch (Exception ex)
            {
                log.LogWarning(ex, "directory replica: syncing with {Primary} failed; {State}", s.DirectoryReplicaOf,
                    LastSync is not { } last ? "LDAP answers 'unavailable' until a sync succeeds"
                    : status.ReplicaStale ? $"the copy is from {(DateTime.UtcNow - last).TotalHours:F0} h ago, past the limit: binds answer 'unavailable' until a sync succeeds"
                    : $"still serving the copy from {(DateTime.UtcNow - last).TotalMinutes:F0} min ago");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    // One poll. Returns how many user rows it applied, and when (UTC, this
    // host's clock) it began: what the copy is current to.
    internal async Task<(int Applied, DateTime SyncedAt)> SyncOnceAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        if (await users.GetReplicaWatermarkAsync(ct) is { } watermark)
        {
            if (await ChangesAsync(watermark, started, ct) is { } applied) return (applied, started);
            log.LogWarning("directory replica: the primary no longer has every deletion since {Watermark}; walking it in full",
                watermark);
        }
        return (await FullAsync(started, ct), started);
    }

    private async Task<int> FullAsync(DateTime started, CancellationToken ct)
    {
        DateTime? serverTime = null;
        List<Group>? groups = null;
        string? after = null;
        var reconciledTo = "";
        var applied = 0;
        DirectoryFeedPage page;
        do
        {
            page = await FetchAsync("mode=full" + (after is null ? "" : "&after=" + Uri.EscapeDataString(after)), ct);
            if (after is null)
            {
                serverTime = page.ServerTime;
                groups = page.Groups;
            }
            foreach (var u in page.Users)
                if (await ApplyAsync(u, ct)) applied++;
            // Every local name the primary skipped between the previous page
            // and the end of this one is a user it no longer has.
            var kept = page.Users.Select(u => u.User.Shortname).ToHashSet(StringComparer.Ordinal);
            var upTo = page.More ? page.After : null;
            foreach (var local in await users.ListShortnamesBetweenAsync(reconciledTo, upTo, ct))
                if (!kept.Contains(local)) await RemoveLocalAsync(local, ct);
            reconciledTo = page.After ?? reconciledTo;
            after = page.After;
        } while (page.More);

        await ApplyGroupsAsync(groups, ct);
        await users.SetReplicaWatermarkAsync(serverTime, started, ct);
        return applied;
    }

    // Null when a changes walk from `watermark` cannot be complete: the primary
    // has no tombstones from that far back.
    private async Task<int?> ChangesAsync(DateTime watermark, DateTime started, CancellationToken ct)
    {
        var since = watermark - Overlap;
        DateTime? serverTime = null, afterTime = null;
        List<Group>? groups = null;
        string? after = null;
        var applied = 0;
        DirectoryFeedPage page;
        do
        {
            var query = "mode=changes&since=" + Uri.EscapeDataString(since.ToString(TimeFormat, CultureInfo.InvariantCulture));
            if (afterTime is { } at)
                query += "&after_time=" + Uri.EscapeDataString(at.ToString(TimeFormat, CultureInfo.InvariantCulture))
                       + "&after=" + Uri.EscapeDataString(after ?? "");
            page = await FetchAsync(query, ct);
            if (afterTime is null && page.RetentionFloor is { } floor && floor > watermark) return null;
            if (afterTime is null)
            {
                serverTime = page.ServerTime;
                groups = page.Groups;
                // Deletions first: a name deleted and then taken again is in
                // both lists, and the row that exists now must win.
                foreach (var gone in page.Deleted)
                    await RemoveLocalAsync(gone, ct);
            }
            foreach (var u in page.Users)
                if (await ApplyAsync(u, ct)) applied++;
            afterTime = page.AfterTime;
            after = page.After;
        } while (page.More && afterTime is not null);

        await ApplyGroupsAsync(groups, ct);
        await users.SetReplicaWatermarkAsync(serverTime, started, ct);
        return applied;
    }

    // True when it changed the local copy. A changes walk re-reads its overlap
    // every poll, so most rows it sees are already here, as they were.
    private async Task<bool> ApplyAsync(DirectoryFeedUser fed, CancellationToken ct)
    {
        var incoming = fed.User with { Password = fed.PasswordHash };
        var local = await users.GetByShortnameAsync(incoming.Shortname, ct);
        if (incoming.IsDeleted)
        {
            if (local is null || local.IsDeleted) return false;
            await RemoveLocalAsync(local.Shortname, ct);
            return true;
        }
        // Unchanged since the last poll: the replica keeps the primary's
        // updated_at (UserRepository.UpsertReplicatedAsync), so it compares.
        if (local is { IsDeleted: false } && local.Uuid == incoming.Uuid && local.UpdatedAt == incoming.UpdatedAt
            && local.Password == incoming.Password)
            return false;
        // A local row under the same name, whatever its uuid (this replica's
        // own bootstrap admin, or an account the primary re-created), is
        // overwritten in place: the upsert resolves on shortname. The feed
        // carries no attempt counter, and a null one keeps the local count
        // (the upsert's COALESCE), including binds that fail meanwhile.
        incoming = incoming with { AttemptCount = null };
        try
        {
            await users.UpsertReplicatedAsync(incoming, ct);
        }
        catch (System.Data.Common.DbException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Another local row still holds something this one now has on the
            // primary: its uuid (a rename whose old name sorts later in a full
            // walk), or an email, msisdn or address that moved between users.
            // The primary is consistent, so the holder's own row will arrive
            // with its current values; drop the stale copy and retry once.
            await ReleaseClashesAsync(incoming, ct);
            await users.UpsertReplicatedAsync(incoming, ct);
        }
        return true;
    }

    private async Task ReleaseClashesAsync(User incoming, CancellationToken ct)
    {
        var holders = new HashSet<string>(StringComparer.Ordinal);
        if (await users.GetShortnameByUuidAsync(incoming.Uuid, ct) is { } byUuid) holders.Add(byUuid);
        if (incoming.Email is { Length: > 0 } email && await users.GetByEmailAsync(email, ct) is { } e) holders.Add(e.Shortname);
        if (incoming.Msisdn is { Length: > 0 } msisdn && await users.GetByMsisdnAsync(msisdn, ct) is { } m) holders.Add(m.Shortname);
        foreach (var address in (incoming.Mailbox is null ? incoming.MailAliases : [incoming.Mailbox, .. incoming.MailAliases]))
            if (await users.FindAddressOwnerAsync(address, ct) is { } owner) holders.Add(owner.Shortname);
        holders.Remove(incoming.Shortname);
        foreach (var holder in holders)
        {
            log.LogInformation("directory replica: dropping the local copy of {Holder}, which clashes with {User}",
                holder, incoming.Shortname);
            await RemoveLocalAsync(holder, ct);
        }
    }

    // A user the primary no longer has. Deleted outright, unless this replica
    // has rows that name it as owner (its own bootstrap created the management
    // space, roles and permissions as its admin): then soft-deleted, which
    // clears its credentials and contact details and hides it from LDAP, the
    // same state the primary's own soft delete leaves.
    private async Task RemoveLocalAsync(string shortname, CancellationToken ct)
    {
        if (await users.OwnsAnyRecordsAsync(shortname, ct)) await users.SoftDeleteAsync(shortname, ct);
        else await users.DeleteAsync(shortname, ct);
    }

    // Groups are few: the first page of every walk carries all of them, and
    // the local set is made to match once the walk's users are in, since a
    // group's owner must be a local user. One the replica does not have (the
    // feed carries live users only) is replaced by the replica's own account.
    private async Task ApplyGroupsAsync(List<Group>? groups, CancellationToken ct)
    {
        if (groups is null) return;
        var names = groups.Select(g => g.Shortname).ToHashSet(StringComparer.Ordinal);
        var present = new Dictionary<string, bool>(StringComparer.Ordinal);
        async Task<bool> Exists(string shortname)
        {
            if (!present.TryGetValue(shortname, out var exists))
                present[shortname] = exists = await users.GetByShortnameAsync(shortname, ct) is not null;
            return exists;
        }
        foreach (var g in groups)
        {
            var owner = g.OwnerShortname;
            if (!await Exists(owner))
                owner = await Exists(settings.Value.DirectoryReplicaShortname) ? settings.Value.DirectoryReplicaShortname : "dmart";
            await access.UpsertGroupAsync(g with { OwnerShortname = owner }, ct);
        }
        foreach (var local in await access.ListGroupsForDirectoryAsync(ct))
            if (!names.Contains(local.Shortname)) await access.DeleteGroupAsync(local.Shortname, ct);
    }

    private async Task<DirectoryFeedPage> FetchAsync(string query, CancellationToken ct)
    {
        var client = httpClients.CreateClient(HttpClientName);
        var url = new Uri(PrimaryUri(), $"managed/directory-feed?{query}&limit={PageSize}");
        for (var attempt = 0; ; attempt++)
        {
            _token ??= await SignInAsync(client, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _token = null;   // expired: sign in again, once
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new HttpRequestException(
                    $"the primary refused the directory feed to {settings.Value.DirectoryReplicaShortname}: "
                    + "list it in the primary's DIRECTORY_FEED_READERS");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"the directory feed answered {(int)response.StatusCode}");
            return await response.Content.ReadFromJsonAsync(DmartJsonContext.Default.DirectoryFeedPage, ct)
                ?? throw new JsonException("the directory feed answered an empty body");
        }
    }

    private async Task<string> SignInAsync(HttpClient client, CancellationToken ct)
    {
        var s = settings.Value;
        using var response = await client.PostAsJsonAsync(new Uri(PrimaryUri(), "user/login"),
            new UserLoginRequest(s.DirectoryReplicaShortname, null, null, s.DirectoryReplicaPassword, null),
            DmartJsonContext.Default.UserLoginRequest, ct);
        var body = await response.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response, ct);
        if (body?.Records is [{ Attributes: { } attrs }, ..]
            && attrs.TryGetValue("access_token", out var token) && token?.ToString() is { Length: > 0 } t)
            return t;
        throw new HttpRequestException(
            $"signing in to the primary as {s.DirectoryReplicaShortname} failed ({(int)response.StatusCode})");
    }

    private Uri PrimaryUri() => new(settings.Value.DirectoryReplicaOf.TrimEnd('/') + "/");
}
