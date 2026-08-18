# DMART — Unified Data Platform (C# Port)

A fast, AOT-native headless information-management backend on .NET 10,
PostgreSQL, and Svelte. Ships as a single ~37 MB self-contained binary.

## The problem DMART solves

Valuable information — organizational and personal — tends to sprawl:

- Dispersed across too many systems, each with its own access context.
- Hard to consolidate, link, and reason about across silos.
- Locked to vendors or application-specific formats.
- Chaotic to discover and search as data piles up.
- Hard to master, dedup, back up, archive, and restore.
- Hard to protect, audit, and secure consistently.

DMART is a structure-oriented information-management layer (aka
Data-as-a-Service) that lets you treat information as a first-class asset:
authored cleanly, searched coherently, shared safely, and extended without
vendor lock-in. It targets small-to-medium footprints (up to ~300 million
primary entries) and is deliberately not aimed at workloads that need heavy
relational modeling or large multi-statement transactions.

## What is DMART?

A headless, low-code information inventory platform that assimilates
structured, unstructured, and binary data under a single REST-like JSON
API. Top highlights:

- **Data-as-a-Service backbone** — data assets are declared in logical
  business shapes and reused across applications and microservices
  without each one redefining its own schema.
- **Standardized JSON API** — a unified public API for every resource
  type; full OpenAPI 3 spec at `/docs`.
- **Entry-oriented data model** — a coherent information unit (meta +
  payload + attachments + relationships) lives as one logical entry,
  organized in hierarchical folders within spaces.
- **Schema validation** — JSON Schema enforcement on content payloads,
  referenced from a central `schema` subpath inside each space.
- **Built-in access control** — role-based permissions with per-entry
  ACLs, hierarchical subpath walks, precomputed `query_policies`
  filtering at the SQL level, and magic-word scope widening
  (`__all_spaces__`, `__all_subpaths__`).
- **Workflow engine** — configurable ticket state machines with lock,
  assign, and progress-transition endpoints.
- **Plugin system** — built-in hooks + external native `.so` plugins
  loaded at runtime + MCP tool surface for AI agents.
- **WebSocket** — real-time notifications via channel subscriptions.
- **Microservice-friendly** — JWT shared secret lets other services
  accept a dmart session out of the box.
- **Admin UI** — CXB and Catalog Svelte SPAs embedded in the binary,
  served at `/cxb/` and `/cat/` with runtime-rewritten config so the
  same bundle works behind any reverse proxy.

New to DMART? Read [`GLOSSARY.md`](./GLOSSARY.md) for the project's
vocabulary. Contributing code? Read [`ARCHITECTURE.md`](./ARCHITECTURE.md)
first — it explains the constraints and the reasoning behind the unusual
choices.

## Quick Start

### Container (fastest — no build needed)

```
podman run --name dmart -p 8000:8000 -d -it ghcr.io/edraj/csdmart:latest
podman exec -it dmart dmart passwd
# Open http://localhost:8000/cxb/ or http://localhost:8000/cat/
```

### RPM (Fedora / RHEL 9)

```
sudo dnf install ./dmart-*.rpm
sudo vi /etc/dmart/config.env          # set DATABASE_PASSWORD, JWT_SECRET
dmart passwd                           # set admin password
sudo systemctl enable --now dmart
```

