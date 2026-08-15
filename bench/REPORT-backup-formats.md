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
| **Parquet** | 1 space | 663 ms | 1639 ms | **2.0 MB** |
| **Parquet `--all`** | all spaces + users/roles/perms | 741 ms | 2246 ms | **2.7 MB** |
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

## Where the export time actually goes

Not in the Parquet encoder. The database emits the same rows an order of
magnitude faster than dmart can export them:

| | Time | Output |
|---|---:|---:|
| `COPY (SELECT * FROM entries WHERE space_name='personal') TO STDOUT` | **52 ms** | 19 MB |
| dmart Parquet export, same 21,843 rows | 663 ms | 2.0 MB |

So roughly **610 of those 663 ms are dmart's pipeline**, not encoding. The
encoder is nowhere near its limit either: 2.0 MB in 663 ms is ~3 MB/s, where
zstd level 3 runs at hundreds of MB/s.

The difference is what dmart does that `COPY` does not — page rows through the
repository, hydrate each into a C# object (parsing every `jsonb` column into
`Payload`, `Translation`, `AclEntry`…), then **re-serialise those columns back
to JSON strings** for the opaque-string representation §2.2 chose. `COPY` ships
the bytes the heap already holds.

**The format is not the cost; the object model is.** Anyone wanting this export
to approach `COPY` speed should attack the hydrate-then-reserialise round trip
for JSON columns — pure loss for an export that stores them opaquely anyway —
not the Parquet writer.

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
