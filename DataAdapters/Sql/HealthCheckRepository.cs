using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Dmart.DataAdapters.Sql;

// Runs the same integrity probes dmart Python's health check exposes:
//   * orphan attachments       — attachments with no matching parent entry
//   * dangling owners          — entries whose owner_shortname doesn't exist
//   * stale locks              — locks held longer than `staleAfter`
//   * empty payload references — entries that claim a payload but have no body
//   * untyped resources        — rows with unknown resource_type values
//
// `health_type` filters which checks run:
//   * "soft"      → counts only
//   * "hard"      → counts + sample rows
//   * "all"       → everything
public sealed class HealthCheckRepository(IDbConnectionFactory db)
{
    public sealed record IssueCheck(string Name, long Count, List<string> Samples);

    public async Task<List<IssueCheck>> RunAsync(string spaceName, string healthType, CancellationToken ct = default)
    {
        var includeSamples = healthType is "hard" or "all";
        var sampleLimit = includeSamples ? 10 : 0;
        var results = new List<IssueCheck>();

        await using var conn = await db.OpenAsync(ct);

        results.Add(await RunCheck(conn, "orphan_attachments", """
            SELECT a.shortname FROM attachments a
            WHERE a.space_name = $1
            AND NOT EXISTS (
                SELECT 1 FROM entries e
                WHERE e.space_name = a.space_name
                  AND a.subpath LIKE e.subpath || '/' || e.shortname || '%'
            )
            """, spaceName, sampleLimit, ct));

        results.Add(await RunCheck(conn, "dangling_owner", """
            SELECT e.shortname FROM entries e
            WHERE e.space_name = $1
            AND NOT EXISTS (SELECT 1 FROM users u WHERE u.shortname = e.owner_shortname)
            """, spaceName, sampleLimit, ct));

        // The 24h cutoff is computed here rather than with NOW() - INTERVAL:
        // SQLite has neither, and the locks timestamps are client wall-clock on
        // both backends.
        results.Add(await RunCheck(conn, "stale_locks", """
            SELECT l.shortname FROM locks l
            WHERE l.space_name = $1 AND l.timestamp < $2
            """, spaceName, sampleLimit, ct, TimeUtils.Now().AddHours(-24)));

        results.Add(await RunCheck(conn, "missing_payload_body", """
            SELECT e.shortname FROM entries e
            WHERE e.space_name = $1
            AND e.payload IS NOT NULL
            AND (e.payload->'body') IS NULL
            """, spaceName, sampleLimit, ct));

        results.Add(await RunCheck(conn, "missing_schema_reference", """
            SELECT e.shortname FROM entries e
            WHERE e.space_name = $1
            AND e.payload IS NOT NULL
            AND (e.payload->>'schema_shortname') IS NOT NULL
            AND NOT EXISTS (
                SELECT 1 FROM entries s
                WHERE s.space_name = e.space_name
                  AND s.resource_type = 'schema'
                  AND s.shortname = (e.payload->>'schema_shortname')
            )
            """, spaceName, sampleLimit, ct));

        // Folder content compliance: entries violating their PARENT folder's
        // declared policy arrays (content_resource_types /
        // content_schema_shortnames / workflow_shortnames). Mirrors
        // FolderContentValidator's rules — empty/absent array means
        // unrestricted, an absent incoming value skips that dimension — so the
        // report and the write-path gate agree on what "non-compliant" means.
        // This surfaces legacy rows that predate enforcement (and anything
        // written during an ENFORCE_FOLDER_CONTENT_POLICY=false dry-run).
        // The parent split duplicates FolderContentValidator.SplitSubpath in
        // SQL: '/a/b' -> folder 'b' under '/a'; '/a' -> folder 'a' under '/'.
        // Entries whose parent folder declares a content policy they violate.
        //
        // Mirrors FolderContentValidator, which enforces the same three arrays
        // on write. This is the read-side audit: it lists stock that is ALREADY
        // stored and would now be rejected — what an operator runs before
        // turning ENFORCE_FOLDER_CONTENT_POLICY on, or after importing legacy
        // data written when these arrays were only rendering hints.
        //
        // Each dimension is guarded the same way the validator guards it: an
        // absent or empty policy array means "no restriction", and an absent
        // incoming value skips that dimension, so a schema-less entry is never
        // flagged for having no schema.
        //
        // The two backends differ in the parent-path split and in every JSON
        // operator, so they get separate text rather than a translation:
        //
        //   PostgreSQL  regexp_replace / left / greatest, jsonb_typeof,
        //               jsonb_array_length, `@> to_jsonb(x)`
        //   SQLite      has no regex at all, so the split uses
        //               rtrim(s, <the non-slash characters of s>) to find the
        //               last separator; json_type / json_array_length; and
        //               containment becomes an EXISTS over json_each.
        results.Add(await RunCheck(conn, "folder_content_violations",
            conn is SqliteConnection ? SqliteFolderContentViolations : PostgresFolderContentViolations,
            spaceName, sampleLimit, ct));

        return results;
    }

