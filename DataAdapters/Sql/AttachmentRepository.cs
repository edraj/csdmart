using System.Data.Common;
using Dmart.QueryGrammar;
using Dmart.Models.Core;

namespace Dmart.DataAdapters.Sql;

// dmart's Attachments table inherits from Metas — same Unique base. The "parent" is
// expressed via the (space_name, subpath, shortname) of the attachment row, where the
// subpath includes the parent shortname (e.g. /content/foo/.attachments). We follow
// dmart's convention here.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
    Justification = "Audited: CommandText is assembled from compile-time SQL, dialect-produced fragments and $N placeholders only. Every caller-supplied value is bound through DbParams, never concatenated.")]
public sealed class AttachmentRepository(IDbConnectionFactory db, ISqlDialect dialect)
{
    private const string SelectAllColumns = """
        SELECT uuid, shortname, space_name, subpath, is_active, slug,
               displayname, description, tags, created_at, updated_at,
               owner_shortname, owner_group_shortname, acl, payload, relationships,
               last_checksum_history, resource_type, media, body, state
        FROM attachments
        """;

    // Same projection, minus the `media` bytea. Listing paths only ever feed
    // QueryService's AttachmentMapper (ToRecord / ToEntryRecord), both of which
    // deliberately omit media — so selecting it meant Postgres detoasted every
    // blob, shipped it over the wire, and Hydrate allocated a byte[] that was
    // dropped on the next line. A 100-record page with 3x5MB attachments each
    // moved ~1.5GB and burned 300 LOH arrays for a response containing zero
    // bytes of media. `media` is replaced by a NULL literal (rather than dropped
    // from the list) so column ordinals stay identical and Hydrate remains the
    // single hydrator for this table — no second mapper to keep in sync.
    private const string SelectColumnsNoMedia = """
        SELECT uuid, shortname, space_name, subpath, is_active, slug,
               displayname, description, tags, created_at, updated_at,
               owner_shortname, owner_group_shortname, acl, payload, relationships,
               last_checksum_history, resource_type, NULL AS media, body, state
        FROM attachments
        """;

    // Metadata-only listing — `media` comes back null. This is what every
    // rendering path wants (AttachmentMapper drops the bytes anyway), so it
    // keeps the short name and the callers that never think about media get
    // the cheap query by default.
    public Task<List<Attachment>> ListForParentAsync(
        string spaceName, string parentSubpath, string parentShortname, CancellationToken ct = default)
        => ListForParentAsync(spaceName, parentSubpath, parentShortname, includeMedia: false, ct);

    // Listing WITH the media bytes. Separate method rather than a defaulted
    // flag: the bytes-or-not decision is the difference between a working
    // export and a zip full of empty files (which is exactly what shipped once
    // — see Export_Writes_Attachment_Media_Bytes_Into_The_Zip), and a caller
    // that needs them should have to say so at the call site where a reviewer
    // can see it. Export is the only in-tree consumer; single-attachment
    // downloads (/managed/payload, the MCP download tool) use GetAsync /
    // GetMediaAsync, which always select media.
    public Task<List<Attachment>> ListForParentWithMediaAsync(
        string spaceName, string parentSubpath, string parentShortname, CancellationToken ct = default)
        => ListForParentAsync(spaceName, parentSubpath, parentShortname, includeMedia: true, ct);

