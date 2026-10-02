`dmart` is a single self-contained Native-AOT binary that is both the server and the CLI client. The same executable starts the ASP.NET Core / Kestrel HTTP server and exposes subcommands for managing data, applying schema migrations, seeding sample spaces, running health checks, and more.

## Usage

```
dmart [subcommand] [options]
```

With no subcommand, `dmart` prints its help. Configuration is resolved once at startup from `$BACKEND_ENV → ./config.env → ~/.dmart/config.env`, then overlaid with environment variables.

## Available Commands

### 1. serve

Starts the DMART HTTP server (ASP.NET Core Minimal APIs on Kestrel). This is the default action when a non-flag argument is passed.

**Options:**

- `--cxb-config <path>`: Path to the CXB admin UI `config.json` file.

```
dmart serve --cxb-config my_cxb_config.json
```

### 2. migrate

Creates or updates the PostgreSQL schema without starting the server. Idempotent — safe to run repeatedly.

```
dmart migrate
```

### 3. seed

Seeds the bundled sample spaces and/or populates the database. The sample spaces are embedded in the binary, so seeding works standalone.

**Modes:**

- no argument: copy bundled spaces to the spaces folder _and_ import them into the database.

- `files-only`: copy bundled spaces to the spaces folder (fallback `~/.dmart/spaces`).

- `db-only`: import the spaces folder into the database.

- `--force`: overwrite existing files / upsert existing rows (default: skip both).

```
dmart seed
dmart seed files-only
dmart seed db-only --force
```

### 4. import

Loads a zip or folder export into the database. By default, existing rows are skipped (idempotent).

**Options:**

- `-r` / `--replace`: overwrite existing rows.

- `--fast`, `--fast-parallelism=N`, `--batch-size=N`: tune throughput.

- `--resume`: resume a crashed filesystem import from a sidecar checkpoint (`<source>/.dmart-import-checkpoint.json`).

```
dmart import school.zip
dmart import ./spaces --fast --replace
```

### 5. export

Exports a space to a zip archive in the DMART on-disk layout (`spaces/` + `.dm/meta.*.json`). This layout is a transfer/backup format — PostgreSQL remains the source of truth.

**Output resolution:**

- `--output` unset → `./<space>.zip`

- `--output .` → `./<space>.zip`

- `--output some/dir/` → `some/dir/<space>.zip`

- `--output snap.zip` → `snap.zip`

```
dmart export school
dmart export school --output .
dmart export school --output snapshots/school.zip
```

### 6. preflight

Scans a legacy filesystem export for integrity issues (duplicate UUIDs, missing owners, schema-noncompliant payloads) and auto-fixes them before `dmart import`.

```
dmart preflight ./spaces
dmart preflight --dry-run --workers 4 ./spaces
```

### 7. settings

Prints the effective settings as JSON (secrets redacted). Shares its projection with `GET /info/settings` so CLI and API output stay in sync.

```
dmart settings
```

### 8. passwd

Sets the password for a user (Argon2-hashed). The shortname may be passed positionally; passwords are read from a prompt, never the command line.

```
dmart passwd
dmart passwd dmart
```

### 9. check

Runs health checks on a space.

```
dmart check
dmart check school hard
```

### 10. selfcheck

Smoke-tests the running HTTP surface (login + CRUD + query) against a live server.

```
dmart selfcheck --url http://localhost:8282 --admin dmart --password-stdin
```

### 11. fix-folder-rendering

Repairs legacy folder payload bodies to match the canonical `folder_rendering` schema (strips unknown fields, adds required-but-missing ones, widens policy arrays). Content is never touched. Dry-run by default; pass `--apply` to write.

```
dmart fix-folder-rendering school
dmart fix-folder-rendering school --apply
```

### 12. update_query_policies

Recomputes `query_policies` for every entry and updates rows whose stored value drifted (e.g. owner / is_active changed outside the write path).

```
dmart update_query_policies
dmart update_query_policies --batch-size 500
```

### 13. fix_query_policies

Backfills `entries.query_policies` for rows written before write-time population landed. Idempotent.

```
dmart fix_query_policies
dmart fix_query_policies school --dry-run
```

### 14. create-users-folders

Backfills each user's personal folders (`notifications`, `private`, `protected`, `public`, `inbox`). Idempotent — existing folders are left untouched.

```
dmart create-users-folders
```

### 15. init

Initializes `~/.dmart` with config files, generating a fresh random `JWT_SECRET`.

```
dmart init
```

### 16. cli

Interactive CLI client for talking to a running DMART server. Supports a REPL, a single command, or a script.

```
dmart cli
dmart cli c myspace get /myspace/folder
dmart cli s ./script.txt
```

### 17. version

Prints version and build info as JSON (version, branch, build date, and .NET runtime).

```
dmart version
```

### 18. help

Prints the list of available subcommands.

```
dmart help
```
