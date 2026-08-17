namespace Dmart.DataAdapters.Sql;

// SQLite DDL. The SQLite counterpart of SqlSchema.CreateAll, NOT a translation
// of it — the two schemas legitimately differ and are kept as separate text
// rather than one parameterized template (docs/sqlite-backend-audit.md §7.3).
//
// The SQL store is a rebuildable index over the flat files under SPACES_FOLDER,
// which stay the source of truth. That is what makes the storage differences
// below acceptable: nothing here is a system of record.
//
// Type mapping (audit §6) — SQLite has only INTEGER/REAL/TEXT/BLOB:
//
//   UUID        -> TEXT, canonical lowercase hyphenated "D" format. Equality is
//                  string equality, so the format must be pinned; see
//                  SqliteValues.FromGuid.
//   TIMESTAMP   -> TEXT, 'yyyy-MM-dd HH:mm:ss.fffffff' (27 chars, fixed width,
//                  most-significant-first). Fixed width is what makes ORDER BY
//                  and BETWEEN correct without a functional index. The column
//                  DEFAULTs pad strftime's 3-digit %f out to 7 digits so a
//                  default-written row sorts consistently against an
//                  app-written one — a narrower default would compare
//                  incorrectly against a wider value with the same instant.
//                  Local wall-clock, no offset, matching the PostgreSQL
//                  TIMESTAMP WITHOUT TIME ZONE columns and TimeUtils.Now().
//   JSONB       -> TEXT holding JSON. Queried with SQLite's json1 operators.
//   TEXT[]      -> TEXT holding a JSON array. Only query_policies is an array
//                  column; it is iterated with json_each instead of unnest.
//   BOOLEAN     -> INTEGER 0/1.
//   BYTEA       -> BLOB.
//   hstore      -> TEXT holding JSON (otp.value is a two-key dictionary).
//   ENUM        -> TEXT with a CHECK constraint.
//
// Deliberately absent, with no SQLite equivalent (audit §9):
//   * every GIN index — JSON containment and array membership degrade to scans
//   * pg_trgm — the wildcard prefilter is served by an FTS5 trigram index
//   * pgcrypto / gen_random_uuid() — UUIDs are generated client-side, which is
//     what the rest of the codebase already does
//   * hstore / pgvector extensions
//   * CREATE INDEX CONCURRENTLY — index builds lock; acceptable at this tier
//   * the PL/pgSQL DO blocks — their conditional-index logic lives in
//     SqliteSchemaInitializer, because SQLite has no procedural language
public static class SqliteSchema
{
    // Wall-clock default matching SqliteValues.TimestampFormat's width.
    // strftime's %f gives milliseconds (3 digits); the suffix pads to the
    // 7 fractional digits the application writes.
    private const string NowExpr = "(strftime('%Y-%m-%d %H:%M:%f','now','localtime') || '0000')";