Download the RPM from the [latest release](https://github.com/edraj/csdmart/releases).

### From source

```
git clone https://github.com/edraj/csdmart
cd csdmart
cp config.env.sample config.env
vi config.env                          # set database credentials
dotnet run -- serve
# In another terminal:
dmart passwd
```

## CLI

```
dmart                      Show help
dmart serve                Start HTTP server
dmart version              Version and build info
dmart settings             Show effective configuration
dmart passwd               Set user password interactively
dmart init                 Initialize ~/.dmart/ with config files
dmart migrate              Create/update Postgres schema (idempotent)
dmart check <space>        Run health checks
dmart export <space>       Export space to zip
dmart import <file.zip>    Import from zip
dmart fix_query_policies   Backfill empty query_policies columns
dmart update_query_policies
                           Recompute query_policies for every entry and
                           write back rows whose stored value drifted
                           (Python parity for update_query_policies.py)
dmart cli                  Interactive REPL client
dmart cli c <space> "ls"   Single CLI command
dmart cli s script.txt     Batch script
```

Run `dmart <subcommand> --help` for per-command details where supported.

## Configuration

Configuration sources, in priority order (later wins):

1. `config.env` — checked at `$BACKEND_ENV`, `./config.env`, `~/.dmart/config.env`
2. Environment variables prefixed `Dmart__` (double underscore = nested)

Unknown keys in `config.env` cause startup to fail — this catches typos
like `DATABAE_HOST` vs `DATABASE_HOST`. See `config.env.sample` for the
complete list of valid keys.

Key settings:

```
DATABASE_HOST="localhost"
DATABASE_PORT=5432
DATABASE_USERNAME="dmart"
DATABASE_PASSWORD="yourpassword"
DATABASE_NAME="dmart"
JWT_SECRET="at-least-32-bytes-long"
LISTENING_HOST="0.0.0.0"
LISTENING_PORT=5099
ALLOWED_CORS_ORIGINS="http://localhost:3000"
CXB_URL="/cxb"
LOG_LEVEL="information"
LOG_FORMAT="json"
```

The admin user `dmart` is created passwordless on first startup. Set a
password with `dmart passwd` before exposing the server.

## Storage backends

The flat files under `SPACES_FOLDER` are the source of truth. The SQL store
is a **rebuildable index** over them — `dmart import` reconstructs it from
disk on either backend. Which backend holds that index is one setting:

```
DATABASE_DRIVER="postgresql"     # + DATABASE_HOST / DATABASE_NAME / ...
DATABASE_DRIVER="sqlite"         # + SQLITE_PATH="/var/lib/dmart/dmart.db"
```

Packaged installs (RPM/deb/apk) ship a `/etc/dmart/config.env` that selects
SQLite at `/var/lib/dmart/dmart.db`, so `dnf install dmart` needs only a
`JWT_SECRET` before the service starts — no database server to stand up. The
file documents how to switch to PostgreSQL, and it is seeded on **first install
only**, so an upgrade never touches a config you have edited.

Leaving it **unset is supported**, and is inferred rather than defaulted: any
PostgreSQL connection setting selects `postgresql`, nothing pointing at
PostgreSQL selects `sqlite`. So `dmart serve` works on a fresh box with no
database configuration at all, while a `config.env` written before this key
existed keeps the backend it always had — an upgrade never silently moves a
deployment onto an empty store. The startup log always states which driver was
chosen and whether it was inferred:

```
database driver: sqlite (inferred — no PostgreSQL connection configured)
sqlite database at /var/lib/dmart/dmart.db — set DATABASE_DRIVER explicitly to pin this choice
```

**PostgreSQL is the supported production tier.** SQLite is a *reduced tier*
for development, CI, single-node and edge deployments — deliberately not at
parity. It serves the same REST API and runs the same test suite — 1,827 of
1,842 tests, with each of the 15 skips carrying a one-line reason at its call
site — and CI exercises both drivers on every push. It is real; it is just
smaller.

### What you give up on SQLite

Unavailable — the feature is off, not silently different:

| Feature | Why | How it fails |
|---|---|---|
| Semantic / vector search | needs pgvector | cleanly disabled — the capability probe returns false |
| `dmart import --fast`, `--drop-indexes`, `--fast-parallelism` | `session_replication_role`, and GIN-specific index juggling | refused with a reason naming the flag, never ignored |
| `Dmart.SqlAdapter` SDK | stays PostgreSQL-only | separate distributable |
| Aggregation reducers `stddev`, `quantile`, `first_value`, `random_sample` | no core-SQLite equivalent (no stddev, no `percentile_cont`, no ordered array aggregation) | HTTP 400 naming the reducer |
| `db_size_info` per-table breakdown | needs the `dbstat` virtual table (`SQLITE_ENABLE_DBSTAT_VTAB`), which the bundled SQLite is not built with | reports the whole-database size and says why the breakdown is missing |

Every other reducer works: `count`, `count_distinct`, `sum`, `avg`, `min`,
`max`, `group_concat`. A reducer name dmart does not recognize is still
ignored on both backends, which is long-standing behaviour — only a reducer
the backend genuinely cannot compute is refused, and it is refused loudly
rather than dropped from the response. Nothing in this table fails with an
opaque database error any more.

Degraded — works, but materially slower or subtly different:

| Behaviour | On SQLite |
|---|---|
| JSON payload filters (`@payload.body.x:v`) | **unindexed scan** — no GIN analogue. The main limit; see below |
| Row-level ACL filter | scans on every authorized query |
| Wildcard `*foo*` search | FTS5 `trigram` index instead of `pg_trgm` — indexed, different tokenizer |
| `ILIKE` on non-ASCII | ASCII-only case folding, so accented Latin does not fold (Arabic is unaffected — no case) |
| Numeric sort of a JSON *string* (`"42"`) | sorts lexically; PostgreSQL sorts it numerically |
| `sum` / `avg` precision | `CAST(x AS REAL)` — no exact decimal type, so money-like sums accumulate float error where PostgreSQL's `numeric` would not |
| Concurrent writes | serialized — one writer at a time |
| AOT deployment | ships a `libe_sqlite3.so` sidecar next to the binary |

### Measured, not estimated

From [`bench/REPORT-sqlite-vs-postgresql.md`](./bench/REPORT-sqlite-vs-postgresql.md)
— 20,000 entries, same host, end-to-end through the HTTP API:

| | SQLite | PostgreSQL |
|---|---:|---:|
| warm read p50 | **0.63 ms** | 1.13 ms |
| filtered search p50 | 67 ms | **10.7 ms** |
| index rebuild | 3,913 rows/s | **13,950 rows/s** |
| concurrent write p50 / max | **2.6 ms** / 555 ms | 8.8 ms / **51 ms** |

Reads are faster on SQLite and rebuilds are comfortably fast enough. Two
results decide whether it fits:

- **Filtered search is a structural gap, not a constant factor.** PostgreSQL
  answers a payload filter from a GIN index; SQLite has no equivalent and
  walks the JSON per row, so the cost grows with corpus size where
  PostgreSQL's does not. 67 ms over 20k entries is fine — the same query over
  500k will not be. **This is the number to check against your corpus before
  choosing SQLite.**
- **Concurrent writes trade tail latency, not correctness.** SQLite's median
  write is *faster* than PostgreSQL's; its worst is ~10× slower, because a
  writer that misses the lock queues behind every other one. Nothing failed
  in 800 concurrent writes — `busy_timeout` and a bounded retry turn
  contention into latency rather than errors.

Rule of thumb, straight off those numbers: use PostgreSQL if a
payload-filtered query over your whole corpus has to stay under ~50 ms, or if
sustained concurrent writing means p99 latency has to stay bounded. Otherwise
SQLite will not be the thing that limits you.

Full analysis in [`docs/sqlite-backend-audit.md`](./docs/sqlite-backend-audit.md).

## API Endpoints

| Group     | Path                                                       | Auth  | Description                                |
|-----------|------------------------------------------------------------|-------|--------------------------------------------|
| Root      | `GET /`                                                    | No    | Server identifier                          |
| Docs      | `GET /docs`                                                | No    | Swagger UI                                 |
| Docs      | `GET /docs/openapi.json`                                   | No    | OpenAPI spec                               |
| Auth      | `POST /user/login`                                         | No    | Login (returns JWT + cookie)               |
| Auth      | `POST /user/logout`                                        | Yes   | Logout                                     |
| Auth      | `GET /user/profile`                                        | Yes   | User profile                               |
| Managed   | `POST /managed/request`                                    | Yes   | CRUD (create/update/delete/move)           |
| Managed   | `POST /managed/query`                                      | Yes   | Query entries                              |
| Managed   | `GET /managed/entry/{type}/{space}/{subpath}/{shortname}`  | Yes   | Get single entry                           |
| Managed   | `POST /managed/resource_with_payload`                      | Yes   | Upload with file                           |
| Managed   | `POST /managed/csv`                                        | Yes   | CSV export                                 |
| Managed   | `POST /managed/resources_from_csv/{type}/{space}/{subpath}/{schema}` | Yes | CSV import                          |
| Managed   | `PUT /managed/progress-ticket/{space}/{subpath}/{shortname}/{action}` | Yes | Workflow state transition           |
| Public    | `POST /public/query`                                       | No    | Public query                               |
| Public    | `POST /public/submit/{space}/{schema}/{subpath}`           | No    | Public submission                          |
| Info      | `GET /info/me`                                             | Yes   | Caller's own shortname                     |
| Info      | `GET /info/manifest`                                       | Admin | Server manifest and plugins (super_admin)  |
| Info      | `GET /info/settings`                                       | Admin | Effective settings (super_admin)           |
| WebSocket | `GET /ws?token=<jwt>`                                      | Token | Real-time channel subscriptions            |

See [`ARCHITECTURE.md`](./ARCHITECTURE.md#request-lifecycle) for how
requests flow through the system.

## Client libraries

Official SDKs for talking to the dmart REST API:

| Language / runtime | Package | Install |
|---|---|---|
| Python | [`pydmart`](https://pypi.org/project/pydmart/) | `pip install pydmart` |
| Python | [`dmart`](https://pypi.org/project/dmart/) (core + CLI) | `pip install dmart` |
| TypeScript / JavaScript (Node, Deno, Bun, browsers) | [`@edraj/tsdmart`](https://www.npmjs.com/package/@edraj/tsdmart) | `npm install @edraj/tsdmart` |
| Dart / Flutter | [`dmart`](https://pub.dev/packages/dmart) | `flutter pub add dmart` |
| C# / .NET | [`Dmart.Client`](./dmart.Client/) (ships from this repo) | `dotnet add package Dmart.Client` |

MCP-capable AI agents (Zed, Claude Code, Cursor, …) can connect directly
to `/mcp` on any dmart instance — no SDK needed. See
[`docs/plugins-and-mcp.md`](./docs/plugins-and-mcp.md).

## Plugins

### Built-in plugins

Compiled into the binary. Configured via `plugins/<name>/config.json`:

- `resource_folders_creation` — auto-creates `/schema` folder on space creation
- `realtime_updates_notifier` — WebSocket broadcasts on CRUD events
- `audit` — logs all dispatched events
- `db_size_info` — API plugin at `GET /db_size_info/`

### External native plugins

Drop a `.so` + `config.json` into `~/.dmart/plugins/<name>/` — no
recompile needed:

```
mkdir -p ~/.dmart/plugins/my_plugin
cp my_plugin.so ~/.dmart/plugins/my_plugin/
cat > ~/.dmart/plugins/my_plugin/config.json << 'EOF'
{
  "shortname": "my_plugin",
  "is_active": true,
  "type": "hook",
  "listen_time": "after",
  "filters": {
    "subpaths": { "__all_spaces__": ["__all_subpaths__"] },
    "resource_types": ["content"],
    "schema_shortnames": [],
    "actions": ["create", "update", "delete"]
  }
}
EOF
```

### Building custom plugins

```
cd custom_plugins_sdk/<name>
dotnet publish <name>.csproj -c Release -r linux-x64 -o /tmp/<name>-build
cp /tmp/<name>-build/<name>.so ~/.dmart/plugins/<name>/
```

Plugins export a C-ABI surface: `get_info()`, `hook()` or
`handle_request()`, `free_string()`. Can be written in any language that
produces a C ABI shared library (C#, Rust, C, Go). See
[`custom_plugins_sdk/README.md`](./custom_plugins_sdk/README.md) for the
full development guide with working examples.

## Building

```
# Development
dotnet run -- serve
./build.sh                   # fast JIT build (~5-50s), bin/dmart -> apphost

# Production (AOT native binary)
./build.sh --aot
# Output: bin/dmart (~40MB self-contained native binary)

# RPM packages (always AOT)
./dist/build-rpm.sh          # Fedora
./dist/build-rpm.sh el9      # RHEL 9 via podman
./dist/build-rpm.sh srpm     # Source RPM
```

## Testing

```
# Unit and integration tests (PostgreSQL)
dotnet test dmart.Tests/dmart.Tests.csproj -c Release

# The same suite against SQLite — needs no database, writes one temp file
DMART_TEST_DRIVER=sqlite dotnet test dmart.Tests/dmart.Tests.csproj -c Release

# E2E smoke tests against a running server
DMART_URL=http://localhost:5099 ./curl.sh

# Backend comparison benchmark (see bench/REPORT-sqlite-vs-postgresql.md)
./bench/sqlite-vs-postgresql.py --rows 20000
```

CI runs both drivers on every push, plus a native-AOT publish that smoke-tests
the linked binary on SQLite.

See [`docs/testing.md`](./docs/testing.md) for fixtures, parallelism
rules, and the commands that include DB-backed integration tests.

## Documentation

Engineering reference for maintainers and contributors, with Mermaid
diagrams:

- [`GLOSSARY.md`](./GLOSSARY.md) — DMART-specific vocabulary (Entry, Space, CXB, MCP, …)
- [`ARCHITECTURE.md`](./ARCHITECTURE.md) — constraints, request lifecycle, directory guide
- [`docs/README.md`](./docs/README.md) — navigation for the rest of the docs tree
- [`docs/data-model.md`](./docs/data-model.md) — ER diagram, wire-format rules, repositories
- [`docs/permissions.md`](./docs/permissions.md) — the permission walk, anonymous + world, ACL, conditions
- [`docs/auth.md`](./docs/auth.md) — login / JWT / session flows, OAuth providers
- [`docs/plugins-and-mcp.md`](./docs/plugins-and-mcp.md) — plugin lifecycle + MCP protocol + OAuth discovery
- [`docs/query.md`](./docs/query.md) — query types, search syntax, sort_by, ACL filtering
- [`docs/testing.md`](./docs/testing.md) — xUnit + curl.sh + parallelism + common recipes
- [`docs/debugging.md`](./docs/debugging.md) — known pitfalls, AOT gotchas, SQL inspection
- [`docs/contributing.md`](./docs/contributing.md) — recipes: add endpoint, repository, service, plugin
- [`docs/sqlite-backend-audit.md`](./docs/sqlite-backend-audit.md) — the SQLite tier: dialect seam, what is unsupported and why
- [`docs/container.md`](./docs/container.md) — the container image: what is in it, upgrading from the PostgreSQL-era image, build pitfalls

## Deployment

### Systemd (RPM)

```
sudo dnf install ./dmart-*.rpm
sudo vi /etc/dmart/config.env
sudo systemctl enable --now dmart
journalctl -u dmart -f
```

### Docker (all-in-one)

```
./admin_scripts/docker/notes.sh
# Single process, SQLite-backed — no database server to stand up
# Access: http://localhost:8000/cxb/
```

The image runs dmart alone and stores its index in SQLite under `/root/.dmart`.
Images before this change bundled PostgreSQL 18; a container reusing one of
those volumes refuses to start and prints how to migrate rather than coming up
with an empty index. Full details — external PostgreSQL, first-run behaviour,
build pitfalls — in [`docs/container.md`](./docs/container.md).

## Project Layout

See [`ARCHITECTURE.md`](./ARCHITECTURE.md#directory-guide) for a complete
directory walkthrough. Briefly:

```
Api/              HTTP handlers (Minimal API)
Auth/             JWT, Argon2, OTP, OAuth
Cli/              Interactive CLI client
Config/           Settings and config.env parsing
DataAdapters/     Postgres repositories and schema
Middleware/       CORS, CXB, logging, headers
Models/           Domain types and API DTOs, plus DmartJsonContext
Plugins/          Built-in and native plugin loader
Services/         Business logic
```

## Technology

- **.NET 10** with Native AOT — single binary, no runtime needed
- **PostgreSQL** — the supported production tier; DDL in `DataAdapters/Sql/SqlSchema.cs`
- **SQLite** — reduced tier for dev / CI / edge; DDL in `DataAdapters/Sql/SqliteSchema.cs`
- **Npgsql** and **Microsoft.Data.Sqlite** — direct SQL, no ORM, one dialect seam
- **Svelte** — CXB and Catalog admin UIs, embedded in the binary
- **System.Text.Json** with source-generated serializers

## License

AGPL-3.0
