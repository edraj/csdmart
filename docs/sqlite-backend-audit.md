# SQLite backend — Phase 1 audit

Status: **audit only, no implementation**. This document answers the ten questions
in the Phase 1 brief and ends with a recommendation and a list of things I need a
decision on before Phase 2.

Scope of the survey: 479 `.cs` files / ~100k lines. **40 non-test files reference
Npgsql**; 20 of those are inside `DataAdapters/Sql/`, the other 20 are not.

Everything marked **[verified]** was executed on this machine (SQLite 3.51.2,
.NET SDK 10.0.110, `Microsoft.Data.Sqlite` 10.0.5) rather than recalled. The probe
lives in the session scratchpad, not in the repo.

---

## 0. Executive summary

Three findings dominate, and they should drive the Phase 2/3 decision more than
the long tail of dialect differences:

1. **`LIKE` is case-insensitive in SQLite and case-sensitive in PostgreSQL.**
   csdmart's row-level ACL is built on `LIKE`
   (`QueryHelper.AppendAclFilter`, `DataAdapters/Sql/QueryHelper.cs:235`). Under
   SQLite the same policy pattern matches *more* rows than under PostgreSQL.
   This is an access-control widening, not a cosmetic difference. **[verified]**
   — see §2.3. This is a STOP-and-ask item (§11).

2. **`Microsoft.Data.Sqlite`'s `*Async` methods complete synchronously.**
   **[verified]**: `ExecuteScalarAsync` returned an already-completed task on the
   calling thread. csdmart has ~250 `await`ed ADO call sites and its request path
   is async end-to-end. This does not deadlock, but it changes the thread-pool
   model under load. Recommendation in §3.

3. **FTS5 + `unicode61` destroys diacritized Arabic.** **[verified]**:
   `مَرْحَبًا` tokenizes to five single-letter tokens (`م ر ح ب ا`) while the
   undiacritized `مرحبا` tokenizes to one. `remove_diacritics 2` does not cover
   Arabic tashkeel — it treats the combining marks as token separators. FTS5 with
   `unicode61` is **disqualified** for this codebase's content. See §5.

The good news: the seam is much smaller than the Npgsql file count suggests,
because of one accident of design — csdmart emits **positional `$N` placeholders**,
and `$1` is a valid *named* parameter in SQLite. **[verified]**: the same SQL text
binds under both providers with no placeholder rewriting. That removes what is
usually the single largest source of churn in a two-dialect port.

---

## 1. Npgsql leakage

### 1.1 The structural leak: the query grammar package depends on Npgsql

`Dmart.QueryGrammar` is otherwise a pure string-in/SQL-out library, but its public
return type is Npgsql-typed:

- `Dmart.QueryGrammar/SearchExpressionParser.cs:61`
  `public sealed record Parsed(IReadOnlyList<string> Clauses, IReadOnlyList<NpgsqlParameter> Parameters);`
- `Dmart.QueryGrammar/SearchExpressionParser.cs:163` — `var pars = new List<NpgsqlParameter>();`
- `Dmart.QueryGrammar/SearchExpressionParser.cs:207-259` — `ParamCtx`, whose
  `Add(object?, NpgsqlDbType?)` constructs `NpgsqlParameter` directly (lines 224, 240-255).
- `Dmart.QueryGrammar/Dmart.QueryGrammar.csproj:46` — `<PackageReference Include="Npgsql" />`

This is the leak that matters most: it is the one place where making the type
provider-neutral forces a public API change in a package that declares
`IsAotCompatible` and multi-targets `net8.0;net10.0`.

`NpgsqlDbType` uses inside the grammar (the only typed params it binds):

| File:line | Type |
|---|---|
| `SearchExpressionParser.cs:986` | `NpgsqlDbType.Boolean` |
| `SearchExpressionParser.cs:1106` | `NpgsqlDbType.Jsonb` |
| `SearchExpressionParser.cs:1126`, `:1128` | `NpgsqlDbType.Jsonb` |
| `SearchExpressionParser.cs:1154` | `NpgsqlDbType.Jsonb` |
| `SearchExpressionParser.cs:1224` | `NpgsqlDbType.Boolean` |

Only two of the sixteen `NpgsqlDbType` members are needed here (`Jsonb`,
`Boolean`), and under SQLite both collapse to TEXT/INTEGER. The abstraction cost
is genuinely small.

### 1.2 `NpgsqlDbType` outside the grammar

Complete inventory (`.Array | .Text` is the dominant form — 24 of 39 sites):

**`NpgsqlDbType.Array | NpgsqlDbType.Text`** — bound to `text[]` columns
(`query_policies`) and to `= ANY($n)` list filters:
`AccessRepository.cs:58,122,176,249,279,362`; `AttachmentRepository.cs:109`;
`EntryRepository.cs:207,337,643,836`; `HistoryRepository.cs:97,152`;
`QueryHelper.cs:70,80,93,104`; `SpaceRepository.cs:147`;
`UserRepository.cs:256,413,574,674`; `Dmart.SqlAdapter/DmartSqlAdapter.cs:491,933`;
`Dmart.SqlAdapter/Permissions/PermissionEngine.cs:306,330`; `Program.cs:135`.

**`NpgsqlDbType.Jsonb`**: `AccessRepository.cs:575,619,622`;
`AttachmentRepository.cs:301,304`; `EntryRepository.cs:916,925`;
`HistoryRepository.cs:33,38`; `SpaceRepository.cs:205,208`;
`UserRepository.cs:964,973`; `Dmart.SqlAdapter/DmartSqlAdapter.Endpoints.cs:121,126`;
`Dmart.SqlAdapter/Helpers/JsonbHelpers.cs:40,53`.

**`NpgsqlDbType.Timestamp`**: `UserRepository.cs:685,697`.
**`NpgsqlDbType.Bytea`**: `AttachmentRepository.cs:229`.
**`NpgsqlDbType.Hstore`**: `OtpRepository.cs:31` — see §2.6.
**`NpgsqlDbType.Uuid` / `.Text` / `.Boolean` / `.Unknown`**: the COPY writer,
`Services/ImportExportService.cs:2593-2759` (see §1.4).

### 1.3 Concrete Npgsql types used where an ADO.NET base class would do

Raw counts across non-test code: `NpgsqlCommand` 233, `NpgsqlConnection` 72,
`NpgsqlParameter` 58, `NpgsqlDataReader` 14, `NpgsqlTransaction` 5,
`NpgsqlBinaryImporter` 2. **`NpgsqlDataSource`/`NpgsqlDataSourceBuilder`: 0 —
csdmart never adopted the data-source API**, it news up a connection per call
(`DataAdapters/Sql/Db.cs:27-34`). That is convenient: there is no data-source
lifetime to abstract.