    public static readonly string CreateAll = $"""
    -- ============================================================
    -- USERS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS users (
        uuid                    TEXT PRIMARY KEY,
        shortname               TEXT NOT NULL UNIQUE,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               INTEGER NOT NULL DEFAULT 0,
        slug                    TEXT,
        displayname             TEXT,
        description             TEXT,
        tags                    TEXT,
        created_at              TEXT NOT NULL DEFAULT {NowExpr},
        updated_at              TEXT NOT NULL DEFAULT {NowExpr},
        owner_shortname         TEXT NOT NULL,
        owner_group_shortname   TEXT,
        payload                 TEXT,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL DEFAULT 'user',

        password                TEXT,
        roles                   TEXT,
        groups                  TEXT,
        acl                     TEXT,
        relationships           TEXT,
        type                    TEXT NOT NULL DEFAULT 'web'
                                CHECK (type IN ('web','mobile','bot')),
        language                TEXT NOT NULL DEFAULT 'en'
                                CHECK (language IN ('ar','en','ku','fr','tr')),
        email                   TEXT,
        msisdn                  TEXT,
        locked_to_device        INTEGER NOT NULL DEFAULT 0,
        is_email_verified       INTEGER NOT NULL DEFAULT 0,
        is_msisdn_verified      INTEGER NOT NULL DEFAULT 0,
        force_password_change   INTEGER NOT NULL DEFAULT 1,
        device_id               TEXT,
        google_id               TEXT,
        facebook_id             TEXT,
        apple_id                TEXT,
        social_avatar_url       TEXT,
        attempt_count           INTEGER,
        last_failed_login       TEXT,
        last_login              TEXT,
        notes                   TEXT,
        query_policies          TEXT NOT NULL DEFAULT '[]'
                                CHECK (json_array_length(query_policies) > 0),

        -- Soft delete. INTEGER because SQLite has no boolean type; the reader
        -- goes through GetBoolean, which maps 0/1. deleted_at is TEXT for the
        -- same reason every other timestamp in this schema is.
        is_deleted              INTEGER NOT NULL DEFAULT 0,
        deleted_at              TEXT,

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- ROLES
    -- ============================================================
    CREATE TABLE IF NOT EXISTS roles (
        uuid                    TEXT PRIMARY KEY,
        shortname               TEXT NOT NULL,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               INTEGER NOT NULL DEFAULT 0,
        slug                    TEXT,
        displayname             TEXT,
        description             TEXT,
        tags                    TEXT,
        created_at              TEXT NOT NULL DEFAULT {NowExpr},
        updated_at              TEXT NOT NULL DEFAULT {NowExpr},
        owner_shortname         TEXT NOT NULL
                                REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     TEXT,
        payload                 TEXT,
        relationships           TEXT,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL DEFAULT 'role',

        grantable_by            TEXT,
        permissions             TEXT,
        query_policies          TEXT NOT NULL DEFAULT '[]'
                                CHECK (json_array_length(query_policies) > 0),

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- GROUPS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS groups (
        uuid                    TEXT PRIMARY KEY,
        shortname               TEXT NOT NULL,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               INTEGER NOT NULL DEFAULT 0,
        slug                    TEXT,
        displayname             TEXT,
        description             TEXT,
        tags                    TEXT,
        created_at              TEXT NOT NULL DEFAULT {NowExpr},
        updated_at              TEXT NOT NULL DEFAULT {NowExpr},
        owner_shortname         TEXT NOT NULL
                                REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     TEXT,
        payload                 TEXT,
        relationships           TEXT,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL DEFAULT 'group',

        grantable_by            TEXT,
        -- No non-empty CHECK here, deliberately. The PostgreSQL schema applies
        -- that constraint to entries/users/roles/permissions/spaces only —
        -- groups is excluded there too, so adding one would make SQLite reject
        -- writes PostgreSQL accepts.
        query_policies          TEXT NOT NULL DEFAULT '[]',

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- PERMISSIONS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS permissions (
        uuid                    TEXT PRIMARY KEY,
        shortname               TEXT NOT NULL,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               INTEGER NOT NULL DEFAULT 0,
        slug                    TEXT,
        displayname             TEXT,
        description             TEXT,
        tags                    TEXT,
        created_at              TEXT NOT NULL DEFAULT {NowExpr},
        updated_at              TEXT NOT NULL DEFAULT {NowExpr},
        owner_shortname         TEXT NOT NULL
                                REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     TEXT,
        payload                 TEXT,
        relationships           TEXT,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL DEFAULT 'permission',

        subpaths                TEXT,
        resource_types          TEXT,
        actions                 TEXT,
        conditions              TEXT,
        restricted_fields       TEXT,
        allowed_fields_values   TEXT,
        filter_fields_values    TEXT,
        query_policies          TEXT NOT NULL DEFAULT '[]'
                                CHECK (json_array_length(query_policies) > 0),

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- ENTRIES
    -- ------------------------------------------------------------
    -- schema_shortname is a VIRTUAL generated column over the payload JSON.
    -- This is the SQLite replacement for PostgreSQL's expression index
    -- `((payload->>'schema_shortname'))`: SQLite cannot index a bare
    -- expression, but it can index a generated column, and the planner uses
    -- that index for a predicate written against the underlying expression.
    -- VIRTUAL (not STORED) costs no storage and is computed on read.
    -- ============================================================
    CREATE TABLE IF NOT EXISTS entries (
        uuid                    TEXT PRIMARY KEY,
        shortname               TEXT NOT NULL,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               INTEGER NOT NULL DEFAULT 0,
        slug                    TEXT,
        displayname             TEXT,
        description             TEXT,
        tags                    TEXT,
        created_at              TEXT NOT NULL DEFAULT {NowExpr},
        updated_at              TEXT NOT NULL DEFAULT {NowExpr},
        owner_shortname         TEXT NOT NULL
                                REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     TEXT,
        payload                 TEXT,
        relationships           TEXT,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL,

        state                   TEXT,
        is_open                 INTEGER,
        reporter                TEXT,
        workflow_shortname      TEXT,
        collaborators           TEXT,
        resolution_reason       TEXT,
        query_policies          TEXT NOT NULL DEFAULT '[]'
                                CHECK (json_array_length(query_policies) > 0),

        schema_shortname        TEXT GENERATED ALWAYS AS
                                (payload ->> '$.schema_shortname') VIRTUAL,

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- ATTACHMENTS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS attachments (
        uuid                    TEXT PRIMARY KEY,
        shortname               TEXT NOT NULL,
        space_name              TEXT NOT NULL,
        subpath                 TEXT NOT NULL,
        is_active               INTEGER NOT NULL DEFAULT 0,
        slug                    TEXT,
        displayname             TEXT,
        description             TEXT,
        tags                    TEXT,
        created_at              TEXT NOT NULL DEFAULT {NowExpr},
        updated_at              TEXT NOT NULL DEFAULT {NowExpr},
        owner_shortname         TEXT NOT NULL
                                REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname   TEXT,
        acl                     TEXT,
        payload                 TEXT,
        relationships           TEXT,
        last_checksum_history   TEXT,
        resource_type           TEXT NOT NULL,

        media                   BLOB,
        body                    TEXT,
        state                   TEXT,

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- SPACES
    -- ============================================================
    CREATE TABLE IF NOT EXISTS spaces (
        uuid                            TEXT PRIMARY KEY,
        shortname                       TEXT NOT NULL,
        space_name                      TEXT NOT NULL,
        subpath                         TEXT NOT NULL,
        is_active                       INTEGER NOT NULL DEFAULT 0,
        slug                            TEXT,
        displayname                     TEXT,
        description                     TEXT,
        tags                            TEXT,
        created_at                      TEXT NOT NULL DEFAULT {NowExpr},
        updated_at                      TEXT NOT NULL DEFAULT {NowExpr},
        owner_shortname                 TEXT NOT NULL
                                        REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
        owner_group_shortname           TEXT,
        acl                             TEXT,
        payload                         TEXT,
        relationships                   TEXT,
        last_checksum_history           TEXT,
        resource_type                   TEXT NOT NULL DEFAULT 'space',

        root_registration_signature     TEXT NOT NULL DEFAULT '',
        primary_website                 TEXT NOT NULL DEFAULT '',
        indexing_enabled                INTEGER NOT NULL DEFAULT 0,
        capture_misses                  INTEGER NOT NULL DEFAULT 0,
        check_health                    INTEGER NOT NULL DEFAULT 0,
        languages                       TEXT,
        icon                            TEXT NOT NULL DEFAULT '',
        mirrors                         TEXT,
        hide_folders                    TEXT,
        hide_space                      INTEGER,
        active_plugins                  TEXT,
        ordinal                         INTEGER,
        query_policies                  TEXT NOT NULL DEFAULT '[]'
                                        CHECK (json_array_length(query_policies) > 0),

        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- HISTORIES
    -- ============================================================
    CREATE TABLE IF NOT EXISTS histories (
        uuid                  TEXT PRIMARY KEY,
        request_headers       TEXT,
        diff                  TEXT,
        timestamp             TEXT NOT NULL DEFAULT {NowExpr},
        owner_shortname       TEXT,
        last_checksum_history TEXT,
        space_name            TEXT NOT NULL,
        subpath               TEXT NOT NULL,
        shortname             TEXT NOT NULL
    );

    -- ============================================================
    -- LOCKS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS locks (
        uuid              TEXT PRIMARY KEY,
        shortname         TEXT NOT NULL,
        space_name        TEXT NOT NULL,
        subpath           TEXT NOT NULL,
        owner_shortname   TEXT NOT NULL,
        timestamp         TEXT NOT NULL DEFAULT {NowExpr},
        payload           TEXT,
        UNIQUE (shortname, space_name, subpath)
    );

    -- ============================================================
    -- DELETIONS  (tombstones — see docs/parquet-export-design.md §5.2)
    -- ============================================================
    -- See SqlSchema for why these are written in code rather than by a trigger.
    -- INTEGER PRIMARY KEY is SQLite's rowid alias, which is its BIGSERIAL.
    CREATE TABLE IF NOT EXISTS deletions (
        id             INTEGER PRIMARY KEY AUTOINCREMENT,
        table_name     TEXT NOT NULL,
        space_name     TEXT NOT NULL,
        subpath        TEXT NOT NULL,
        shortname      TEXT NOT NULL,
        resource_type  TEXT NOT NULL DEFAULT '',
        deleted_at     TEXT NOT NULL DEFAULT {NowExpr}
    );

    CREATE INDEX IF NOT EXISTS idx_deletions_deleted_at ON deletions (deleted_at);

    -- The instant from which tombstone recording is COMPLETE (§5.2).
    --
    -- An incremental export whose watermark predates this floor cannot see
    -- deletions from the gap, because none were recorded then — the usual cause
    -- being a chain started before this build. Without the floor that gap is
    -- undetectable: missing tombstones look exactly like "nothing was deleted".
    --
    -- Seeded from CODE with a bound local timestamp, not by a DEFAULT: the
    -- server evaluates NOW() in ITS timezone, which is the same trap that put
    -- deleted_at hours out (§5.1).
    --
    -- A pruning job MUST raise this floor to the oldest tombstone it keeps.
    -- Nothing prunes today; the coupling is the point of recording it.
    CREATE TABLE IF NOT EXISTS deletion_retention (
        id INTEGER PRIMARY KEY CHECK (id = 1),
        floor_at  TEXT NOT NULL
    );

    -- ============================================================
    -- INCREMENTAL SCAN INDEXES  (§5.1)
    -- ============================================================
    -- An incremental export selects `updated_at >= watermark` per table. None
    -- of these columns was indexed, so that scan was a seq scan on every table
    -- it touched — a prerequisite for the feature, not an optimization.
    CREATE INDEX IF NOT EXISTS idx_entries_updated_at ON entries (updated_at);
    CREATE INDEX IF NOT EXISTS idx_attachments_updated_at ON attachments (updated_at);
    -- histories is append-only, so its `timestamp` is the equivalent column.
    -- idx_histories_lookup leads with space_name and cannot serve a scan keyed
    -- on time alone.
    CREATE INDEX IF NOT EXISTS idx_histories_timestamp ON histories (timestamp);

    -- ============================================================
    -- SESSIONS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS sessions (
        uuid             TEXT PRIMARY KEY,
        shortname        TEXT NOT NULL,
        token            TEXT NOT NULL,
        timestamp        TEXT NOT NULL DEFAULT {NowExpr},
        firebase_token   TEXT
    );

    -- ============================================================
    -- URL SHORTS
    -- ============================================================
    CREATE TABLE IF NOT EXISTS urlshorts (
        uuid        TEXT PRIMARY KEY,
        token_uuid  TEXT NOT NULL,
        url         TEXT NOT NULL,
        timestamp   TEXT NOT NULL DEFAULT {NowExpr}
    );

    -- ============================================================
    -- OTP   (PostgreSQL uses hstore; here the two-key dictionary is JSON)
    -- ============================================================
    CREATE TABLE IF NOT EXISTS otp (
        key       TEXT PRIMARY KEY,
        value     TEXT NOT NULL,
        timestamp TEXT NOT NULL DEFAULT {NowExpr}
    );

    -- ============================================================
    -- USERPERMISSIONSCACHE
    -- ============================================================
    CREATE TABLE IF NOT EXISTS userpermissionscache (
        user_shortname TEXT PRIMARY KEY,
        permissions    TEXT
    );

    -- ============================================================
    -- INDEXES
    -- ------------------------------------------------------------
    -- Direct ports of the PostgreSQL B-tree indexes. The GIN indexes have no
    -- SQLite counterpart, so JSON containment, tag membership and the ACL
    -- policy filter all scan — see docs/sqlite-backend-audit.md §4 for which
    -- API predicates that affects.
    -- ============================================================
    CREATE INDEX IF NOT EXISTS idx_entries_space_name          ON entries (space_name);
    CREATE INDEX IF NOT EXISTS idx_entries_subpath             ON entries (subpath);
    CREATE INDEX IF NOT EXISTS idx_entries_owner_shortname     ON entries (owner_shortname);
    CREATE INDEX IF NOT EXISTS idx_entries_resource_type       ON entries (resource_type);
    CREATE INDEX IF NOT EXISTS idx_entries_slug                ON entries (slug);
    CREATE INDEX IF NOT EXISTS idx_entries_created_at          ON entries (created_at);
    CREATE INDEX IF NOT EXISTS idx_entries_schema_shortname    ON entries (schema_shortname);
    CREATE INDEX IF NOT EXISTS idx_attachments_space_name      ON attachments (space_name);
    CREATE INDEX IF NOT EXISTS idx_attachments_subpath         ON attachments (subpath);
    CREATE INDEX IF NOT EXISTS idx_attachments_owner_shortname ON attachments (owner_shortname);
    CREATE INDEX IF NOT EXISTS idx_users_owner_shortname       ON users (owner_shortname);
    CREATE INDEX IF NOT EXISTS idx_roles_owner_shortname       ON roles (owner_shortname);
    CREATE INDEX IF NOT EXISTS idx_permissions_owner_shortname ON permissions (owner_shortname);
    CREATE INDEX IF NOT EXISTS idx_groups_owner_shortname      ON groups (owner_shortname);

    -- Global shortname uniqueness: roles, groups and permissions are fetched
    -- and deleted by shortname alone, so the per-table composite UNIQUE is too
    -- weak — it would permit one shortname under two subpaths and make those
    -- lookups ambiguous. Same reasoning as the PostgreSQL schema.
    CREATE UNIQUE INDEX IF NOT EXISTS idx_roles_shortname       ON roles (shortname);
    CREATE UNIQUE INDEX IF NOT EXISTS idx_groups_shortname      ON groups (shortname);
    CREATE UNIQUE INDEX IF NOT EXISTS idx_permissions_shortname ON permissions (shortname);

    CREATE INDEX IF NOT EXISTS idx_sessions_shortname          ON sessions (shortname);
    -- Every authenticated request validates a session on this exact pair.
    CREATE INDEX IF NOT EXISTS idx_sessions_shortname_token    ON sessions (shortname, token);
    CREATE INDEX IF NOT EXISTS idx_histories_lookup
        ON histories (space_name, subpath, shortname, timestamp DESC);

    -- Matches ON CONFLICT (token_uuid) in LinkRepository.CreateWithTokenAsync.
    CREATE UNIQUE INDEX IF NOT EXISTS idx_urlshorts_token_uuid ON urlshorts (token_uuid);

    -- Identifier uniqueness. SQLite supports both partial indexes and
    -- expression indexes, so these port directly. The `<> ''` half of each
    -- predicate matters for the same reason it does on PostgreSQL: legacy rows
    -- may hold '' for "no identifier", and treating '' as collidable would
    -- block index creation on databases with several such rows.
    CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email_lower_unique
        ON users (lower(email)) WHERE email IS NOT NULL AND email <> '';
    CREATE UNIQUE INDEX IF NOT EXISTS idx_users_msisdn_unique
        ON users (msisdn) WHERE msisdn IS NOT NULL AND msisdn <> '';
    CREATE UNIQUE INDEX IF NOT EXISTS idx_users_google_id
        ON users (google_id) WHERE google_id IS NOT NULL AND google_id <> '';
    CREATE UNIQUE INDEX IF NOT EXISTS idx_users_facebook_id
        ON users (facebook_id) WHERE facebook_id IS NOT NULL AND facebook_id <> '';
    CREATE UNIQUE INDEX IF NOT EXISTS idx_users_apple_id
        ON users (apple_id) WHERE apple_id IS NOT NULL AND apple_id <> '';

    -- ============================================================
    -- WILDCARD SEARCH INDEX
    -- ------------------------------------------------------------
    -- The SQLite counterpart of PostgreSQL's pg_trgm GIN over
    -- (payload::text), which accelerates `@payload.body.x:*foo*` lookups.
    -- FTS5's `trigram` tokenizer is the only one that can serve a
    -- LIKE '%...%' query from an index, and unlike `unicode61` it does not
    -- shatter diacritized Arabic into single letters (see the audit, §5) —
    -- it indexes character trigrams, so scripts without word breaks work.
    --
    -- external content (content='entries'): the FTS table stores only the
    -- index, reading column values back from `entries` by rowid. That keeps
    -- the payload from being duplicated, at the cost of needing the triggers
    -- below to stay in sync.
    --
    -- Those triggers are load-bearing for CORRECTNESS, not just freshness.
    -- The wildcard filter ANDs this prefilter onto a precise per-path check,
    -- so a stale index cannot produce wrong rows — but it can silently drop
    -- rows that should have matched. Every write path to `entries` must be
    -- reflected here, which is why the triggers hang off the table rather
    -- than off any particular repository method.
    --
    -- Patterns shorter than 3 characters cannot be served by a trigram index;
    -- SQLite falls back to scanning the FTS content, exactly as PostgreSQL's
    -- planner does for short pg_trgm patterns.
    CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
        payload,
        content='entries',
        content_rowid='rowid',
        tokenize='trigram'
    );

    CREATE TRIGGER IF NOT EXISTS entries_fts_ai AFTER INSERT ON entries BEGIN
        INSERT INTO entries_fts(rowid, payload) VALUES (new.rowid, new.payload);
    END;

    CREATE TRIGGER IF NOT EXISTS entries_fts_ad AFTER DELETE ON entries BEGIN
        INSERT INTO entries_fts(entries_fts, rowid, payload)
        VALUES ('delete', old.rowid, old.payload);
    END;

    CREATE TRIGGER IF NOT EXISTS entries_fts_au AFTER UPDATE ON entries BEGIN
        INSERT INTO entries_fts(entries_fts, rowid, payload)
        VALUES ('delete', old.rowid, old.payload);
        INSERT INTO entries_fts(rowid, payload) VALUES (new.rowid, new.payload);
    END;
    """;

