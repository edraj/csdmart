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

## Full matrix — every option, one run

`bench/backup-matrix.sh`. Best of 2; every restore into a freshly created
database. Sizes are what you would **store**, so uncompressed variants show
their real size rather than being quietly piped through a compressor.

| Option | Export | Import | Stored size |
|---|---:|---:|---:|
| zip / JSON (1 space) | 4078 ms | 3762 ms | 19.0 MB |
| **Parquet zstd-3 (1 space)** | 361 ms | 1621 ms | **2.0 MB** |
| **Parquet zstd-3 (`--all`)** | 436 ms | 2125 ms | 2.7 MB |
| pg_dump `-Fc` (zlib) | 137 ms | 1068 ms | 3.6 MB |
| pg_dump `-Fc` + `pg_restore -j8` | – | 513 ms | 3.6 MB |
| pg_dump `-Fc -Z zstd:3` + `-j8` | 114 ms | 507 ms | 3.4 MB |
| **pg_dump `-Fd -j8 -Z zstd:3` + `-j8`** | **75 ms** | **496 ms** | 3.4 MB |
| pg_dump `-Fp` (plain SQL) | 118 ms | 1110 ms | 32.3 MB |
| pg_dump `-Fp \| zstd -3` | 117 ms | 1109 ms | 3.4 MB |
| pg_dump `-Fp \| gzip -6` | 184 ms | 1158 ms | 3.7 MB |
| `COPY BINARY` (raw) | 92 ms | 679 ms | 27.3 MB |
| **`COPY BINARY \| zstd -3`** | 114 ms | 690 ms | **2.2 MB** |
| `COPY BINARY \| gzip -6` | 149 ms | 716 ms | 2.7 MB |
| `COPY CSV \| zstd -3` | 134 ms | 727 ms | 2.6 MB |

