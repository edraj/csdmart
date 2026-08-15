# Backup formats: json+zip vs Parquet vs PostgreSQL native

Reproduce with `bench/backup-formats.sh <publish-dir> <config.env> [runs]`.

## Setup

| | |
|---|---|
| Binary | Native AOT (`dotnet publish -p:PublishAot=true`) |
| Database | PostgreSQL 18, local, 87 MB |
| Space measured | `personal` — 21,843 entries, 40,000 history rows |
| Whole database | 22,096 entries, 47,156 histories, 1,900 users, 3 spaces |
| Method | best of 3; every restore into a **freshly created** database |

## Results

| Method | Scope | Export | Import | Archive |
|---|---|---:|---:|---:|
| zip / JSON | 1 space | 4010 ms | 3861 ms | 19 MB |
| **Parquet** | 1 space | **349 ms** | 1639 ms | **2.0 MB** |
| **Parquet `--all`** | all spaces + users/roles/perms | 442 ms | 2246 ms | **2.7 MB** |
| pg_dump `-Fc` (binary + zlib) | whole DB | 139 ms | 1076 ms | 3.6 MB |
| pg_dump `-Fp` (SQL text) | whole DB | 109 ms | 1072 ms | 33 MB |
| **`COPY … BINARY`** | dmart tables | **97 ms** | **680 ms** | 28 MB |

Row counts were verified equal after every restore — 22,096 entries /
47,156 histories / 1,900 users for the whole-database methods, 21,843 entries /
40,000 histories for the single space. Without that check these would be
timings of unequal work.

## Compressing the PostgreSQL formats — and a claim of mine that did not survive

The table above compares Parquet (zstd level 3, per column) against an
UNCOMPRESSED `COPY` and a zlib `pg_dump`. That is not a fair fight. Applying the
same codec to the PostgreSQL formats, whole database:

| Method | Time | Size |
|---|---:|---:|
| `COPY … BINARY` raw | 94 ms | 28 MB |
| `COPY … BINARY` \| `gzip -6` | 145 ms | 2.7 MB |
| **`COPY … BINARY` \| `zstd -3`** | **109 ms** | **2.2 MB** |
| `pg_dump -Fp` raw | 107 ms | 33 MB |
| `pg_dump -Fp` \| `gzip -6` | 184 ms | 3.7 MB |
| `pg_dump -Fp` \| `zstd -3` | 122 ms | 3.4 MB |
| `pg_dump -Fc` (zlib, built in) | 139 ms | 3.6 MB |
| Parquet `--all` (zstd-3, columnar) | 741 ms | 2.7 MB |

**Compression is nearly free with zstd**: +15 ms on `COPY BINARY` for a 12.7×
reduction, +15 ms on SQL text for 9.7×. gzip costs 3–5× more time for a worse
ratio.

**`COPY BINARY | zstd -3` is both smaller AND ~7× faster than Parquet here.**
So "Parquet is the smallest by a wide margin" — which an earlier version of this
report claimed — is false once the alternatives are compressed with the same
codec. It was only true against uncompressed and zlib baselines.

### Was that an artefact of the fixture?

Partly, and it was worth checking: every one of the 20,000 generated rows
contains the SAME lorem ipsum block, which flatters whole-stream compression —
a single zstd window can dedupe across rows in a way per-column chunks cannot.

Re-run on 20,000 entries whose payloads are random md5 text, so nothing dedupes:

| Method | Time | Size |
|---|---:|---:|
| Parquet (1 space) | 428 ms | **2.0 MB** |
| `COPY … BINARY` \| `zstd -3` | **43 ms** | 2.1 MB |

Essentially **tied on size**, still an order of magnitude apart on time.

**Conclusion: Parquet has no meaningful size advantage over zstd-compressed
row-oriented output at this scale, and is far slower to produce.** Its case
rests on what it can do, not on how small or fast it is:

- restores to **SQLite**, which no PostgreSQL-native format can
- readable by **DuckDB/Spark** without dmart or a database
- **column projection** — read `updated_at` for every row without decompressing
  `payload`, which a single compressed stream fundamentally cannot offer
- **per-space and per-folder** scope
- **incremental** with tombstones
- **verified** on write and on restore

