using Dmart.DataAdapters.Parquet;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Microsoft.Extensions.Logging;

namespace Dmart.Services;

/// <param name="Problems">
/// Capped — a wholly failed restore would otherwise produce one string per row
/// and bury the reason in its own output.
/// </param>
public sealed record RestoreVerification(
    int Checked, int Missing, int Mismatched, IReadOnlyList<string> Problems)
{
    public bool Ok => Missing == 0 && Mismatched == 0;
}

// Verifies that the DATABASE matches an archive after a restore.
//
// ParquetArchiveService.VerifyAsync checks that an archive is readable — that
// what was written can be read back. This is the other half, and the one an
// operator actually cares about: did the restore land?
//
// WHAT IS DELIBERATELY NOT COMPARED
//
// `query_policies` is REGENERATED on write — the bulk COPY path recomputes it
// because COPY writes the column verbatim, and UpsertAsync recomputes it too.
// A restored row can therefore hold different policies from the archived one
// entirely legitimately, for instance when the archive predates a change to how
// policies are derived. Comparing it would report a mismatch on a CORRECT
// restore, and a verifier that cries wolf is one people stop running.
//
// Media is compared by SHA256 recomputed from the database bytes, not by
// length: length survives a byte-for-byte substitution, and media is exactly
// the payload nothing downstream would ever notice was wrong.
public sealed class ParquetRestoreVerifier(
    EntryRepository entries,
    AttachmentRepository attachments,
    HistoryRepository histories,
    SpaceRepository spaces,
    UserRepository users,
    AccessRepository access,
    ILogger<ParquetRestoreVerifier> log)
{
    /// <summary>Problems recorded before the list stops growing.</summary>
    public const int MaxProblems = 50;

    private const int PageSize = 5_000;

    public async Task<RestoreVerification> VerifyAsync(
        string exportDirectory, CancellationToken ct = default)
    {
        var problems = new List<string>();
        int checkedRows = 0, missing = 0, mismatched = 0;

        void Report(string message)
        {
            if (problems.Count < MaxProblems) problems.Add(message);
        }

        // ---- entries ----
        var archivedEntries = ParquetArchiveService.ReadEntries(exportDirectory);
        foreach (var group in archivedEntries.GroupBy(e => e.SpaceName))
        {
            ct.ThrowIfCancellationRequested();
            var live = await ReadLiveEntriesAsync(group.Key, ct);

            foreach (var archived in group)
            {
                checkedRows++;
                if (!live.TryGetValue(Key(archived.Subpath, archived.Shortname), out var actual))
                {
                    missing++;
                    Report($"entry missing: {archived.SpaceName}{archived.Subpath}/{archived.Shortname}");
                    continue;
                }

                var diff = CompareEntry(archived, actual);
                if (diff is not null)
                {
                    mismatched++;
                    Report($"entry differs: {archived.SpaceName}{archived.Subpath}/{archived.Shortname} - {diff}");
                }
            }
        }

        // ---- attachments (metadata + media hash) ----
        foreach (var row in ParquetArchiveService.ReadAttachmentRows(exportDirectory))
        {
            ct.ThrowIfCancellationRequested();
            checkedRows++;

            var actual = await attachments.GetAsync(
                row.Attachment.SpaceName, row.Attachment.Subpath, row.Attachment.Shortname, ct);
            if (actual is null)
            {
                missing++;
                Report($"attachment missing: {row.Attachment.SpaceName}{row.Attachment.Subpath}/{row.Attachment.Shortname}");
                continue;
            }

            var (bytes, _) = await attachments.GetMediaAsync(Guid.Parse(actual.Uuid), ct);
            var actualSha = bytes is null
                ? null
                : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

            if (!string.Equals(actualSha, row.MediaSha256, StringComparison.OrdinalIgnoreCase))
            {
                mismatched++;
                Report($"attachment media differs: {row.Attachment.SpaceName}{row.Attachment.Subpath}/"
                     + $"{row.Attachment.Shortname} - archive {row.MediaSha256 ?? "(none)"}, "
                     + $"database {actualSha ?? "(none)"}");
            }
        }

        // ---- histories (existence by uuid; append-only and immutable) ----
        foreach (var group in ParquetArchiveService.ReadHistoryRows(exportDirectory).GroupBy(h => h.SpaceName))
        {
            ct.ThrowIfCancellationRequested();
            var liveUuids = await ReadLiveHistoryUuidsAsync(group.Key, ct);
            foreach (var archived in group)
            {
                checkedRows++;
                if (liveUuids.Contains(archived.Uuid)) continue;
                missing++;
                Report($"history row missing: {archived.Uuid} ({archived.SpaceName}{archived.Subpath}/{archived.Shortname})");
            }
        }

        // ---- global tables ----
        //
        // These carry the accounts and the ACL, so "did the restore land?" is
        // least answerable without them — and the users table is the one whose
        // silent failure disables logins.
        //
        // NOTE on what is NOT compared here, beyond uuid/created_at/
        // query_policies: `updated_at` is stamped TimeUtils.Now() on write for
        // ALL FOUR of these tables, unlike entries which honour the model's
        // value. Comparing it would fail every row of a correct restore.
        foreach (var archived in ParquetArchiveService.ReadSpaces(exportDirectory))
        {
            ct.ThrowIfCancellationRequested();
            checkedRows++;
            var actual = await spaces.GetAsync(archived.Shortname, ct);
            if (actual is null)
            {
                missing++;
                Report($"space missing: {archived.Shortname}");
                continue;
            }
            if (archived.IsActive != actual.IsActive
                || archived.OwnerShortname != actual.OwnerShortname
                || archived.Slug != actual.Slug
                || archived.PrimaryWebsite != actual.PrimaryWebsite
                || archived.IndexingEnabled != actual.IndexingEnabled)
            {
                mismatched++;
                Report($"space differs: {archived.Shortname}");
            }
        }

        foreach (var archived in ParquetArchiveService.ReadUsers(exportDirectory))
        {
            ct.ThrowIfCancellationRequested();
            checkedRows++;
            var actual = await users.GetByShortnameAsync(archived.Shortname, ct);
            if (actual is null)
            {
                missing++;
                Report($"user missing: {archived.Shortname}");
                continue;
            }

            // The PASSWORD check is the point of verifying users at all: a
            // restore that lands the row but drops the hash disables the
            // account, and nothing else would notice.
            //
            // Only checked when the ARCHIVE carries one. A null archived
            // password legitimately leaves the stored hash in place — that is
            // what `password = COALESCE(EXCLUDED.password, users.password)`
            // guarantees, and every pre-Parquet archive has nulls because the
            // zip export omits the column.
            if (archived.Password is not null && archived.Password != actual.Password)
            {
                mismatched++;
                Report($"user password differs: {archived.Shortname} — the archived hash was not restored");
                continue;
            }

            if (archived.IsActive != actual.IsActive
                || archived.Email != actual.Email
                || archived.Msisdn != actual.Msisdn
                || archived.Type != actual.Type
                || archived.Language != actual.Language
                || !archived.Roles.OrderBy(r => r, StringComparer.Ordinal)
                       .SequenceEqual(actual.Roles.OrderBy(r => r, StringComparer.Ordinal))
                || !archived.Groups.OrderBy(g => g, StringComparer.Ordinal)
                       .SequenceEqual(actual.Groups.OrderBy(g => g, StringComparer.Ordinal)))
            {
                mismatched++;
                Report($"user differs: {archived.Shortname}");
            }
        }

        foreach (var archived in ParquetArchiveService.ReadRoles(exportDirectory))
        {
            ct.ThrowIfCancellationRequested();
            checkedRows++;
            var actual = await access.GetRoleAsync(archived.Shortname, ct);
            if (actual is null)
            {
                missing++;
                Report($"role missing: {archived.Shortname}");
                continue;
            }
            // Permissions are what a role GRANTS, so a role restored without
            // them is an authorisation hole that looks like a success.
            if (archived.IsActive != actual.IsActive
                || !archived.Permissions.OrderBy(x => x, StringComparer.Ordinal)
                       .SequenceEqual(actual.Permissions.OrderBy(x => x, StringComparer.Ordinal)))
            {
                mismatched++;
                Report($"role differs: {archived.Shortname}");
            }
        }

        foreach (var archived in ParquetArchiveService.ReadPermissions(exportDirectory))
        {
            ct.ThrowIfCancellationRequested();
            checkedRows++;
            var actual = await access.GetPermissionAsync(archived.Shortname, ct);
            if (actual is null)
            {
                missing++;
                Report($"permission missing: {archived.Shortname}");
                continue;
            }
            if (archived.IsActive != actual.IsActive
                || !archived.Actions.OrderBy(x => x, StringComparer.Ordinal)
                       .SequenceEqual(actual.Actions.OrderBy(x => x, StringComparer.Ordinal))
                || !archived.ResourceTypes.OrderBy(x => x, StringComparer.Ordinal)
                       .SequenceEqual(actual.ResourceTypes.OrderBy(x => x, StringComparer.Ordinal)))
            {
                mismatched++;
                Report($"permission differs: {archived.Shortname}");
            }
        }

        var result = new RestoreVerification(checkedRows, missing, mismatched, problems);
        if (result.Ok)
            log.LogInformation("restore verify: {Checked} rows match the archive", checkedRows);
        else
            log.LogError("restore verify: {Missing} missing, {Mismatched} differing of {Checked} checked",
                missing, mismatched, checkedRows);
        return result;
    }

    private static string Key(string subpath, string shortname) => subpath + " " + shortname;

    private async Task<Dictionary<string, Entry>> ReadLiveEntriesAsync(string space, CancellationToken ct)
    {
        // Paged bulk read rather than one GetAsync per archived row: verifying a
        // restore of 100k entries would otherwise be 100k round trips.
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var offset = 0;
        while (true)
        {
            var page = await entries.ListForSpaceUpdatedSincePagedAsync(
                space, DateTime.MinValue, PageSize, offset, null, ct);
            if (page.Count == 0) break;
            foreach (var e in page) map[Key(e.Subpath, e.Shortname)] = e;
            offset += page.Count;
            if (page.Count < PageSize) break;
        }
        return map;
    }

    private async Task<HashSet<string>> ReadLiveHistoryUuidsAsync(string space, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (true)
        {
            var page = await histories.ListForSpacePagedAsync(space, PageSize, offset, null, null, ct);
            if (page.Count == 0) break;
            foreach (var h in page) seen.Add(h.Uuid);
            offset += page.Count;
            if (page.Count < PageSize) break;
        }
        return seen;
    }

    // Returns a description of the first field that differs, or null.
    //
    // query_policies is absent on purpose — see the class comment.
    private static string? CompareEntry(Entry archived, Entry actual)
    {
        // WHAT IS COMPARED, AND WHY THE REST IS NOT.
        //
        // A field is only verifiable if a restore can actually make it match.
        // Two classes cannot, and comparing either reports a correct restore as
        // broken:
        //
        //   * fields the upsert DOES NOT OVERWRITE — `uuid` and `created_at`.
        //     Entries upsert on (shortname, space_name, subpath), and the
        //     conflict clause deliberately omits both: they are stable identity
        //     a restore preserves rather than rewrites. Restoring over a row
        //     that already exists therefore keeps the EXISTING values.
        //
        //   * fields the write path REGENERATES rather than copies —
        //     `query_policies`, recomputed on every write.
        //
        // Both were found the same way: restoring a full backup over a
        // freshly-bootstrapped system, whose own startup had already created
        // the management folders. Verification reported four differing entries
        // on a restore that was completely correct — first on uuid, then on
        // created_at. Everything still compared below IS in the conflict
        // clause, so a mismatch there is a real one.
        if (archived.ResourceType != actual.ResourceType)
            return $"resource_type {archived.ResourceType} vs {actual.ResourceType}";
        if (archived.IsActive != actual.IsActive) return "is_active";
        if (archived.OwnerShortname != actual.OwnerShortname) return "owner_shortname";
        if (archived.Slug != actual.Slug) return "slug";
        if (archived.State != actual.State) return "state";
        if (archived.IsOpen != actual.IsOpen) return "is_open";
        if (!archived.Tags.SequenceEqual(actual.Tags)) return "tags";
        // updated_at only — created_at is in the not-overwritten class above.
        //
        // Compared at MICROSECOND granularity: Parquet TIMESTAMP_MICROS holds 6
        // decimal places and .NET DateTime holds 7, so an exact comparison would
        // flag every row of a correct restore on SQLite.
        if (archived.UpdatedAt.Ticks / 10 != actual.UpdatedAt.Ticks / 10) return "updated_at";
        return null;
    }
}