Scope reminder: the first two rows are **one space**; everything below covers
the **whole database** (Parquet `--all` covers every space plus users, roles
and permissions; `pg_dump` and `COPY` also include tables dmart's export omits).

### What stands out

**`pg_dump -Fd -j8 -Z zstd:3` is the fastest whole-database option in both
directions** — 75 ms out, 496 ms in, 3.4 MB. Parallelism is the lever:
`pg_restore -j8` halves the serial restore (1068 → 513 ms) at every compression
setting, which is a larger win than any codec choice in this table.

**zstd is effectively free; gzip is not.** On `COPY BINARY`, zstd costs 22 ms
and shrinks 27.3 MB to 2.2 MB. gzip costs 57 ms for a worse 2.7 MB. On plain
SQL, zstd costs nothing measurable (117 vs 118 ms) for a 9.5× reduction. There
is no reason to reach for gzip here beyond compatibility with something old.

**`COPY BINARY | zstd` remains the smallest whole-database option at 2.2 MB** —
smaller than Parquet `--all` (2.7 MB), while exporting 3.8× faster. Parquet's
2.0 MB is a *single space*, so it is not directly comparable.

**Import costs more than export everywhere**, by 4–10×. Every restore is doing
index maintenance and constraint checking that no export pays. That is also why
`-j` matters so much more on the way in.

**dmart's own paths remain the slowest**, and the zip path is dominated on every
axis by Parquet: 11× slower to export, 2.3× slower to import, 9.5× larger.

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

### And then the paged reader was replaced too — for a reason this fixture hides

The "~300 ms remaining" above breaks down further: entries and histories leave
the database in **49 ms** and **22 ms**, so roughly 280 ms of a 350 ms export is
the encoder and the writer, not reading.

Replacing the `LIMIT/OFFSET` walk with a single streaming `COPY` measured
**353 ms — no change at all**. `ExportPageSize` is 10,000, so 21,843 rows is
three round trips; there was nothing left to win at this size.

The fixture is simply too small to show the mechanism. `OFFSET` makes
PostgreSQL scan and discard every row before it, so the paged reader's cost
grows with the square of the table — and at 21,843 rows with 10,000-row pages
there are only three pages, so there is nothing to see.

Re-run END TO END on 218,430 entries (the same space copied ten times, real
payloads), best of three:

| | Time |
|---|---:|
| paged reader | 4034 ms |
| **streaming `COPY`** | **1571 ms** |

**2.6× faster, 2463 ms saved on a 10× fixture** — and the gap widens from there,
because the paged side is quadratic while the streamed side is linear. DuckDB
confirms both exports carry the same 218,430 rows with zero differing in either
direction.

For reference, the same mechanism isolated at SQL level on a synthetic
1,000,000-row table — `LIMIT/OFFSET` in 100,000-row pages against one `COPY` —
is 1868 ms versus 373 ms. That is the read alone, not an export.

The lesson worth keeping: at benchmark size this change looks like pure
complexity for zero gain, and the only reason it is in the tree is that
someone measured it at a size the benchmark does not cover.

### Then histories and attachments, where the win is much larger

`entries` was the least valuable of the three tables to convert.

**Histories** carried the paging cost AND the hydrate-then-reserialise round
trip that the raw-JSON change removed for entries: the paged reader parsed
`request_headers` and `diff` into dictionaries, which the writer immediately
serialised back for a format that stores them opaquely. Histories usually
outnumber entries — 40,000 against 21,843 here — so this is the larger win on a
full backup, and unlike entries it pays at ANY size:

| `personal` (21,843 entries + 40,000 histories) | Time |
|---|---:|
| paged | 350 ms |
| entries streamed only | 353 ms |
| **entries + histories streamed** | **304 ms** |

**Attachments** were the worst of the three, and the measurement is not close.
60,000 attachments with no media at all, so this isolates the reader:

| 60,000 attachments | Time |
|---|---:|
| paged | 4131 ms |
| **streamed** | **258 ms** |

**16x.** Attachments pay both costs at once — quadratic paging over a smaller
page size, and a full `Attachment` object built per row purely to be serialised
back.

What the streamed attachment path does NOT change: media bytes are still
fetched one row at a time. Streaming them inline would hold every blob in
memory at once, which is exactly what the per-row fetch prevents. Blob-heavy
spaces stay dominated by blob fetches, by design.

**Archive text vs archive data.** The paged attachment reader emitted C# key
order; the streamed one emits PostgreSQL's `jsonb` normalisation. The text
differs, the data does not — both archives restore to byte-identical rows,
verified by importing each into a fresh database and diffing all 60,000. Same
property the raw-JSON change established for entries.

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
`-h HOST -p PORT -U USER`; set `PGPASSWORD` or use `~/.pgpass`.

Everything here was executed against the benchmark database before being
written down, **except the `pg_basebackup` restore** — that one stops and
replaces a running cluster, so it is transcribed from the PostgreSQL manual and
has not been run. Treat it as a checklist to verify on a throwaway cluster, not
as tested copy-paste.

### dmart's own formats

```bash
# --- Parquet: one space, or a subfolder of one ---
dmart export myspace --parquet --output myspace-backup
dmart export myspace --parquet --subpath /docs --output docs-backup
dmart import myspace-backup --parquet -r             # -r overwrites; verified by default

# --- Parquet: full backup, verified on write ---
dmart export --all --parquet --output nightly
dmart import nightly --parquet -r

# --- Parquet: incremental, chained off a previous run ---
#     --since takes the previous export DIRECTORY, not a timestamp: the
#     watermark that makes the two runs overlap lives in its manifest.
dmart export myspace --parquet --since nightly --output nightly-inc
dmart import nightly-inc --parquet -r            # apply increments in order

# --- Parquet: reclaim blobs a fixed --output has accumulated ---
dmart export myspace --parquet --output nightly --gc-blobs --dry-run
dmart export myspace --parquet --output nightly --gc-blobs

# --- zip / JSON: one space, on-disk dmart layout ---
dmart export myspace --output myspace.zip
dmart import myspace.zip -r
```

A restore into an EMPTY system needs `--all`: a scoped export carries content
only, and its rows reference users that must already exist. The CLI says so
after every scoped export rather than letting it surface at restore time.

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

createdb $PG dmart_restored
pg_restore $PG -d dmart_restored --no-owner -j 8 dmart.dumpdir
```

### pg_dump — plain SQL text, compressed externally

```bash
pg_dump $PG -d dmart -Fp | zstd -3 -T0 > dmart.sql.zst    # fastest useful ratio
pg_dump $PG -d dmart -Fp | gzip -6     > dmart.sql.gz     # widest compatibility

# Restore. psql reads SQL; pg_restore CANNOT read -Fp output, and there is no
# parallelism here — the file is replayed statement by statement.
createdb $PG dmart_restored
zstd -dc dmart.sql.zst | psql $PG -d dmart_restored      # for the .zst above
gzip -dc dmart.sql.gz  | psql $PG -d dmart_restored      # for the .gz above

# -v ON_ERROR_STOP=1 is worth adding: without it psql prints errors and keeps
# going, so a half-restored database exits 0.
zstd -dc dmart.sql.zst | psql $PG -v ON_ERROR_STOP=1 -d dmart_restored
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
createdb $PG dmart_restored
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
# Not comparable to anything above: copies the DATA DIRECTORY, not rows. It
# restores the entire cluster at once — every database, every role — and it is
# the only option here that supports point-in-time recovery.
pg_basebackup $PG -D /backup/base -Ft -z -Xs -P
```

Restoring one is a **server-level** operation, not a client command:

```bash
# 1. Stop the server. Restoring into a running cluster corrupts it.
sudo systemctl stop postgresql

# 2. Move the old data directory aside — do NOT delete it until the restore
#    is verified. $PGDATA is e.g. /var/lib/pgsql/18/data.
sudo mv "$PGDATA" "$PGDATA.old"
sudo -u postgres mkdir -p "$PGDATA" && sudo chmod 700 "$PGDATA"

# 3. Unpack. -Ft -z produces base.tar.gz plus one tar per extra tablespace.
sudo -u postgres tar -xzf /backup/base/base.tar.gz -C "$PGDATA"

# 4. For POINT-IN-TIME recovery only: replay archived WAL up to a target.
#    Needs archive_mode/archive_command to have been configured BEFORE the
#    backup — a base backup alone cannot do PITR.
sudo -u postgres tee -a "$PGDATA/postgresql.auto.conf" <<'CONF'
restore_command = 'cp /backup/wal/%f %p'
recovery_target_time = '2026-08-15 03:00:00'
CONF
sudo -u postgres touch "$PGDATA/recovery.signal"

# 5. Start, and watch it come out of recovery before trusting it.
sudo systemctl start postgresql
sudo -u postgres psql -c "SELECT pg_is_in_recovery();"   # expect false when done
```

Version-locked in a way none of the others are: a base backup restores only to
the **same PostgreSQL major version** on a compatible platform, because it is
the on-disk format, not a logical dump.

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