If none of those matter to you, `COPY BINARY | zstd` or `pg_dump -Fc` is the
faster and equally small choice for same-version PostgreSQL recovery.

## Where the export time goes — and what fixing half of it bought

The database emits the same rows far faster than dmart can export them:

| | Time | Output |
|---|---:|---:|
| `COPY (SELECT * FROM entries WHERE space_name='personal') TO STDOUT` | **52 ms** | 19 MB |
| dmart Parquet export, hydrated (original) | 663 ms | 2.0 MB |
| dmart Parquet export, **raw JSON passthrough** | **349 ms** | 2.0 MB |

The original export parsed every `jsonb` column into a C# object
(`Payload`, `Translation`, `AclEntry`…) and immediately serialised it back to a
JSON string — for a format that stores those columns opaquely (§2.2). Pure
loss. The export now reads them as the raw strings the driver already returns
and writes them straight through.

**1.9× faster, byte-identical archive.**

Worth recording precisely because the prediction was wrong: this report
previously estimated ~610 ms of the 663 ms was that round trip. Removing it
saved **314 ms**, so it was roughly HALF the overhead. The remaining ~300 ms
above the `COPY` floor is paging round trips, ADO.NET reads, and the Parquet
encoding itself.

The raw path is used only when there is **no actor**. An ACL-filtered export
still goes through the Query pipeline, because that is where the policy
predicate lives, and skipping it to go faster would hand a caller rows it
cannot see. A test asserts both paths restore to identical objects — compared
by what restores rather than byte-for-byte, since PostgreSQL normalises `jsonb`
key order on write and the raw text legitimately differs.

## The comparison the timings do not make

`COPY BINARY` and `pg_dump` win on speed and lose on everything that makes a
backup useful beyond restoring the same PostgreSQL major version to itself:

|  | zip | Parquet | pg_dump | COPY BINARY |
|---|---|---|---|---|
| Restores to PostgreSQL | yes | yes | yes | yes |
| Restores to **SQLite** | yes | **yes** | no | no |
| Readable without dmart | partly (JSON) | **yes** (DuckDB/Spark) | no | no |
| Survives a schema change | yes | yes | partly | **no** |
| Per-space / per-folder scope | per space | **either** | whole DB | per table |
| **Incremental** | no | **yes** (`--since` + tombstones) | no | no |
| Verified on write | no | **yes** (`--all`) | no | no |
| Restore verified against source | no | **yes** (`--verify`) | no | no |
| Carries password hashes | **no** | yes | yes | yes |
| Includes sessions / locks / otp | no | no | yes | if selected |

Two rows deserve emphasis. **`COPY BINARY` is the most brittle of the four**:
its format is tied to the column list and types of the table it came from, so
any schema change between dump and load makes it unloadable — it is a fast
transfer, not an archive. And **zip is the only one that does not write password
hashes**, which is a security property, not an omission, but it also means a
zip restore cannot recover logins.

**A reasonable policy:** `COPY BINARY` or `pg_dump` for same-version PostgreSQL
disaster recovery where minutes matter; `parquet --all` for portable, verifiable
backups and anything incremental; `zip` only where the on-disk JSON layout is
the requirement.

## PostgreSQL-native commands, in full

Every option measured above, as runnable commands. `$PG` stands for
`-h HOST -p PORT -U USER`; set `PGPASSWORD` or use `~/.pggass`.

### pg_dump — custom format (binary, compressed, restores selectively)

```bash
# Backup. -Fc is binary AND zlib-compressed; -Z0..9 tunes it (default 6).
pg_dump $PG -d dmart -Fc -f dmart.pgc

# Faster and smaller than the default zlib, on PostgreSQL 16+:
pg_dump $PG -d dmart -Fc -Z zstd:3 -f dmart.pgc

# Restore into an empty database.
createdb $PG dmart_restored
pg_restore $PG -d dmart_restored --no-owner dmart.pgc

# Parallel restore — the single biggest win on a large database.
pg_restore $PG -d dmart_restored --no-owner -j 8 dmart.pgc
```

### pg_dump — directory format (parallel dump AND parallel restore)