Almost all of these are mechanically replaceable with `DbConnection` /
`DbCommand` / `DbDataReader`, because the code only ever calls base-class members.
The exceptions that genuinely need Npgsql:

- **`Db.FastImportSession`** — `DataAdapters/Sql/Db.cs:94-417`. Holds
  `NpgsqlConnection`/`NpgsqlTransaction` as fields (lines 98-99, 123), and its
  whole retry/reconnect design is built on PostgreSQL SQLSTATE classification
  (`IsTransient` line 379, `IsConnectionFailureState` line 408, `IsTimeout` line 398).
- **Connection-string assembly** — `NpgsqlConnectionStringBuilder` at
  `Db.cs:470` and `Db.cs:516`.
- **Deadlock retry** — `Db.ExecuteWithRetryOnDeadlockAsync` catches
  `PostgresException` with SQLSTATE `40P01` (`Db.cs:436`).

### 1.4 Npgsql escaping into domain/service/CLI code (the brief's actual question)

Twenty non-test files outside `DataAdapters/Sql/` touch Npgsql:

| File:line | What escapes | Notes |
|---|---|---|
| `Services/QueryService.cs:11-12,649,665` | `List<NpgsqlParameter>`, `NpgsqlCommand` | Builds and executes the aggregation query in the service layer |
| `Services/ImportExportService.cs:2587-2759` | `BeginBinaryImportAsync`, `NpgsqlDbType.*` | Binary COPY — no SQLite equivalent (§2.9) |
| `Services/ImportBulkIndexes.cs:74,90,111,120,144` | `NpgsqlConnection`, `NpgsqlCommand` | GIN drop/rebuild around bulk load; PG-only by nature |
| `Services/EntryService.cs:691` | `PostgresException` SQLSTATE `23505` | Unique-violation mapping |
| `Services/SemanticSearchService.cs:107,138` | `List<NpgsqlParameter>`, `NpgsqlCommand` | pgvector |
| `Services/SemanticIndexerService.cs:165`, `Services/EmbeddingProvider.cs:72,176` | `NpgsqlCommand` | pgvector |
| `Auth/OAuth/OAuthUserResolver.cs:165` | `PostgresException` `23505` | |
| `Api/HealthEndpoints.cs:40` | `NpgsqlCommand("SELECT 1")` | Trivial |
| `Plugins/BuiltIn/DbSizeInfoPlugin.cs:29` | `NpgsqlCommand` + `pg_total_relation_size` | PG-only |
| `Cli/SelfCheckCommand.cs:153,166`, `Cli/FolderRenderingFixer.cs:74,181` | `NpgsqlCommand` | |
| `Program.cs:135,1138,1165` | `NpgsqlDbType`, `pg_advisory_lock` | Composition root + startup lock |
| `Utils/PgErrorParsing.cs` | Parses `PostgresException` fields | Error-message parity helper |
| `Config/Telemetry.cs:28,33` | OTel `"Npgsql"` ActivitySource | Cosmetic |

`Dmart.SqlAdapter` (the distributable SDK) is a **separate concern**: it is a
standalone PostgreSQL client library (`Dmart.SqlAdapter.csproj:27` explicitly
declines to advertise AOT compatibility). I recommend leaving it PostgreSQL-only
and saying so in the readme — see §9.

### 1.5 Things the brief asked me to hunt that are **absent**

`NpgsqlRange`: 0. `EnableDynamicJson`: 0. `MapComposite`: 0. Custom type
handlers: 0. `NpgsqlDataSource(Builder)`: 0. `NpgsqlBatch`: 0. Composite/enum
*client-side* mapping: 0 — the two PG enum types (`usertype`, `language`,
`SqlSchema.cs:38-44`) are read and written as strings, never mapped to CLR enums
by Npgsql. Array mapping is confined to the single `text[]` column
`query_policies` plus `= ANY($n)` parameter lists.

This is a better starting position than the file count implies.

---

## 2. SQL dialect dependencies

### 2.1 JSONB operators and GIN

csdmart stores nine JSONB columns per Metas table and filters on them heavily.

| Construct | Where (representative) | SQLite equivalent |
|---|---|---|
| `payload->'a'->'b'` | `SearchExpressionParser.cs:975,1048,1107`; `QueryHelper.cs` `BuildJsonbPath` | `payload -> '$.a.b'` — exists, **different semantics**: SQLite `->` returns JSON *text*, PG returns `jsonb`. **[verified]** |
| `payload->>'schema_shortname'` | `QueryHelper.cs:95`; `SqlSchema.cs:428` | `payload ->> '$.schema_shortname'` **[verified]** |
| `payload::jsonb @> $n` (containment) | `SearchExpressionParser.cs:1111,1127,1129,1169`; `HealthCheckRepository.cs:94,99,105` | **none.** Must be rewritten as `EXISTS (SELECT 1 FROM json_each(...) WHERE value = ?)` for the array case and a path-equality test for the scalar case. This is the single largest rewrite. |
| `tags ?\| $n` (any-key-exists) | `QueryHelper.cs:106` | **none.** → `EXISTS (SELECT 1 FROM json_each(tags) WHERE value IN (...))` |
| `(elem->'allowed_actions') ? 'query'` | `QueryHelper.cs:215` | **none.** → `json_each` |
| `jsonb_typeof(x)` | ~30 sites in `SearchExpressionParser.cs`, `HealthCheckRepository.cs:90,92,97,103` | `json_type(x)` — **different vocabulary**: PG returns `'string'/'number'/'boolean'/'array'/'object'/'null'`; SQLite returns `'text'/'integer'/'real'/'true'/'false'/'array'/'object'/'null'`. Note PG's single `boolean` vs SQLite's `true`/`false`, and `number` vs `integer`/`real`. Every comparison must be remapped, not textually substituted. |
| `jsonb_array_elements(...)` | `QueryHelper.cs:215` | `json_each(...)` **[verified]** |
| `jsonb_array_length(...)` | `HealthCheckRepository.cs:93,98,104` | `json_array_length(...)` |
| `to_jsonb(x::text)` | `HealthCheckRepository.cs:94,99,105` | `json_quote(x)` |
| `GIN (… jsonb_path_ops)` | `SqlSchema.cs:409-440` (10 indexes) | **none.** SQLite has no general JSON index. See §5. |
| `GIN (query_policies)` (array) | `SqlSchema.cs:435-440` (6 indexes) | **none.** |

