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
// `set`, not `init`: dmart strips empty arrays from every JSON response, so a
// quiet page arrives without `users` or `deleted`, and on meeting init-only
// properties the source-generated reader stops running the initializers
// (ModelDefaultsTests has the details). With `set` an absent list reads as [].
public sealed record DirectoryFeedPage
{
    public List<DirectoryFeedUser> Users { get; set; } = [];
    public bool More { get; set; }
    public string? After { get; set; }
    public DateTime? AfterTime { get; set; }
    public List<string> Deleted { get; set; } = [];
    public List<Group>? Groups { get; set; }
    public DateTime? ServerTime { get; set; }
    // The instant from which the primary's tombstones are complete (first page
    // only). A replica whose watermark is older cannot learn every deletion
    // from a changes walk and must walk in full. The replica compares it with
    // its real watermark, not with `since`, which it backs off by an overlap.
    public DateTime? RetentionFloor { get; set; }
}

public sealed class DirectoryFeedService(
    UserRepository users, AccessRepository access, IDbConnectionFactory db, IOptions<DmartSettings> settings)
{
    public const int MaxPage = 1000;

    // A listed bot account. Listing is not enough on its own: a person who
    // was given a listed shortname (or took one freed by a delete) must not
    // read every password hash.
    public async Task<bool> MayReadAsync(string? actor, CancellationToken ct)
        => actor is not null
           && settings.Value.ParseDirectoryFeedReaders().Contains(actor, StringComparer.Ordinal)
           && await users.GetByShortnameAsync(actor, ct) is { Type: Models.Enums.UserType.Bot, IsUsable: true };

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

    // What a replica needs to answer LDAP and binds, and no more: not the
    // payload, admin notes, social-login links, device or login history.
    // A deleted row carries only its name and the flag.
    private static DirectoryFeedUser Carry(User u) => new(u with
    {
        Payload = null,
        Notes = null,
        GoogleId = null,
        FacebookId = null,
        AppleId = null,
        SocialAvatarUrl = null,
        DeviceId = null,
        LastLogin = null,
        AttemptCount = null,
        LastFailedLogin = null,
        Acl = null,
        Relationships = null,
    }, u.Password);
}