    // Tables the initializer patches for forward-compatibility, mirroring the
    // PostgreSQL schema's `ADD COLUMN IF NOT EXISTS` block. SQLite has no
    // IF NOT EXISTS on ADD COLUMN, so SqliteSchemaInitializer probes
    // pragma_table_info first.
    //
    // Only columns that a SELECT or INSERT references need to be here. A column
    // added to CreateAll must ALSO be added here, or an existing database
    // created before the change never gets it.
    public static readonly IReadOnlyList<(string Table, string Column, string Definition)> ExpectedColumns = new[]
    {
        ("users", "device_id", "TEXT"),
        ("users", "google_id", "TEXT"),
        ("users", "facebook_id", "TEXT"),
        ("users", "apple_id", "TEXT"),
        ("users", "social_avatar_url", "TEXT"),
        ("users", "attempt_count", "INTEGER"),
        ("users", "last_failed_login", "TEXT"),
        ("users", "last_login", "TEXT"),
        ("users", "notes", "TEXT"),
        ("users", "locked_to_device", "INTEGER NOT NULL DEFAULT 0"),
        ("users", "last_checksum_history", "TEXT"),
        ("users", "is_deleted", "INTEGER NOT NULL DEFAULT 0"),
        ("users", "deleted_at", "TEXT"),
        ("roles", "last_checksum_history", "TEXT"),
        ("roles", "grantable_by", "TEXT"),
        ("permissions", "last_checksum_history", "TEXT"),
        ("entries", "last_checksum_history", "TEXT"),
        ("spaces", "last_checksum_history", "TEXT"),
        ("spaces", "active_plugins", "TEXT"),
        ("spaces", "hide_folders", "TEXT"),
        ("spaces", "hide_space", "INTEGER"),
        ("spaces", "ordinal", "INTEGER"),
        ("spaces", "mirrors", "TEXT"),
    };
}