### 2.2 Text arrays

`query_policies TEXT[]` is the only array column (`SqlSchema.cs:89,119,148,183,217,287`).
Used via `unnest(query_policies) AS qp` (`QueryHelper.cs:240`,
`SearchExpressionParser.cs:1195`) and `array_length(query_policies, 1)`
(`SqlSchema.cs:624,635`).

SQLite equivalent: store as a JSON array in TEXT, iterate with `json_each`.
Mechanical, but it changes the storage format, so the SQLite schema is **not**
the PostgreSQL schema. That is fine — the SQL store is a rebuildable index — but
it means the two DDLs diverge and cannot share migration text.

### 2.3 `ILIKE` / `LIKE` / trigram — **the correctness hazard**

- `ILIKE`: 20+ sites, e.g. `SearchExpressionParser.cs:1066,1085,1164,1170,1188,1211`.
- `LIKE`: `QueryHelper.cs:62` (subpath prefix), `QueryHelper.cs:235` (**ACL**).
- `pg_trgm` GIN over `(payload::text)`: `SqlSchema.cs:713-714`, created
  `CONCURRENTLY`; the wildcard branch emits a matching prefilter at
  `SearchExpressionParser.cs:1085`.

**[verified] SQLite `LIKE` semantics:**

```
'ADMIN' LIKE 'admin'                              → 1   (ASCII case-INSENSITIVE)
'ADMIN' = 'admin'                                 → 0   (= is case-sensitive)
'É'     LIKE 'é'                                  → 0   (non-ASCII NOT folded)
'management:/USERS:*' LIKE 'management:/users:%'  → 1   ← ACL WIDENING
```

Two distinct problems:

- **`ILIKE` → `LIKE` is a *narrowing* for non-ASCII.** PostgreSQL `ILIKE` folds
  Unicode; SQLite `LIKE` folds ASCII only. Arabic is unaffected (no case), but
  accented Latin search results will differ.
- **`LIKE` → `LIKE` is a *widening* for the ACL.** The ACL patterns at
  `QueryHelper.cs:228-235` are compared case-sensitively today. Under SQLite the
  identical SQL grants access on case-differing paths. `PRAGMA case_sensitive_like=ON`
  fixes the ACL but simultaneously breaks every `ILIKE` site, since both compile
  to `LIKE`. They cannot both be satisfied by a connection-level pragma.

The clean fix is to stop overloading one operator: emit `LIKE` for the ACL and
case-folded comparisons elsewhere via an explicit `lower()` on both sides, or use
`GLOB` (**[verified]** case-sensitive) for the ACL. I want a decision before I
touch this — see §11.

### 2.4 Types: `uuid`, `timestamptz`, `gen_random_uuid()`, `now()`

- `UUID` columns: primary key on every table (`SqlSchema.cs:50,98,128,157,192,...`).
  SQLite: no UUID type → TEXT. See §6.
- `gen_random_uuid()` (pgcrypto): 10 sites —
  `LockRepository.cs:52`, `LinkRepository.cs:24,38`, `HistoryRepository.cs:27`,
  `UserRepository.cs:569,779`, `Cli/SelfCheckCommand.cs:155`,
  `Dmart.SqlAdapter/DmartSqlAdapter.cs:805`, `DmartSqlAdapter.Endpoints.cs:114,203`.
  SQLite: **none** built in. Generate the Guid client-side and bind it — which is
  what the rest of the codebase already does (`Guid.Parse(...)` at
  `SpaceRepository.cs:104`, `AccessRepository.cs:101,227,334`).
- `NOW()`: ~20 sites in DDL defaults plus `SpaceRepository.cs:81`,
  `EntryRepository.cs:632,712,810`, `HistoryRepository.cs:27`.
  SQLite: `CURRENT_TIMESTAMP` has second resolution only and no local-time
  semantics — **not** a drop-in. csdmart is deliberately timezone-less and
  local-wall-clock (`Utils/TimeUtils.cs:11,20`), so the correct move is to bind
  `TimeUtils.Now()` from the client rather than translate `NOW()`.
- `timestamptz`: only in the one-time migration DO block (`SqlSchema.cs:665-684`)
  and in `SearchExpressionParser.cs:1238` (`{p}::timestamptz` in the timestamp
  range filter). Live columns are all `TIMESTAMP WITHOUT TIME ZONE`. Good — no
  offset handling to port.
- `EXTRACT(EPOCH FROM (NOW() - timestamp))::int`: `OtpRepository.cs:47`.
  SQLite: `CAST((julianday('now') - julianday(timestamp)) * 86400 AS INTEGER)`.

### 2.5 `DISTINCT ON`, laterals, upsert, `RETURNING`, window functions

- `DISTINCT ON`: **0 sites.** Nothing to port.
- Lateral joins: **0 sites.**
- Window functions: **0 sites.**
- `ON CONFLICT`: 44 sites. SQLite supports `ON CONFLICT (cols) DO UPDATE SET`
  with `excluded.*` — compatible. One caveat: SQLite requires the conflict target
  to match a unique index, same as PG, and `EXCLUDED` is spelled `excluded`
  (case-insensitive, so no change).
- `RETURNING`: 24 sites. Supported by SQLite 3.35+ (we have 3.51.2). Compatible.

### 2.6 Extensions, schemas, sequences

- `hstore` (`SqlSchema.cs:29`) — used only for `otp.value`
  (`SqlSchema.cs:347`, written at `OtpRepository.cs:31`, a two-key dictionary
  `{code, expires_at}`). SQLite: store as JSON TEXT. Low risk, isolated.
- `pgcrypto` (`SqlSchema.cs:30`) — only for `gen_random_uuid()`; see §2.4.
- `pg_trgm` (`SqlSchema.cs:36`) — see §5.
- `vector`/pgvector (`SqlSchema.cs:648-652`) — already optional and gated behind
  `EmbeddingProvider.IsEnabledAsync`. Stays off under SQLite.
- Sequences / `nextval` / `SETVAL`: **0 sites.**
- Schemas: `public` is assumed in `ImportBulkIndexes.cs:120`
  (`DROP INDEX IF EXISTS public.{name}`) and the `current_schema()` probes in
  `SqlSchema.cs:537,552,577`. SQLite has no schemas (only attached databases).
- PG catalogs: `pg_indexes`, `pg_constraint`, `pg_extension`, `information_schema.columns`
  (`SqlSchema.cs:536,551,576,621,649,671`), `pg_total_relation_size`
  (`DbSizeInfoPlugin.cs:19-23`). SQLite: `pragma_table_info`, `pragma_index_list`,
  `sqlite_master`, `PRAGMA page_count * page_size`.

