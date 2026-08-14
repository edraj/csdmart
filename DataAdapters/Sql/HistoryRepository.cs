using System.Data.Common;
using Dmart.QueryGrammar;
using Dmart.Models.Core;
using System.Diagnostics.CodeAnalysis;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// histories table — flat (no Metas inheritance in dmart).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
    Justification = "Audited: CommandText is assembled from compile-time SQL, dialect-produced fragments and $N placeholders only. Every caller-supplied value is bound through DbParams, never concatenated.")]
public sealed class HistoryRepository(IDbConnectionFactory db, ISqlDialect dialect)
{
    public async Task AppendAsync(string spaceName, string subpath, string shortname, string? actor,
                                   Dictionary<string, object>? requestHeaders, Dictionary<string, object>? diff,
                                   CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await AppendAsync(spaceName, subpath, shortname, actor, requestHeaders, diff, conn, ct);
    }

    public async Task AppendAsync(string spaceName, string subpath, string shortname, string? actor,
                                   Dictionary<string, object>? requestHeaders, Dictionary<string, object>? diff,
                                   DbConnection conn,
                                   CancellationToken ct = default)
    {
        // uuid and timestamp are bound rather than produced by SQL: pgcrypto's
        // gen_random_uuid() and NOW() have no SQLite equivalents. History rows
        // are append-only and never compared against a server clock, so the
        // client wall-clock is the right basis on both backends — it is what
        // TimeUtils uses everywhere else.
        await using var cmd = conn.CreateCommand();
        // request_headers and diff are NOT NULL in dmart's schema — default to {}.
        DbParams.Add(cmd, JsonbHelpers.ToJsonb(requestHeaders) ?? "{}", SqlValueKind.Json);
        DbParams.Add(cmd, JsonbHelpers.ToJsonb(diff) ?? "{}", SqlValueKind.Json);
        DbParams.Add(cmd, (object?)actor ?? DBNull.Value);
        DbParams.Add(cmd, spaceName);
        DbParams.Add(cmd, subpath);
        DbParams.Add(cmd, shortname);
        var uuid = DbParams.Add(cmd, Guid.NewGuid());
        var stamp = DbParams.Add(cmd, TimeUtils.Now());
        cmd.CommandText = $"""
            INSERT INTO histories (uuid, request_headers, diff, timestamp,
                                   owner_shortname, last_checksum_history,
                                   space_name, subpath, shortname)
            VALUES ({uuid}, $1, $2, {stamp}, $3, NULL, $4, $5, $6)
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Pages every history row in a space, for the Parquet export.</summary>
    /// <remarks>
    /// Index: the leading `space_name` column of idx_histories_lookup
    /// (space_name, subpath, shortname, timestamp DESC) serves the filter.
    ///
    /// Ordered by uuid, not timestamp: paging needs a TOTAL order, and
    /// timestamps collide freely — several rows share one when a single request
    /// touches several resources. Ordering by a non-unique column would skip or
    /// repeat rows as the window advances, which is the same silent corruption
    /// ImportExportService.ForEachMatchAsync documents.
    /// </remarks>
    /// <param name="since">
    /// When set, only rows at or after this instant. History is append-only, so
    /// `timestamp` is its updated_at. Index: idx_histories_timestamp.
    /// </param>
    public async Task<List<HistoryRow>> ListForSpacePagedAsync(
        string spaceName, int limit, int offset, DateTime? since = null,
        string? subpath = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var sinceClause = since is null ? "" : "AND timestamp >= $4";
        var scoped = !string.IsNullOrEmpty(subpath) && subpath != "/";
        var scopeParam = since is null ? "$4" : "$5";
        var scopeClause = scoped ? $"AND (subpath = {scopeParam} OR subpath LIKE {scopeParam} || '/%')" : "";
        await using var cmd = conn.Command($"""
            SELECT uuid, request_headers, diff, timestamp, owner_shortname,
                   last_checksum_history, space_name, subpath, shortname
            FROM histories
            WHERE space_name = $1 {sinceClause} {scopeClause}
            ORDER BY uuid
            LIMIT $2 OFFSET $3
            """);
        DbParams.Add(cmd, spaceName);
        DbParams.Add(cmd, limit);
        DbParams.Add(cmd, offset);
        if (since is not null) DbParams.Add(cmd, since.Value);
        if (scoped) DbParams.Add(cmd, subpath!);

        var rows = new List<HistoryRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new HistoryRow
            {
                Uuid = r.GetGuid(0).ToString(),
                RequestHeaders = JsonbHelpers.FromDictStringObject(r.IsDBNull(1) ? null : r.GetString(1)),
                Diff = JsonbHelpers.FromDictStringObject(r.IsDBNull(2) ? null : r.GetString(2)),
                Timestamp = r.GetDateTime(3),
                OwnerShortname = r.IsDBNull(4) ? null : r.GetString(4),
                LastChecksumHistory = r.IsDBNull(5) ? null : r.GetString(5),
                SpaceName = r.GetString(6),
                Subpath = r.GetString(7),
                Shortname = r.GetString(8),
            });
        return rows;
    }

    /// <summary>
    /// Inserts a history row PRESERVING its original uuid and timestamp, for a
    /// restore. Returns true if it was inserted, false if it was already there.
    /// </summary>
    /// <remarks>
    /// <see cref="AppendAsync"/> cannot be used here: it mints a fresh uuid and
    /// stamps the current time, which would turn a restored audit trail into a
    /// record of the restore itself.
    ///
    /// History is append-only and immutable, so an existing uuid is left alone
    /// rather than overwritten — there is nothing about a past event that a
    /// later export could legitimately correct. ON CONFLICT DO NOTHING also
    /// makes this race-free and halves the queries versus check-then-insert.
    /// </remarks>
    /// <summary>
    /// Restores many history rows in batched multi-row INSERTs. Returns how
    /// many were newly inserted; the rest were already present.
    /// </summary>
    /// <remarks>
    /// A multi-row INSERT rather than a COPY session, deliberately. History is
    /// append-only with `ON CONFLICT (uuid) DO NOTHING` and no dependent
    /// merge, so it needs none of the scratch-table machinery entries and
    /// attachments require — and unlike COPY, this works on SQLITE TOO, which
    /// otherwise has no bulk path at all.
    ///
    /// Batched at <see cref="RestoreBatchRows"/> rows because both drivers cap
    /// bound parameters: PostgreSQL at 65535, SQLite lower still. Nine columns
    /// per row means a large restore would blow that cap in a single statement
    /// — and it would do so only on big archives, which is the worst time to
    /// discover it.
    /// </remarks>
    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: the VALUES list is assembled from generated placeholder names only; every caller-supplied value binds through DbCommand.Parameters.")]
    public async Task<int> RestoreManyAsync(
        IReadOnlyList<HistoryRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;

        var inserted = 0;
        await using var conn = await db.OpenAsync(ct);

        for (var offset = 0; offset < rows.Count; offset += RestoreBatchRows)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(RestoreBatchRows, rows.Count - offset);

            await using var cmd = conn.CreateCommand();
            var values = new System.Text.StringBuilder();
            for (var i = 0; i < take; i++)
            {
                var r = rows[offset + i];
                var headers = DbParams.Add(cmd, JsonbHelpers.ToJsonb(r.RequestHeaders) ?? "{}", SqlValueKind.Json);
                var diff = DbParams.Add(cmd, JsonbHelpers.ToJsonb(r.Diff) ?? "{}", SqlValueKind.Json);
                var owner = DbParams.Add(cmd, (object?)r.OwnerShortname ?? DBNull.Value);
                var checksum = DbParams.Add(cmd, (object?)r.LastChecksumHistory ?? DBNull.Value);
                var space = DbParams.Add(cmd, r.SpaceName);
                var subpath = DbParams.Add(cmd, r.Subpath);
                var shortname = DbParams.Add(cmd, r.Shortname);
                var uuid = DbParams.Add(cmd, Guid.Parse(r.Uuid));
                var stamp = DbParams.Add(cmd, r.Timestamp);

                if (i > 0) values.Append(',');
                values.Append($"({uuid},{headers},{diff},{stamp},{owner},{checksum},{space},{subpath},{shortname})");
            }

            // The VALUES list is built from PLACEHOLDER NAMES only; every value
            // binds through DbCommand.Parameters.
            cmd.CommandText = $"""
                INSERT INTO histories (uuid, request_headers, diff, timestamp,
                                       owner_shortname, last_checksum_history,
                                       space_name, subpath, shortname)
                VALUES {values}
                ON CONFLICT (uuid) DO NOTHING
                """;
            inserted += await cmd.ExecuteNonQueryAsync(ct);
        }

        return inserted;
    }

    /// <summary>Rows per multi-row INSERT. Bounded by each driver's parameter cap.</summary>
    internal static int RestoreBatchRows { get; set; } = 500;

    public async Task<bool> RestoreAsync(HistoryRow row, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        DbParams.Add(cmd, JsonbHelpers.ToJsonb(row.RequestHeaders) ?? "{}", SqlValueKind.Json);
        DbParams.Add(cmd, JsonbHelpers.ToJsonb(row.Diff) ?? "{}", SqlValueKind.Json);
        DbParams.Add(cmd, (object?)row.OwnerShortname ?? DBNull.Value);
        DbParams.Add(cmd, (object?)row.LastChecksumHistory ?? DBNull.Value);
        DbParams.Add(cmd, row.SpaceName);
        DbParams.Add(cmd, row.Subpath);
        DbParams.Add(cmd, row.Shortname);
        var uuid = DbParams.Add(cmd, Guid.Parse(row.Uuid));
        var stamp = DbParams.Add(cmd, row.Timestamp);
        cmd.CommandText = $"""
            INSERT INTO histories (uuid, request_headers, diff, timestamp,
                                   owner_shortname, last_checksum_history,
                                   space_name, subpath, shortname)
            VALUES ({uuid}, $1, $2, {stamp}, $3, $4, $5, $6, $7)
            ON CONFLICT (uuid) DO NOTHING
            """;
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<List<HistoryEntry>> ListAsync(string spaceName, string subpath, string shortname, int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("""
            SELECT uuid, owner_shortname, diff, timestamp
            FROM histories
            WHERE space_name = $1 AND subpath = $2 AND shortname = $3
            ORDER BY timestamp DESC
            LIMIT $4
            """);
        DbParams.Add(cmd, spaceName);
        DbParams.Add(cmd, subpath);
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<HistoryEntry>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new HistoryEntry(
                Uuid: reader.GetGuid(0),
                Actor: reader.IsDBNull(1) ? null : reader.GetString(1),
                Diff: reader.IsDBNull(2) ? null : reader.GetString(2),
                Timestamp: reader.GetDateTime(3)));
        }
        return results;
    }
    // ----- query support (used by QueryService for type=history) -----

    private const string SelectAllColumns = """
        SELECT uuid, request_headers, diff, timestamp, owner_shortname,
               last_checksum_history, space_name, subpath, shortname
        FROM histories
        """;

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: SQL is a StringBuilder of compile-time fragments and $N positional placeholders; user-supplied filter values flow through NpgsqlParameters (args).")]
    public async Task<List<HistoryRecord>> QueryHistoryAsync(Models.Api.Query q, CancellationToken ct = default)
    {
        var args = new List<NpgsqlParameter>();
        var sql = new System.Text.StringBuilder(
            $"{SelectAllColumns} WHERE space_name = $1 ");
        args.Add(new() { Value = q.SpaceName });

        if (!string.IsNullOrEmpty(q.Subpath) && q.Subpath != "/")
        {
            args.Add(new() { Value = q.Subpath });
            sql.Append($"AND (subpath = ${args.Count} OR subpath LIKE ${args.Count} || '/%') ");
        }
        if (q.FilterShortnames is { Count: > 0 })
        {
            // PostgreSQL binds one text[]; SQLite expands to an IN list. The
            // binder appends to the same positional args list either way.
            sql.Append($"AND {dialect.AnyOf("shortname", q.FilterShortnames, (v, k) => {
                args.Add(PostgresDialect.CreateParameter(new SqlParam(null, v, k)));
                return "$" + args.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            })} ");
        }
        if (q.FromDate is not null)
        {
            args.Add(new() { Value = q.FromDate.Value });
            sql.Append($"AND timestamp >= ${args.Count} ");
        }
        if (q.ToDate is not null)
        {
            args.Add(new() { Value = q.ToDate.Value });
            sql.Append($"AND timestamp <= ${args.Count} ");
        }

        sql.Append("ORDER BY timestamp DESC ");
        args.Add(new() { Value = Math.Max(1, q.Limit) });
        sql.Append($"LIMIT ${args.Count} ");
        args.Add(new() { Value = Math.Max(0, q.Offset) });
        sql.Append($"OFFSET ${args.Count}");

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(sql.ToString());
        DbParams.BindAll(cmd, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<HistoryRecord>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new HistoryRecord(
                Uuid: reader.GetGuid(0),
                RequestHeaders: reader.IsDBNull(1) ? null : reader.GetString(1),
                Diff: reader.IsDBNull(2) ? null : reader.GetString(2),
                Timestamp: reader.GetDateTime(3),
                OwnerShortname: reader.IsDBNull(4) ? null : reader.GetString(4),
                LastChecksumHistory: reader.IsDBNull(5) ? null : reader.GetString(5),
                SpaceName: reader.GetString(6),
                Subpath: reader.GetString(7),
                Shortname: reader.GetString(8)));
        }
        return results;
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: identical pattern to QueryHistoryAsync — StringBuilder of constants + $N placeholders; user values via NpgsqlParameters.")]
    public async Task<int> CountHistoryQueryAsync(Models.Api.Query q, CancellationToken ct = default)
    {
        var args = new List<NpgsqlParameter>();
        var sql = new System.Text.StringBuilder("SELECT COUNT(*) FROM histories WHERE space_name = $1 ");
        args.Add(new() { Value = q.SpaceName });
        if (!string.IsNullOrEmpty(q.Subpath) && q.Subpath != "/")
        {
            args.Add(new() { Value = q.Subpath });
            sql.Append($"AND (subpath = ${args.Count} OR subpath LIKE ${args.Count} || '/%') ");
        }
        if (q.FilterShortnames is { Count: > 0 })
        {
            // PostgreSQL binds one text[]; SQLite expands to an IN list. The
            // binder appends to the same positional args list either way.
            sql.Append($"AND {dialect.AnyOf("shortname", q.FilterShortnames, (v, k) => {
                args.Add(PostgresDialect.CreateParameter(new SqlParam(null, v, k)));
                return "$" + args.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            })} ");
        }
        if (q.FromDate is not null)
        {
            args.Add(new() { Value = q.FromDate.Value });
            sql.Append($"AND timestamp >= ${args.Count} ");
        }
        if (q.ToDate is not null)
        {
            args.Add(new() { Value = q.ToDate.Value });
            sql.Append($"AND timestamp <= ${args.Count} ");
        }
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(sql.ToString());
        DbParams.BindAll(cmd, args);
        return (int)DbParams.ReadCount(await cmd.ExecuteScalarAsync(ct));
    }
}

public sealed record HistoryEntry(Guid Uuid, string? Actor, string? Diff, DateTime Timestamp);

public sealed record HistoryRecord(
    Guid Uuid, string? RequestHeaders, string? Diff, DateTime Timestamp,
    string? OwnerShortname, string? LastChecksumHistory,
    string SpaceName, string Subpath, string Shortname);
