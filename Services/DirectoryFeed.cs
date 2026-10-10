using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// One user as the directory feed carries it: the row as dmart serializes it,
// plus the password hash, which User never serializes.
public sealed record DirectoryFeedUser(User User, string? PasswordHash);

// One page of the directory feed (docs/directory-replica.md).
//   full:    every live user, in shortname order, `After` resuming the walk.
//   changes: users whose row changed since `Since` (soft-deleted ones
//            included, with is_deleted set), in (updated_at, shortname)
//            order, `AfterTime` + `After` resuming the walk.
// The first page of either also carries the whole group list, and a changes
// walk's first page the users hard-deleted since `Since`. ServerTime is the
// primary's clock when that first page was read: the replica's next
// watermark.
//
// The lists are nullable on purpose: dmart strips empty arrays from every JSON
// response, so a quiet page arrives without `users` or `deleted`, and the
// source-generated reader leaves a missing list null whatever its initializer
// says. Typing them nullable makes every reader say what empty means.
public sealed record DirectoryFeedPage
{
    public List<DirectoryFeedUser>? Users { get; init; }
    public bool More { get; init; }
    public string? After { get; init; }
    public DateTime? AfterTime { get; init; }
    public List<string>? Deleted { get; init; }
    public List<Group>? Groups { get; init; }
    public DateTime? ServerTime { get; init; }
    // The instant from which the primary's tombstones are complete (first page
    // only). A replica whose watermark is older cannot learn every deletion
    // from a changes walk and must walk in full. The replica compares it with
    // its real watermark, not with `since`, which it backs off by an overlap.
    public DateTime? RetentionFloor { get; init; }
}

public sealed class DirectoryFeedService(
    UserRepository users, AccessRepository access, IDbConnectionFactory db, IOptions<DmartSettings> settings)
{
    public const int MaxPage = 1000;

    public bool MayRead(string? actor)
        => actor is not null && settings.Value.ParseDirectoryFeedReaders().Contains(actor, StringComparer.Ordinal);

    public async Task<DirectoryFeedPage> FullAsync(string? after, int limit, CancellationToken ct)
    {
        var first = after is null;
        var serverTime = Utils.TimeUtils.Now();
        var rows = await users.ListForDirectoryAsync(after, limit + 1, ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new DirectoryFeedPage
        {
            Users = rows.Select(Carry).ToList(),
            More = more,
            After = rows.Count > 0 ? rows[^1].Shortname : after,
            Groups = first ? await access.ListGroupsForDirectoryAsync(ct) : null,
            ServerTime = first ? serverTime : null,
        };
    }

    public async Task<DirectoryFeedPage> ChangesAsync(
        DateTime since, DateTime? afterTime, string? after, int limit, CancellationToken ct)
    {
        var first = afterTime is null;
        var serverTime = Utils.TimeUtils.Now();
        DateTime? floor = null;
        if (first)
        {
            await using var conn = await db.OpenAsync(ct);
            floor = await Tombstones.ReadRetentionFloorAsync(conn, null, ct);
        }

        var rows = await users.ListChangedSinceAsync(afterTime ?? since, after ?? "", limit + 1, ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        var last = rows.Count > 0 ? rows[^1] : null;
        return new DirectoryFeedPage
        {
            Users = rows.Select(Carry).ToList(),
            More = more,
            After = last?.Shortname ?? after,
            AfterTime = last?.UpdatedAt ?? afterTime,
            Deleted = first ? await users.ListDeletedSinceAsync(since, ct) : [],
            Groups = first ? await access.ListGroupsForDirectoryAsync(ct) : null,
            ServerTime = first ? serverTime : null,
            RetentionFloor = floor,
        };
    }

    private static DirectoryFeedUser Carry(User u) => new(u, u.Password);
}
