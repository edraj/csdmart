using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;

namespace Dmart.Models.Contracts;

/// <summary>
/// Backend-neutral data contract shared by the HTTP client
/// (<c>Dmart.Client.DmartClient</c>) and the direct-PostgreSQL adapter
/// (<c>Dmart.SqlAdapter.DmartSqlAdapter</c>). Program against this to swap
/// HTTP ↔ direct-DB backends via DI.
///
/// Semantic caveats (differ by backend, not by signature):
/// <list type="bullet">
///   <item><description><c>actor</c> is IGNORED on the HTTP client (the bearer
///   token identifies the caller); on the SQL adapter it is the RBAC subject.</description></item>
///   <item><description>RBAC is enforced server-side for the client, in-process
///   (PermissionEngine) for the adapter.</description></item>
///   <item><description>The client is AOT/trim-safe; the adapter is not.</description></item>
///   <item><description><see cref="MoveAsync"/> across spaces THROWS on the
///   client (dmart move is intra-space) but performs the UPDATE on the adapter.</description></item>
///   <item><description>Writes: success returns a <see cref="Response"/>; failure
///   throws a <see cref="DmartException"/> subtype on both backends. The returned
///   <see cref="Response"/> is a success signal whose Records echo the written
///   entry's identity; treat it as such rather than depending on an exact
///   Attributes field set (the client echoes the server's full record, the
///   adapter synthesizes an equivalent). Load the entry back if you need the
///   canonical stored form.</description></item>
///   <item><description><c>scope</c> on <see cref="QueryEntriesAsync"/> selects the
///   HTTP endpoint scope ("managed"/"public") on the client; it is IGNORED on the
///   SQL adapter (RBAC there comes from <c>actor</c>).</description></item>
/// </list>
/// </summary>
public interface IDmartData
{
    // ---- Reads ----
    Task<Entry?> LoadAsync(string spaceName, string subpath, string shortname, ResourceType? resourceType = null, string? actor = null, CancellationToken ct = default);
    Task<Entry?> LoadOrNoneAsync(Locator locator, string? actor = null, CancellationToken ct = default);
    Task<Entry?> GetByUuidAsync(Guid uuid, string? actor = null, CancellationToken ct = default);
    Task<Entry?> GetBySlugAsync(string slug, string? actor = null, CancellationToken ct = default);
    Task<Entry?> GetEntryByCriteriaAsync(IReadOnlyDictionary<string, object?> criteria, string? actor = null, CancellationToken ct = default);
    Task<Entry?> GetSchemaAsync(string spaceName, string shortname, string? actor = null, CancellationToken ct = default);
    Task<bool> IsEntryExistAsync(Locator locator, string? actor = null, CancellationToken ct = default);

    // ---- Writes (success => Response, failure => typed throw) ----
    Task<Response> CreateAsync(Entry entry, string? actor = null, CancellationToken ct = default);
    Task<Response> UpdateAsync(Entry entry, string? actor = null, CancellationToken ct = default);
    Task<Response> SaveAsync(Entry entry, string? actor = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(Locator locator, string? actor = null, CancellationToken ct = default);
    Task<bool> MoveAsync(Locator source, Locator target, string? actor = null, CancellationToken ct = default);

    // ---- Query / children ----
    Task<(int Total, List<Entry> Records)> QueryEntriesAsync(Query query, string? actor = null, string scope = "managed", CancellationToken ct = default);
    Task<(int Total, List<Entry> Records)> GetChildrenEntriesAsync(string spaceName, string subpath, string search = "", int limit = 20, int offset = 0, IReadOnlyList<ResourceType>? restrictTypes = null, string? actor = null, CancellationToken ct = default);

    // ---- Spaces / users ----
    Task<Space?> FetchSpaceAsync(string spaceName, string? actor = null, CancellationToken ct = default);
    Task<Dictionary<string, Space>> LoadSpacesAsync(string? actor = null, CancellationToken ct = default);
    Task<User?> LoadUserMetaAsync(string shortname, string? actor = null, CancellationToken ct = default);
    Task<User?> GetProfileAsync(string actor, CancellationToken ct = default);

    // ---- History ----
    Task<List<HistoryRow>> QueryHistoryAsync(string spaceName, string subpath, string shortname, int limit = 50, string? actor = null, CancellationToken ct = default);

    // ---- Locks ----
    Task<bool> TryLockAsync(Locator locator, string ownerShortname, int lockPeriodSeconds = 300, CancellationToken ct = default);
    Task<bool> UnlockAsync(Locator locator, string ownerShortname, CancellationToken ct = default);
}