    /// <summary>
    /// Pages every attachment in a space, metadata only, plus the SIZE of each
    /// media blob.
    /// </summary>
    /// <remarks>
    /// For the Parquet export, which is space-wide rather than per-parent. It
    /// deliberately does NOT select the bytes: an export streams blobs one at a
    /// time through <see cref="GetMediaAsync"/>, so peak memory is one blob
    /// rather than a page of them. Media is where the gigabytes are, and a page
    /// of 5 MB attachments would otherwise be resident all at once.
    ///
    /// The size comes back so the exporter can skip the media fetch entirely
    /// for attachments that have none, which is most of them.
    ///
    /// `length(media)` rather than `octet_length`: on PostgreSQL bytea the two
    /// are the same function, and SQLite has only the former. Ordering is by
    /// uuid because paging needs a TOTAL order — the same trap
    /// ImportExportService.ForEachMatchAsync documents.
    ///
    /// Index: scans by `space_name`, served by idx_attachments_space_name.
    /// </remarks>
    /// <param name="since">
    /// When set, only rows with <c>updated_at &gt;= since</c> — the incremental
    /// selection (§5.1). Inclusive, so it overlaps the previous run: an upsert
    /// makes a re-shipped row free, while a missed one is silent corruption.
    /// Index: idx_attachments_updated_at.
    /// </param>
    public async Task<List<(Attachment Attachment, long MediaSize)>> ListForSpacePagedAsync(
        string spaceName, int limit, int offset, DateTime? since = null,
        string? subpath = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var sinceClause = since is null ? "" : "AND updated_at >= $4";
        // An attachment's subpath is "<parent subpath>/<parent shortname>", so
        // the folder's own attachments sit AT the scope and everything deeper
        // sits under it — the same predicate the folder cascade uses.
        var scoped = !string.IsNullOrEmpty(subpath) && subpath != "/";
        var scopeParam = since is null ? "$4" : "$5";
        var scopeClause = scoped ? $"AND (subpath = {scopeParam} OR subpath LIKE {scopeParam} || '/%')" : "";
        await using var cmd = conn.Command($"""
            {SelectColumnsNoMedia.Replace("FROM attachments", "").TrimEnd()},
                   COALESCE(length(media), 0) AS media_size
            FROM attachments
            WHERE space_name = $1 {sinceClause} {scopeClause}
            ORDER BY uuid
            LIMIT $2 OFFSET $3
            """);
        DbParams.Add(cmd, spaceName);
        DbParams.Add(cmd, limit);
        DbParams.Add(cmd, offset);
        if (since is not null) DbParams.Add(cmd, since.Value);
        if (scoped) DbParams.Add(cmd, subpath!);

        var result = new List<(Attachment, long)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add((Hydrate(r), r.IsDBNull(21) ? 0L : Convert.ToInt64(r.GetValue(21))));
        return result;
    }

    private async Task<List<Attachment>> ListForParentAsync(
        string spaceName, string parentSubpath, string parentShortname, bool includeMedia, CancellationToken ct)
    {
        var normalized = Locator.NormalizeSubpath(parentSubpath);
        var attachmentSubpath = $"{normalized.TrimEnd('/')}/{parentShortname}";
        var columns = includeMedia ? SelectAllColumns : SelectColumnsNoMedia;
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command($"{columns} WHERE space_name = $1 AND subpath = $2 ORDER BY created_at DESC");
        DbParams.Add(cmd, spaceName);
        DbParams.Add(cmd, attachmentSubpath);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var results = new List<Attachment>();
        while (await r.ReadAsync(ct)) results.Add(Hydrate(r));
        return results;
    }