### 2.7 `FOR UPDATE`, advisory locks, `LISTEN/NOTIFY`

- `SELECT … FOR UPDATE`: `EntryRepository.cs:266`, `UserRepository.cs:313`
  (read-modify-write upserts). SQLite: **no row locks**. Under a single-writer
  model this is *implicitly* satisfied inside a write transaction — but only if
  the transaction is `BEGIN IMMEDIATE`. With SQLite's default deferred begin, two
  readers can both read, then one gets `SQLITE_BUSY` at write time. The SQLite
  path must open these two transactions as `BEGIN IMMEDIATE`. Semantically
  equivalent, mechanically different.
- `pg_advisory_lock(1)`: `SchemaInitializer.cs:35,108` and `Program.cs:1138,1165`
  — guards concurrent schema init across processes. SQLite: a write transaction
  on the single DB file gives the same mutual exclusion; the advisory lock becomes
  a no-op.
- `LISTEN`/`NOTIFY`: **0 sites.** Nothing to port.

### 2.8 Aggregations (`QueryHelper.cs:607-630`)

| Reducer | PG emit | SQLite |
|---|---|---|
| `count`, `count_distinct`, `sum`, `avg`, `min`, `max` | standard | fine (drop the `::numeric` casts) |
| `stddev` | `STDDEV(...)` | **none** built in |
| `group_concat`/`tolist` | `STRING_AGG(x, ',')` | `group_concat(x, ',')` |
| `quantile` | `percentile_cont(q) WITHIN GROUP (ORDER BY ...)` | **none** — no ordered-set aggregates |
| `first_value` | `(ARRAY_AGG(x ORDER BY updated_at DESC))[1]` | no ordered array_agg; needs a window function or correlated subquery |
| `random_sample` | `(ARRAY_AGG(x ORDER BY RANDOM()))[1]` | same |

`stddev` and `quantile` have no SQLite equivalent without a custom function.
`Microsoft.Data.Sqlite` can register one via `SqliteConnection.CreateFunction`
(delegate-based, AOT-safe), but aggregate registration is per-connection —
which interacts with pooling (§10).

### 2.9 Bulk load