```bash
# -Fd is the only format pg_dump can write in parallel.
pg_dump $PG -d dmart -Fd -j 8 -Z zstd:3 -f dmart.dumpdir
pg_restore $PG -d dmart_restored --no-owner -j 8 dmart.dumpdir
```

### pg_dump — plain SQL text, compressed externally

```bash
pg_dump $PG -d dmart -Fp | zstd -3 -T0 > dmart.sql.zst    # fastest useful ratio
pg_dump $PG -d dmart -Fp | gzip -6     > dmart.sql.gz     # widest compatibility

# Restore (psql reads SQL; pg_restore cannot read -Fp output).
zstd -dc dmart.sql.zst | psql $PG -d dmart_restored
```

### COPY … BINARY — per table, fastest, least portable

```bash
TABLES="users spaces roles permissions entries attachments histories"

# Dump. Client-side (TO STDOUT) so no superuser and no server-side file access.
for t in $TABLES; do
  psql $PG -d dmart -c "COPY $t TO STDOUT WITH (FORMAT BINARY)" \
    | zstd -3 -T0 > "$t.bin.zst"
done

# Restore. The target needs the SCHEMA first — COPY carries data only.
dmart migrate                     # or: pg_dump $PG -d dmart -s | psql $PG -d dmart_restored
for t in $TABLES; do              # order matters: users before anything owning rows
  zstd -dc "$t.bin.zst" \
    | psql $PG -d dmart_restored -c "COPY $t FROM STDIN WITH (FORMAT BINARY)"
done
```

**Two warnings on `COPY … BINARY`.** Its format is bound to the exact column
list and types of the source table, so *any* schema change between dump and
load makes it unloadable — it is a fast transfer, not an archive. And the load
order above is not cosmetic: `owner_shortname` is a foreign key into `users`,
so users must land first or every content row fails.

### COPY … CSV — portable, human-readable, slower

```bash
psql $PG -d dmart -c "\copy entries TO 'entries.csv' WITH (FORMAT CSV, HEADER)"
psql $PG -d dmart_restored -c "\copy entries FROM 'entries.csv' WITH (FORMAT CSV, HEADER)"
```

### Physical backup — the whole cluster, point-in-time capable

```bash
# Not comparable to anything above: copies the data directory, not rows.
# Restores the entire cluster at once, and supports PITR with WAL archiving.
pg_basebackup $PG -D /backup/base -Ft -z -Xs -P
```

### Speed levers that matter more than the format

```bash
# Parallelism, on both ends (-Fd or -Fc only):
pg_dump ... -Fd -j "$(nproc)"        pg_restore ... -j "$(nproc)"

# Skip index and constraint rebuilds during load, then build them once:
pg_restore ... --section=pre-data --section=data
pg_restore ... --section=post-data -j 8

# Data only, into a schema that already exists:
pg_dump $PG -d dmart --data-only -Fc -f data.pgc
```

Index rebuilding usually dominates a large restore. `-j` and deferring
`post-data` are worth more than any choice of compression codec — measured on
the same database as everything above:

| Command | Time | Size |
|---|---:|---:|
| `pg_dump -Fc` (default zlib) | 139 ms | 3.6 MB |
| `pg_dump -Fc -Z zstd:3` | 138 ms | 3.4 MB |
| `pg_dump -Fd -j 8 -Z zstd:3` | **99 ms** | 3.4 MB |
| `pg_restore` (serial) | 1076 ms | |
| **`pg_restore -j 8`** | **553 ms** | |

**`-j 8` nearly halves the restore** — a bigger win than any codec choice here,
and the one flag most worth adding to an existing backup script. Verified by
restoring and counting rows (22,096 entries), not just by timing the command.

## Caveats

- **One host, local database.** Every number includes near-zero network
  latency. The dmart paths issue far more round trips than `COPY` or `pg_dump`,
  so a remote database widens the gap against them, not narrows it.
- **21,843 entries is not multi-GB.** The design targets far larger
  installations; the ratios transfer better than the absolute times.
- **Media is barely represented** — 176 KB across 3 attachments. Blob-heavy
  installations shift the picture, and content-addressed blobs are exactly where
  Parquet's incremental story pays off: unchanged bytes ship once.
- **Import timings include index maintenance** on tables that already had their
  indexes. A restore that creates indexes afterwards would be faster for all
  methods.