    private const string PostgresFolderContentViolations = """
        SELECT e.subpath || '/' || e.shortname FROM entries e
        JOIN entries f
          ON f.space_name = e.space_name
         AND f.resource_type = 'folder'
         AND f.shortname = regexp_replace(e.subpath, '^.*/', '')
         AND f.subpath = COALESCE(NULLIF(left(e.subpath,
               greatest(length(e.subpath) - length(regexp_replace(e.subpath, '^.*/', '')) - 1, 0)), ''), '/')
        WHERE e.space_name = $1
          AND e.subpath <> '/'
          AND jsonb_typeof(f.payload->'body') = 'object'
          AND (
            (jsonb_typeof(f.payload->'body'->'content_resource_types') = 'array'
             AND jsonb_array_length(f.payload->'body'->'content_resource_types') > 0
             AND NOT (f.payload->'body'->'content_resource_types' @> to_jsonb(e.resource_type::text)))
            OR
            ((e.payload->>'schema_shortname') IS NOT NULL
             AND jsonb_typeof(f.payload->'body'->'content_schema_shortnames') = 'array'
             AND jsonb_array_length(f.payload->'body'->'content_schema_shortnames') > 0
             AND NOT (f.payload->'body'->'content_schema_shortnames' @> to_jsonb(e.payload->>'schema_shortname')))
            OR
            (e.resource_type = 'ticket'
             AND e.workflow_shortname IS NOT NULL
             AND jsonb_typeof(f.payload->'body'->'workflow_shortnames') = 'array'
             AND jsonb_array_length(f.payload->'body'->'workflow_shortnames') > 0
             AND NOT (f.payload->'body'->'workflow_shortnames' @> to_jsonb(e.workflow_shortname)))
          )
        """;

    // `rtrim(s, replace(s,'/',''))` trims every character that is NOT a slash
    // off the right-hand end, which leaves the prefix up to and including the
    // last separator. The tail after it is the parent folder's shortname, and
    // the prefix minus its trailing slash is that folder's own subpath ('/'
    // when the entry sits one level down).
    private const string SqliteFolderContentViolations = """
        SELECT e.subpath || '/' || e.shortname FROM entries e
        JOIN entries f
          ON f.space_name = e.space_name
         AND f.resource_type = 'folder'
         AND f.shortname = substr(e.subpath, length(rtrim(e.subpath, replace(e.subpath, '/', ''))) + 1)
         AND f.subpath = CASE
               WHEN length(rtrim(e.subpath, replace(e.subpath, '/', ''))) <= 1 THEN '/'
               ELSE substr(rtrim(e.subpath, replace(e.subpath, '/', '')), 1,
                           length(rtrim(e.subpath, replace(e.subpath, '/', ''))) - 1)
             END
        WHERE e.space_name = $1
          AND e.subpath <> '/'
          AND json_valid(f.payload)
          AND json_type(f.payload, '$.body') = 'object'
          AND (
            (json_type(f.payload, '$.body.content_resource_types') = 'array'
             AND json_array_length(f.payload, '$.body.content_resource_types') > 0
             AND NOT EXISTS (SELECT 1 FROM json_each(f.payload, '$.body.content_resource_types') AS ct
                             WHERE ct.value = e.resource_type))
            OR
            ((e.payload ->> '$.schema_shortname') IS NOT NULL
             AND json_type(f.payload, '$.body.content_schema_shortnames') = 'array'
             AND json_array_length(f.payload, '$.body.content_schema_shortnames') > 0
             AND NOT EXISTS (SELECT 1 FROM json_each(f.payload, '$.body.content_schema_shortnames') AS cs
                             WHERE cs.value = e.payload ->> '$.schema_shortname'))
            OR
            (e.resource_type = 'ticket'
             AND e.workflow_shortname IS NOT NULL
             AND json_type(f.payload, '$.body.workflow_shortnames') = 'array'
             AND json_array_length(f.payload, '$.body.workflow_shortnames') > 0
             AND NOT EXISTS (SELECT 1 FROM json_each(f.payload, '$.body.workflow_shortnames') AS ws
                             WHERE ws.value = e.workflow_shortname))
          )
        """;

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `sql` is a compile-time constant from in-class call sites (the five HealthCheck SQL literals); `spaceName` flows through $1.")]
    private static async Task<IssueCheck> RunCheck(
        DbConnection conn, string name, string sql, string spaceName,
        int sampleLimit, CancellationToken ct, object? extra = null)
    {
        var samples = new List<string>();
        long count = 0;

        // Count
        await using (var cmd = conn.Command($"SELECT COUNT(*) FROM ({sql}) c"))
        {
            DbParams.Add(cmd, spaceName);
            if (extra is not null) DbParams.Add(cmd, extra);
            count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }

        // Samples
        if (sampleLimit > 0 && count > 0)
        {
            await using var cmd = conn.Command($"{sql} LIMIT {sampleLimit}");
            if (extra is not null) DbParams.Add(cmd, extra);
            DbParams.Add(cmd, spaceName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) samples.Add(reader.GetString(0));
        }

        return new IssueCheck(name, count, samples);
    }
}