    // Batched lookup — fetches every parent's attachments in a single round
    // trip. Replaces the N-query fan-out QueryService used to do for
    // retrieve_attachments=true (one query per record). For a 100-record page
    // that's 100 queries → 1, with a corresponding drop in connection-pool
    // pressure (the default DatabasePoolSize is 10+10 overflow, so a few
    // concurrent /public/query calls with retrieve_attachments would saturate
    // it and serialize behind the pool).
    //
    // Returned dictionary is keyed by the attachment's `subpath` (the
    // `<parentSubpath>/<parentShortname>` form Hydrate writes back). Callers
    // recompute that key per record to look up.
    public async Task<Dictionary<string, List<Attachment>>> ListForParentsAsync(
        string spaceName,
        IReadOnlyList<(string ParentSubpath, string ParentShortname)> parents,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<Attachment>>(StringComparer.Ordinal);
        if (parents.Count == 0) return result;

        var keys = new string[parents.Count];
        for (var i = 0; i < parents.Count; i++)
        {
            var normalized = Locator.NormalizeSubpath(parents[i].ParentSubpath);
            keys[i] = $"{normalized.TrimEnd('/')}/{parents[i].ParentShortname}";
        }

        await using var conn = await db.OpenAsync(ct);
        // PostgreSQL matches the key list with one bound text[]; SQLite has no
        // array type and expands to an IN list, so the dialect emits the form
        // and binds the values.
        await using var cmd = conn.CreateCommand();
        DbParams.Add(cmd, spaceName);
        var inList = dialect.AnyOf("subpath", keys, (v, k) => DbParams.Add(cmd, v, k));
        cmd.CommandText =
            $"{SelectColumnsNoMedia} WHERE space_name = $1 AND {inList} "
            + "ORDER BY subpath, created_at DESC";

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var att = Hydrate(r);
            if (!result.TryGetValue(att.Subpath, out var list))
                result[att.Subpath] = list = new List<Attachment>();
            list.Add(att);
        }
        return result;
    }

    // Direct lookup by (space, subpath, shortname) — used by /managed/payload/... when
    // the URL already points at the attachment row itself.
    public async Task<Attachment?> GetAsync(string spaceName, string subpath, string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await GetAsync(spaceName, subpath, shortname, conn, ct);
    }

    public async Task<Attachment?> GetAsync(string spaceName, string subpath, string shortname, DbConnection conn, CancellationToken ct = default)
    {
        await using var cmd = conn.Command($"{SelectAllColumns} WHERE space_name = $1 AND subpath = $2 AND shortname = $3");
        DbParams.Add(cmd, spaceName);
        DbParams.Add(cmd, Locator.NormalizeSubpath(subpath));
        DbParams.Add(cmd, shortname);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Hydrate(r) : null;
    }

    public async Task<Attachment?> GetByUuidAsync(Guid uuid, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command($"{SelectAllColumns} WHERE uuid = $1");
        DbParams.Add(cmd, uuid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Hydrate(r) : null;
    }

    public async Task UpsertAsync(Attachment a, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await UpsertAsync(a, conn, ct);
    }

    public async Task UpsertAsync(Attachment a, DbConnection conn, CancellationToken ct = default)
    {
        await using var cmd = conn.Command("""
            INSERT INTO attachments (uuid, shortname, space_name, subpath, is_active, slug,
                                     displayname, description, tags, created_at, updated_at,
                                     owner_shortname, owner_group_shortname, acl, payload, relationships,
                                     last_checksum_history, resource_type, media, body, state)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21)
            ON CONFLICT (shortname, space_name, subpath) DO UPDATE SET
                is_active = EXCLUDED.is_active,
                slug = EXCLUDED.slug,
                displayname = EXCLUDED.displayname,
                description = EXCLUDED.description,
                tags = EXCLUDED.tags,
                updated_at = EXCLUDED.updated_at,
                owner_shortname = EXCLUDED.owner_shortname,
                owner_group_shortname = EXCLUDED.owner_group_shortname,
                acl = EXCLUDED.acl,
                payload = EXCLUDED.payload,
                relationships = EXCLUDED.relationships,
                last_checksum_history = EXCLUDED.last_checksum_history,
                resource_type = EXCLUDED.resource_type,
                media = EXCLUDED.media,
                body = EXCLUDED.body,
                state = EXCLUDED.state
            """);

        BindAttachment(cmd, a);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Insert-only: returns true if the row was inserted, false if an attachment with
    // the same (shortname, space_name, subpath) already exists. Unlike UpsertAsync
    // (ON CONFLICT DO UPDATE, which overwrites in place), this lets the auto-shortname
    // create path detect a collision and re-mint instead of clobbering an existing row.
    public async Task<bool> TryInsertAsync(Attachment a, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("""
            INSERT INTO attachments (uuid, shortname, space_name, subpath, is_active, slug,
                                     displayname, description, tags, created_at, updated_at,
                                     owner_shortname, owner_group_shortname, acl, payload, relationships,
                                     last_checksum_history, resource_type, media, body, state)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21)
            ON CONFLICT (shortname, space_name, subpath) DO NOTHING
            """);
        BindAttachment(cmd, a);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static void BindAttachment(DbCommand cmd, Attachment a)
    {
        DbParams.Add(cmd, Guid.Parse(a.Uuid));
        DbParams.Add(cmd, a.Shortname);
        DbParams.Add(cmd, a.SpaceName);
        DbParams.Add(cmd, a.Subpath);
        DbParams.Add(cmd, a.IsActive);
        DbParams.Add(cmd, (object?)a.Slug ?? DBNull.Value);
        AddJsonb(cmd, JsonbHelpers.ToJsonb(a.Displayname));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(a.Description));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(a.Tags));   // tags is NOT NULL
        DbParams.Add(cmd, a.CreatedAt == default ? TimeUtils.Now() : a.CreatedAt);
        // Honor the caller's UpdatedAt — see EntryRepository.UpsertAsync for
        // the full reasoning; same pattern keeps round-trip verbatim.
        DbParams.Add(cmd, a.UpdatedAt == default ? TimeUtils.Now() : a.UpdatedAt);
        DbParams.Add(cmd, a.OwnerShortname);
        DbParams.Add(cmd, (object?)a.OwnerGroupShortname ?? DBNull.Value);
        AddJsonb(cmd, JsonbHelpers.ToJsonb(a.Acl));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(a.Payload));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(a.Relationships));
        DbParams.Add(cmd, (object?)a.LastChecksumHistory ?? DBNull.Value);
        DbParams.Add(cmd, JsonbHelpers.EnumMember(a.ResourceType));
        DbParams.Add(cmd, (object?)a.Media ?? DBNull.Value);
        DbParams.Add(cmd, (object?)a.Body ?? DBNull.Value);
        DbParams.Add(cmd, (object?)a.State ?? DBNull.Value);
    }

    public async Task<(byte[]? Bytes, string? ContentType)> GetMediaAsync(Guid uuid, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT media, payload FROM attachments WHERE uuid = $1");
        DbParams.Add(cmd, uuid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, null);
        var bytes = r.IsDBNull(0) ? null : (byte[])r.GetValue(0);
        var payload = JsonbHelpers.FromPayload(r.IsDBNull(1) ? null : r.GetString(1));
        var contentType = payload?.ContentType.ToString().ToLowerInvariant();
        return (bytes, contentType);
    }

    public async Task DeleteAsync(Guid uuid, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        // Tombstone and delete share one transaction so a crash cannot separate
        // them, leaving a row gone with nothing recording that it went (§5.2).
        // The tombstone runs FIRST because it reads the row being removed.
        await using var tx = await conn.BeginTransactionAsync(ct);

        await Tombstones.RecordAsync(conn, tx, "attachments", "uuid = $1",
            c => DbParams.Add(c, uuid), hasResourceType: true, ct);

        await using var cmd = conn.Command("DELETE FROM attachments WHERE uuid = $1", tx);
        DbParams.Add(cmd, uuid);
        await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    // Bulk-delete every attachment whose subpath sits at or under `prefix`.
    // Used during recursive folder/entry deletes — an attachment's subpath
    // is its parent's subpath + "/" + parent shortname, so callers pass the
    // parent's full path here. Matches Python adapter.py:2752-2757 +
    // 2770-2775 — same intent, but we use the precise prefix-with-slash
    // check instead of a raw `startswith` to avoid matching unrelated
    // siblings (`/products` vs `/products_old`).
    public async Task<int> DeleteUnderSubpathAsync(string spaceName, string prefix, CancellationToken ct = default)
    {
        const string predicate = """
            space_name = $1
              AND (subpath = $2 OR subpath LIKE $2 || '/%')
            """;

        void Bind(DbCommand c)
        {
            DbParams.Add(c, spaceName);
            DbParams.Add(c, prefix);
        }

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Over the SAME predicate as the delete, so the tombstones and the
        // removal cannot disagree about which attachments a cascade took.
        await Tombstones.RecordAsync(conn, tx, "attachments", predicate,
            Bind, hasResourceType: true, ct);

        await using var cmd = conn.Command($"DELETE FROM attachments WHERE {predicate}", tx);
        Bind(cmd);
        var deleted = await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return deleted;
    }

    // Count (don't delete) every attachment at or under `prefix` — the dryrun
    // projection counterpart of DeleteUnderSubpathAsync, using the identical predicate.
    public async Task<long> CountUnderSubpathAsync(string spaceName, string prefix, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("""
            SELECT count(*) FROM attachments
            WHERE space_name = $1
              AND (subpath = $2 OR subpath LIKE $2 || '/%')
            """);
        DbParams.Add(cmd, spaceName);
        DbParams.Add(cmd, prefix);
        return DbParams.ReadCount(await cmd.ExecuteScalarAsync(ct));
    }

    // ----- query support (used by QueryService for type=attachments) -----

    // Media-less: both consumers (QueryService's attachment page → AttachmentMapper
    // .ToRecord, and UniquenessValidator's shortname/subpath probe) discard the bytes.
    public Task<List<Attachment>> QueryAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunQueryAsync(db, SelectColumnsNoMedia, q, Hydrate, ct, tableName: "attachments");

    public Task<int> CountQueryAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunCountAsync(db, "attachments", q, ct);

    private static void AddJsonb(DbCommand cmd, string? json)
        => DbParams.Add(cmd, (object?)json ?? DBNull.Value, SqlValueKind.Json);

    private static void AddJsonbNotNull(DbCommand cmd, string json)
        => DbParams.Add(cmd, json, SqlValueKind.Json);

    private static Attachment Hydrate(DbDataReader r)
    {
        return new Attachment
        {
            Uuid = r.GetGuid(0).ToString(),
            Shortname = r.GetString(1),
            SpaceName = r.GetString(2),
            Subpath = r.GetString(3),
            IsActive = r.GetBoolean(4),
            Slug = r.IsDBNull(5) ? null : r.GetString(5),
            Displayname = JsonbHelpers.FromTranslation(r.IsDBNull(6) ? null : r.GetString(6)),
            Description = JsonbHelpers.FromTranslation(r.IsDBNull(7) ? null : r.GetString(7)),
            Tags = JsonbHelpers.FromListString(r.IsDBNull(8) ? null : r.GetString(8)) ?? new(),
            CreatedAt = r.GetDateTime(9),
            UpdatedAt = r.GetDateTime(10),
            OwnerShortname = r.GetString(11),
            OwnerGroupShortname = r.IsDBNull(12) ? null : r.GetString(12),
            Acl = JsonbHelpers.FromAclList(r.IsDBNull(13) ? null : r.GetString(13)),
            Payload = JsonbHelpers.FromPayload(r.IsDBNull(14) ? null : r.GetString(14)),
            Relationships = JsonbHelpers.FromRelationships(r.IsDBNull(15) ? null : r.GetString(15)),
            LastChecksumHistory = r.IsDBNull(16) ? null : r.GetString(16),
            ResourceType = JsonbHelpers.ParseEnumMember<Models.Enums.ResourceType>(r.GetString(17)),
            Media = r.IsDBNull(18) ? null : (byte[])r.GetValue(18),
            Body = r.IsDBNull(19) ? null : r.GetString(19),
            State = r.IsDBNull(20) ? null : r.GetString(20),
        };
    }
}