`ImportExportService.cs:2587,2680` use `BeginBinaryImportAsync` (PostgreSQL binary
COPY) into TEMP scratch tables, then merge with `INSERT … ON CONFLICT`.
SQLite has **no COPY**. The equivalent is a prepared `INSERT` reused inside one
transaction, which is genuinely fast (SQLite's bottleneck is fsync, not parse).
This is the rebuild-index-from-flat-files path and needs its own implementation,
not a translation.

### 2.10 DDL and migration SQL

All of it is in `SqlSchema.cs` (727 lines):
`CreateAll` (lines 19-685) and `ConcurrentIndexes` (lines 705-726).

Not portable, by construction:
- `SET statement_timeout`/`lock_timeout` (26-27) — SQLite has no equivalent.
- `CREATE EXTENSION` ×3 (29-36).
- `CREATE TYPE … AS ENUM` ×2 (38-44) — SQLite: TEXT + `CHECK` constraint.
- Four `DO $$ … $$` PL/pgSQL blocks (531-596, 612-639, 648-652, 665-684) —
  procedural, no SQLite analogue. The duplicate-detection-then-conditional-index
  logic must move into C#.
- `ADD COLUMN IF NOT EXISTS` ×25 (459-484) — SQLite has `ADD COLUMN` but **no
  `IF NOT EXISTS`**; must probe `pragma_table_info` first. (`ExpectedColumnPatcher.cs`
  already does something similar and is a reasonable model.)
- Partial unique indexes (494-499, 546, 561) — **supported** by SQLite.
- Expression index `((payload->>'schema_shortname'))` (427-428) — supported via a
  generated column (§5). **[verified]** the planner uses it.
- `CREATE INDEX CONCURRENTLY` (713, 724) — SQLite has no concurrent index build;
  it locks. Acceptable at the tier's data sizes.
- `DEFERRABLE INITIALLY DEFERRED` FKs (109, 139, 168, 203, 237, 267) — SQLite
  supports `DEFERRABLE INITIALLY DEFERRED` **only** with `PRAGMA foreign_keys=ON`
  and it applies at commit. Roughly equivalent; needs verification per FK.

---

## 3. The async problem — recommendation

**[verified]** on this machine: `ExecuteScalarAsync` on `Microsoft.Data.Sqlite`
returned `IsCompleted == true` before any `await`, on the same managed thread.
There is no I/O completion port behind it; the provider implements the async API
over synchronous `sqlite3_step`. SQLite has no async file I/O to expose.

**Where csdmart assumes real async.** Every HTTP handler is async to the socket,
and the ADO calls are the only blocking-capable work in the pipeline. Highest-density
call sites: `UserRepository.cs` (42 `*Async` DB calls), `EntryRepository.cs` (28),
`AccessRepository.cs` (21), `AttachmentRepository.cs` (15),
`Dmart.SqlAdapter/DmartSqlAdapter.cs` (25). Session validation
(`UserRepository.IsSessionValidAsync`/`TouchSessionAsync`, indexed at
`SqlSchema.cs:397`) runs on **every authenticated request** — that is the hottest path.

**Thread-pool impact under load.** With PostgreSQL, a request awaiting a query
releases its thread and the pool stays small. With SQLite, the call occupies its
thread for the duration of the query. Consequences, in order of importance:

1. Concurrency becomes bounded by thread-pool width, not by connection count. The
   pool grows by hill-climbing (~1-2 threads/second beyond the core count), so a
   sudden burst queues rather than scales. On an 8-core box a burst of 200
   concurrent reads will see latency spikes for several seconds until the pool
   catches up.
2. Under WAL, readers do not block and queries are typically microseconds-to-low-
   milliseconds, so occupancy per call is small. It is writes — serialized by
   SQLite's single-writer rule — that hold threads longest.
3. `Kestrel`'s accept loop is unaffected; the risk is thread starvation manifesting
   as latency, not as deadlock.

**Recommendation: do nothing clever. Call the `*Async` methods and accept that
they complete synchronously.** Specifically:

- Do **not** wrap SQLite calls in `Task.Run`. That trades one thread for two
  (the caller's continuation plus the pool item) and adds scheduling latency for
  work that typically finishes in microseconds. It is a net loss for short queries
  and only marginally helps long ones.
- Do **not** introduce a separate sync code path. Duplicating ~250 call sites to
  avoid a state-machine allocation is a large, permanent maintenance cost for a
  tier explicitly scoped to "dev, CI, single-node, small/edge".
- **Do** set `ThreadPool.SetMinThreads` to a floor above core count when the
  SQLite driver is active, so a burst does not wait on hill-climbing. This is a
  one-line, driver-scoped mitigation.
- **Do** document the ceiling honestly in the readme: the SQLite tier's useful
  concurrency is tens of concurrent requests, not thousands.

The reason this is safe: the tier's stated scope already excludes the load
regime where the thread-pool model would matter. Engineering around it would be
optimizing for a deployment the tier is not for.

---

## 4. Filter surface — what stays index-backed

API-exposed predicates, from `Query` via `QueryHelper.BuildWhereClause` and the
search grammar.

**Index-backed under SQLite** (plain B-tree, direct ports of existing indexes):

| Predicate | Emit site | Serving index |
|---|---|---|
| `space_name = ?` | `QueryHelper.cs:49` | `idx_entries_space_name` |
| `subpath = ?` / `subpath LIKE ? \|\| '/%'` | `QueryHelper.cs:57,62` | `idx_entries_subpath` (prefix `LIKE` is index-usable only with a literal prefix and `case_sensitive_like=ON`; otherwise it scans — see §11) |
| `resource_type = ANY(?)` | `QueryHelper.cs:72` | `idx_entries_resource_type`, rewritten as `IN (...)` |
| `shortname = ANY(?)` | `QueryHelper.cs:82` | UNIQUE`(shortname,space_name,subpath)` |
| `payload->>'schema_shortname' = ANY(?)` | `QueryHelper.cs:95` | generated column + index — **[verified]** planner picks it |
| `slug = ?` | `SqlSchema.cs:433` | `idx_entries_slug` |
| `created_at` range | `QueryHelper.cs:116,121` | needs a new index; text timestamps sort correctly (§6) |
| session `(shortname, token)` | `SqlSchema.cs:397` | direct port |
| history `(space,subpath,shortname,timestamp DESC)` | `SqlSchema.cs:398` | direct port |
| `lower(email)`, `msisdn`, provider ids | `SqlSchema.cs:546,561,590` | partial unique indexes — supported |

**Degrades to a scan under SQLite:**

| Predicate | Emit site | Why |
|---|---|---|
| `tags ?\| ?` | `QueryHelper.cs:106` | `json_each` rewrite is a per-row table-valued function; no index |
| `payload @> ?` (all JSON containment) | `SearchExpressionParser.cs:1127,1129` | no GIN analogue |
| `roles`/`groups`/`permissions` array match | `SearchExpressionParser.cs:1163,1169` | same |
| `relationships @> ?` (delete-time RI probe) | `SqlSchema.cs:419-420` | same — this one is a **hot** gate on every delete |
| ACL `query_policies` LIKE | `QueryHelper.cs:240` | `json_each` + `LIKE`; no index. Runs on **every** authorized query |
| wildcard `@payload.body.x:*foo*` | `SearchExpressionParser.cs:1085` | no `pg_trgm`; FTS5 `trigram` can serve it (§5) |
| arbitrary JSON path compare | `SearchExpressionParser.cs:961+` | only hot paths can get generated columns |

The two that concern me are the **ACL filter** (every query) and the
**relationships probe** (every delete). At the tier's data sizes a scan is
tolerable; both should be called out in the readme as the things that set the
practical row ceiling.

---

## 5. JSON indexing strategy

### (a) Generated columns + expression indexes on hot paths

**[verified]** working end to end:

```sql
schema_shortname TEXT GENERATED ALWAYS AS (payload ->> '$.schema_shortname') VIRTUAL
CREATE INDEX idx_entries_schema_shortname ON entries(schema_shortname);
-- EXPLAIN QUERY PLAN → SEARCH entries USING INDEX idx_entries_schema_shortname
```

`VIRTUAL` costs no storage and is computed on read; `STORED` costs storage and is
computed on write. Either is indexable. Adding a generated column to an existing
table is allowed (`ALTER TABLE ADD COLUMN ... GENERATED`) for `VIRTUAL` only —
convenient for the patcher path.

Pros: exact, planner-visible, no write-path code, no consistency risk (the engine
maintains it). Cons: only covers paths known at schema-design time.

### (b) Normalized `(entry_id, json_path, value)` side table

Pros: covers arbitrary paths; one index serves all of them.
Cons: this is a hand-rolled inverted index maintained in application code on
every write. It must be kept transactionally consistent with `entries`, rebuilt
by the flat-file reindex, and it multiplies row count by the number of JSON leaves
(csdmart payloads are nested — `payload.body.*` is arbitrary user schema, so this
is easily 20-50× the row count). It also reintroduces exactly the write
amplification that made the GIN indexes the dominant import cost on the PostgreSQL
side (per the existing import-performance work, GIN was measured as the single
largest lever). I do not recommend building a second index engine inside a backend
whose stated purpose is "dev, CI, single-node, small/edge".

### (c) FTS5

**[verified], and this is the finding that decides it.** With
`tokenize='unicode61 remove_diacritics 2'`:

```
INSERT 'مَرْحَبًا'  → tokens: م  ر  ح  ب  ا     (five single-letter tokens)
INSERT 'مرحبا'    → tokens: مرحبا              (one token)
```

`remove_diacritics 2` does **not** cover Arabic tashkeel (U+064B-U+0652). unicode61
classifies those combining marks as non-token characters, so they act as
*separators* and shatter the word. The same word written with and without
diacritics indexes completely differently and will not match. Additionally, with
no stemming and no clitic handling, `الكتاب` does not match a query for `كتاب`
(**[verified]**: 0 hits) — Arabic's attached definite article and affixes make
unstemmed matching notably worse than for English.

The `trigram` tokenizer avoids both problems — **[verified]** it matches
substrings inside diacritized Arabic, and it can serve `LIKE '%...%'`
(**[verified]**), making it the true `pg_trgm` analogue.

### Recommendation

**(a) as the primary strategy, plus FTS5 with the `trigram` tokenizer — not
`unicode61` — solely as the replacement for the `pg_trgm` wildcard prefilter.
Reject (b).**

Rationale against the §4 filter surface: the predicates that actually need index
support are the fixed, known ones (`schema_shortname`, `slug`, `created_at`,
identifier lookups) — all covered by (a) with exact semantics and zero write-path
code. The only genuinely open-ended predicate is the `*foo*` wildcard, which is
precisely what trigram indexing exists for and which already has an
"approximate prefilter + exact recheck" design on the PostgreSQL side
(`SearchExpressionParser.cs:1070-1086`) that ports directly. Containment filters
degrade to scans, which is the honest tier limit rather than a reason to build (b).

If full-text search over Arabic is ever required as a *feature* rather than as a
wildcard accelerator, the answer is normalizing tashkeel at write time, not
tuning `unicode61` — and that is a product decision, not a backend one.

---

## 6. Type round-tripping

Microsoft.Data.Sqlite maps CLR types onto SQLite's four storage classes. What
csdmart actually needs is narrow — **[verified] `DateTimeOffset` and `decimal` do
not appear in the data layer at all** (0 and 1 occurrences respectively), because
the system is deliberately timezone-less (`Utils/TimeUtils.cs:11,20`).

Reader accessors in use across the data layer: `GetString` 262, `GetBoolean` 29,
`GetDateTime` 24, `GetGuid` 15, `GetDouble` 8, `GetInt32` 3, `GetInt64` 1.

| CLR type | Storage | Format | Ordering / equality |
|---|---|---|---|
| `Guid` | **TEXT** | `00000000-0000-0000-0000-000000000000` (lowercase, hyphenated — `Guid.ToString("D")`) | Equality: exact string match, so **binding must be canonical**. `GetGuid` parses it back — **[verified]** round-tripped correctly. Ordering is lexicographic over the string, which differs from PG's `uuid` byte order; nothing sorts by uuid today, so this is inert. Do **not** use BLOB: Microsoft.Data.Sqlite's default for `Guid` is BLOB in some paths, and mixed representations break equality silently. Pin it to TEXT explicitly. |
| `DateTime` | **TEXT** | `yyyy-MM-dd HH:mm:ss.fffffff` — space separator, no `T`, no offset, no `Z` | **Lexicographic-safe by construction**: fixed width, zero-padded, most-significant-first, single format. This is what makes `ORDER BY created_at` and `BETWEEN` correct without a functional index. Kind must be `Unspecified` (`TimeUtils.Naive`, `TimeUtils.cs:20`) — matching the PG `TIMESTAMP WITHOUT TIME ZONE` columns. **[verified]** round-tripped to `2026-08-08T19:29:25.7728215`. |
| `DateTimeOffset` | n/a | — | Not used. If ever introduced, it must be `yyyy-MM-dd HH:mm:ss.fffffffzzz`, which is **not** lexicographic-safe across differing offsets — normalize before storing. |
| `decimal` | **TEXT** | invariant round-trip | SQLite has no exact decimal; REAL would lose precision and break equality. TEXT preserves value but sorts lexicographically (wrong for numbers). Only 1 occurrence in the data layer and it is not a stored column — keep it that way. The `::numeric` casts in aggregations (`QueryHelper.cs:613,615,620,625`) become REAL under SQLite, which is a documented precision degradation for `sum`/`avg`. |
| `bool` | INTEGER 0/1 | | `GetBoolean` handles it. Note the `json_type` vocabulary difference (§2.1): JSON booleans read back as `'true'`/`'false'`, not `'boolean'`. |
| `byte[]` (`attachments.media`) | BLOB | | Direct. Media stays on the filesystem per the brief; this column is the small-blob path only. |

**The one thing to get right:** timestamps must be written through a single
formatter, used by both the writer and the DDL default, or lexicographic ordering
silently breaks the moment two code paths disagree on width or separator. This
should be one function in the dialect, not a format string repeated at call sites.

---

## 7. The seam

The smallest thing that works, given what §1 found (no data sources, no composites,
no custom handlers, no ranges) and the `$N` discovery.

**Three pieces. No more.**

### 7.1 Move the data layer to ADO.NET base classes

`DbConnection`, `DbCommand`, `DbDataReader`, `DbParameter`, `DbTransaction`.
This is mechanical and is the bulk of the diff (233 `NpgsqlCommand` sites →
`conn.CreateCommand()`). It changes no SQL text, so it is independently
verifiable against the Phase 2 byte-identical-SQL requirement.

**Explicitly not `DbProviderFactories`** — per the brief, it is reflection-based
and AOT-hostile. Instead:

```csharp
public interface IDbConnectionFactory       // one implementation per driver
{
    Task<DbConnection> OpenAsync(CancellationToken ct = default);
    bool IsConfigured { get; }
}
```

Registered by a `switch` on `DATABASE_DRIVER` in `Program.cs` — a direct `new`,
statically rooted, no reflection.

### 7.2 `ISqlDialect` — only for what genuinely differs

Scoped to what the audit actually found, not to what might differ:

```csharp
public interface ISqlDialect
{
    // §1.1 — replaces the NpgsqlDbType leak in the grammar
    DbParameter CreateParameter(string? name, object? value, SqlValueKind kind);

    // §2.1 — JSON access and type tests
    string JsonPath(string column, IReadOnlyList<string> segments);   // -> / ->>
    string JsonTypeIs(string expr, JsonKind kind);                    // jsonb_typeof vs json_type
    string JsonContains(string expr, string paramPlaceholder);        // @> vs json_each EXISTS
    string JsonArrayAnyOf(string column, string paramPlaceholder);    // ?| vs json_each IN
    string ArrayElements(string column);                              // unnest vs json_each

    // §2.3 — the case-sensitivity split
    string CaseInsensitiveLike(string lhs, string rhs);               // ILIKE vs lower()/lower()
    string CaseSensitiveLike(string lhs, string rhs);                 // LIKE vs GLOB

    // §2.4 / §6
    string NowExpr { get; }                                           // NOW() vs bound parameter
    string FormatTimestamp(DateTime value);                           // single formatter, §6

    // §2.8
    string? Aggregate(string name, string fieldExpr, IReadOnlyList<string> args);
}

public enum SqlValueKind { Text, TextArray, Json, Boolean, Timestamp, Uuid, Bytes, Blob }
```

`SqlValueKind` is the neutral replacement for `NpgsqlDbType` and has exactly the
eight members the codebase actually binds (§1.2) — not the full `NpgsqlDbType`
surface.

### 7.3 What deliberately gets **no** abstraction

Per "no speculative abstraction — if only one driver needs it, don't abstract it":

- **`Db.FastImportSession`** (`Db.cs:94-417`). Its reconnect/replay/bisect design
  is PostgreSQL transport semantics. SQLite gets a *separate, much simpler* bulk
  path (one transaction, prepared insert). Two implementations behind a narrow
  "bulk load these rows" call — not a shared session abstraction.
- **`ImportBulkIndexes`**, **pgvector**, **`DbSizeInfoPlugin`**, `pg_advisory_lock`.
  PostgreSQL-only; gated off under SQLite.
- **`Dmart.SqlAdapter`** — stays PostgreSQL-only (§1.4).
- **`SqlSchema`** — two separate DDL files, not one parameterized one. The
  schemas legitimately differ (§2.2, §2.10); pretending otherwise would produce
  a worse artifact than writing both.

### 7.4 The `$N` finding — why this seam is smaller than expected

**[verified]**: SQLite accepts `$1` as a *named* parameter, and
`Microsoft.Data.Sqlite` binds it when the parameter is named `"$1"`. csdmart
already emits positional `$N` everywhere (`QueryHelper.cs:49,57,62,...`;
`SearchExpressionParser.cs:236`). So the emitted placeholder text needs **no
rewriting at all** — only the binding side changes (Npgsql binds nameless params
by position; SQLite binds by the name `"$N"`). One `CreateParameter` implementation
per driver absorbs the entire difference.

This is the single biggest reason I think this port is tractable at reasonable cost.

---

## 8. AOT verification — **done, not assumed**

I built and ran a Native AOT probe rather than reasoning about it.

Environment: .NET SDK 10.0.110, clang 22.1.8, lld present, `linux-x64`.
Probe settings mirrored `dmart.csproj`: `PublishAot`, `InvariantGlobalization`,
`EnableTrimAnalyzer`, `EnableAotAnalyzer`, `EnableSingleFileAnalyzer`,
`JsonSerializerIsReflectionEnabledByDefault=false`.

**Result: `dotnet publish -r linux-x64 /p:PublishAot=true` produced ZERO
IL2026 / IL2027 / IL3050 / IL2104 / IL3053 warnings**, and the native binary ran:

```
PLAN: SEARCH entries USING INDEX idx_entries_schema_shortname (schema_shortname=?)
json_each match: 1
guid=b9959e55-be44-4948-8851-7e5f4443b03d dt=2026-08-08T19:30:40.8357957
ExecuteScalarAsync completed-synchronously=True
SQLITE-AOT-PROBE-OK
```

The probe exercised `DbConnection`/`DbCommand` base classes, `$1`-named parameter
binding, PRAGMAs, a generated column + expression index (with `EXPLAIN QUERY PLAN`
confirming index use), `json_each`, and Guid/DateTime round-tripping.

### Caveats — both real, both actionable

**1. The native library is a sidecar, not statically linked. [verified]**
`libe_sqlite3.so` (1.3 MB) is emitted *next to* the AOT binary, and removing it
makes the binary fail at startup. csdmart's AOT output stops being a single
self-contained file.

`SQLitePCLRaw.lib.e_sqlite3` ships `.a` static libraries **only for
`browser-wasm`** — **[verified]** by inspecting the package: every other RID
(including `linux-x64`, `linux-arm64`, `linux-musl-x64`) ships `.so` only.
Static linking is therefore possible but requires compiling the SQLite
amalgamation ourselves and wiring `<DirectPInvoke Include="e_sqlite3" />` plus
`<NativeLibrary Include="…/e_sqlite3.a" />`. I have **not** verified that path and
would not commit to it without doing so. If single-file deployment is a
requirement rather than a nicety, tell me and I will verify it in Phase 3 before
building on it.

**2. Security: the default transitive package has a live CVE. [verified]**
`Microsoft.Data.Sqlite` 10.0.5 pulls `SQLitePCLRaw.lib.e_sqlite3` **2.1.11**,
which raises `NU1903: known high severity vulnerability` (GHSA-2m69-gcr7-jv3q).

**[verified] fix**: pinning `SQLitePCLRaw.lib.e_sqlite3` to **2.1.12** clears the
warning and the AOT publish stays clean and functional. Note that
`SQLitePCLRaw.bundle_e_sqlite3` tops out at 3.0.5 while `lib.e_sqlite3` has
2.1.12 and 3.53.3 — pinning the *lib* alone is the minimal correct change; a
3.x bundle upgrade is a larger move I have not tested.

Whatever else is decided, **the SQLite path must pin the lib package**. Shipping
the default transitive version would introduce a known high-severity vulnerability
into a project whose PostgreSQL path has none.

---

## 9. Unsupported / degraded on SQLite

**Unsupported (feature off, not silently different):**

| Feature | Reason |
|---|---|
| Semantic / vector search (`/query` semantic mode, `SemanticSearchService`, `SemanticIndexerService`, `EmbeddingProvider`) | pgvector. Already gated behind `EmbeddingProvider.IsEnabledAsync` — stays false |
| `dmart import --fast` | `session_replication_role` is PostgreSQL-specific (`Db.cs:70-75,364-368`) |
| `dmart import --drop-indexes` | GIN-specific (`ImportBulkIndexes.cs`) |
| `DbSizeInfoPlugin` | `pg_total_relation_size` |
| `Dmart.SqlAdapter` SDK | Stays PostgreSQL-only (separate distributable) |
| `quantile`, `stddev`, `first_value`, `random_sample` reducers | No SQLite equivalent (§2.8). **Implemented**: `ISqlDialect.Reducer` declines them and the caller raises a request error naming the reducer, rather than returning wrong numbers or a response silently missing the column |
| `CREATE INDEX CONCURRENTLY` | Index builds lock |

**Degraded (works, materially slower or subtly different):**

| Behaviour | Degradation |
|---|---|
| JSON containment filters (`@>`, `?|`) | Full scan — no GIN analogue (§4) |
| ACL `query_policies` filter | Scan on **every** authorized query (§4) |
| `relationships @>` delete-time RI probe | Scan on **every** delete (§4) |
| Wildcard `*foo*` search | FTS5 `trigram` instead of `pg_trgm` (§5) |
| `ILIKE` on non-ASCII | ASCII-only case folding (§2.3) |
| `sum`/`avg` precision | REAL instead of `numeric` (§6) |
| Concurrent writes | Serialized — single writer (§10) |
| Request concurrency | Bounded by thread pool, not connections (§3) |
| Bulk import throughput | Prepared INSERT instead of binary COPY (§2.9) |
| AOT deployment | Sidecar `.so` (§8) |

---

## 10. Concurrency plan

### PRAGMAs and where they apply

PRAGMAs are **connection-scoped**, and csdmart opens a fresh connection per call
(`Db.OpenAsync`, `Db.cs:27-34`), so there is no long-lived connection to
configure once. Two of these are also *database*-scoped and persist in the file
header — the distinction matters:

| PRAGMA | Value | Scope | Where applied |
|---|---|---|---|
| `journal_mode` | `WAL` | **Database** — persists in the file | Once at init; re-asserting per connection is a cheap no-op |
| `synchronous` | `NORMAL` | Connection | Every connection |
| `busy_timeout` | 5000 ms | Connection | Every connection |
| `foreign_keys` | `ON` | Connection | Every connection — **must**, or the deferred FKs (§2.10) silently do nothing |
| `mmap_size` | 256 MB | Connection | Every connection; ignored if not compiled in |
| `cache_size` | negative (KB) | Connection | Every connection |

`Microsoft.Data.Sqlite` pools connections by connection string, and **a pooled
connection retains connection-scoped PRAGMAs**, so applying them on every
`OpenAsync` is correct but redundant on a pool hit. The right place is a single
`OpenAsync` implementation in the SQLite `IDbConnectionFactory` (§7.1) that issues
them after open — not scattered at call sites, and not assumed-once-at-startup.

`Pooling=False` would make PRAGMA application unambiguous at the cost of reopening
the file per request. I would keep pooling on and apply PRAGMAs on open.

### Single writer under concurrent ASP.NET requests

WAL gives concurrent readers + one writer. Writers serialize at the database
level, so under concurrent write load requests queue on the write lock. Combined
with §3 (each queued write occupies a thread), this is the tier's real ceiling.

Specific interactions with existing code:

- The two `SELECT … FOR UPDATE` read-modify-write paths
  (`EntryRepository.cs:266`, `UserRepository.cs:313`) **must** use
  `BEGIN IMMEDIATE`. With SQLite's default deferred transactions, the read
  succeeds under a shared lock and the upgrade to write can fail with
  `SQLITE_BUSY` even when `busy_timeout` is set — because the engine cannot
  safely wait on a lock upgrade without risking deadlock, so it returns
  `SQLITE_BUSY` immediately rather than honouring the timeout. This is the
  classic SQLite footgun and it will otherwise show up as flaky test failures
  under parallel test execution, not as a clean error.
- `Db.ExecuteWithRetryOnDeadlockAsync` (`Db.cs:426-443`) is the natural place for
  the SQLite analogue — same shape, different error codes.

### Retry / backoff for `SQLITE_BUSY`

`busy_timeout` handles *contention within a statement* by sleeping and retrying
internally, but it does **not** cover `SQLITE_BUSY_SNAPSHOT` (a deferred
transaction's lock upgrade) or `SQLITE_BUSY` returned at `COMMIT`. So both layers
are needed:

1. `PRAGMA busy_timeout = 5000` on every connection — absorbs ordinary contention.
2. An application-level retry mirroring the existing deadlock retry: catch
   `SqliteException` with `SqliteErrorCode` 5 (`SQLITE_BUSY`) or 6
   (`SQLITE_LOCKED`), **including the `_SNAPSHOT` extended codes**; retry the
   whole transaction (never a partial one — the transaction is dead); 3 attempts;
   exponential backoff with jitter starting at ~50 ms. Jitter matters here in a
   way it does not for PG deadlocks: SQLite contention is a thundering herd on one
   file lock, and unjittered backoff resynchronizes the herd.

`Db.ExecuteWithRetryOnDeadlockAsync` uses linear 50/100 ms backoff without jitter
(`Db.cs:440`) — correct for PostgreSQL deadlocks, wrong for SQLite. Two
implementations, not one shared one.

---

## 11. Decisions I need before Phase 2

Per the working rule "when you hit something with no SQLite equivalent: STOP and
ask", these are the items I will not resolve unilaterally.

1. **The `LIKE` case-sensitivity split (§2.3) — blocking, security-relevant.**
   PostgreSQL `LIKE` is case-sensitive; SQLite `LIKE` is ASCII-case-insensitive.
   The row-level ACL (`QueryHelper.cs:235`) depends on the former, and **[verified]**
   `'management:/USERS:*' LIKE 'management:/users:%'` is true in SQLite and false
   in PostgreSQL — the SQLite path would grant access PostgreSQL denies.
   `PRAGMA case_sensitive_like=ON` fixes the ACL and simultaneously breaks all 20+
   `ILIKE` sites, since both compile to `LIKE`.
   My recommendation: **`GLOB` for the ACL** (**[verified]** case-sensitive, and
   its `*` wildcard matches dmart's native policy syntax, so the
   `*`→`%` translation at `QueryHelper.cs:232` disappears), and explicit
   `lower(x) LIKE lower(y)` for the `ILIKE` sites. Confirm before I build on it.

2. **`quantile` and `stddev` (§2.8) — no SQLite equivalent.**
   Error with a clear "unsupported on the SQLite driver" message (my
   recommendation), or register custom aggregates via `CreateFunction`
   (per-connection, interacts with pooling)?

3. **`ILIKE` on non-ASCII (§2.3).** Accept ASCII-only folding as a documented tier
   limit, or pay for `lower()` on both sides everywhere? Arabic is unaffected
   either way; this only changes accented-Latin results.

4. **Single-file AOT deployment (§8).** Is the sidecar `libe_sqlite3.so` acceptable,
   or is a single self-contained binary a requirement? If the latter, I need to
   verify the self-built-`.a` + `DirectPInvoke` path before Phase 3 depends on it.

5. **`sum`/`avg` precision (§6).** `numeric` → REAL is a real precision change on
   aggregation results. Acceptable for the tier, or should these error like
   `quantile`?

One item needs no decision but should be actioned regardless of the rest:
**pin `SQLitePCLRaw.lib.e_sqlite3` to 2.1.12** (§8) — the default transitive
2.1.11 carries a known high-severity CVE.

---

## Appendix — sizing

| Area | Files | Nature |
|---|---|---|
| `DataAdapters/Sql/*` → ADO.NET base classes | 18 | Mechanical; no SQL text changes |
| `Dmart.QueryGrammar` param type (§1.1) | 1 (+public API) | Small but breaks a published signature |
| `SearchExpressionParser` JSON emit (§2.1) | 1 | Largest genuine rewrite |
| `QueryHelper` JSON/array/ACL emit | 1 | Includes the §11.1 decision |
| SQLite DDL + migration (§2.10) | new | PL/pgSQL logic moves to C# |
| SQLite bulk load / reindex (§2.9) | new | Not a translation |
| Driver selection + PRAGMAs (§7.1, §10) | ~3 | New |
| PG-only gating (§9) | ~8 | Feature flags |

Phase 2 (the seam, PostgreSQL-only, byte-identical SQL) is the first two rows plus
the factory — independently verifiable and safe to land on its own.
