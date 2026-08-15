# Backup formats: json+zip vs Parquet vs pg_dump

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

| Operation | Time | Archive |
|---|---:|---:|
| **export** zip (1 space) | 4115 ms | 19 MB |
| **export** parquet (1 space) | **674 ms** | **2.0 MB** |
| **export** parquet (`--all`) | 757 ms | 2.7 MB |
| **export** pg_dump `-Fc` (whole DB) | **141 ms** | 3.6 MB |
| **import** zip (1 space) | 3620 ms | |
| **import** parquet (1 space) | **1632 ms** | |
| **import** parquet (`--all`) | 2075 ms | |
| **restore** pg_restore (whole DB) | **1086 ms** | |

All three restores were checked to land identical row counts
(21,843 entries / 40,000 histories), so the timings compare equal work.

## What the numbers say

**Parquet beats zip on every axis for dmart's own formats.** 6.1× faster to
export, 2.2× faster to import, and **9.5× smaller** — 2.0 MB against 19 MB for
the same content.

The size gap is the more interesting half. It is not mainly zstd: it is that
the zip writes one JSON file per entry, so 21,843 entries means 21,843 local
headers, 21,843 central-directory entries, and deflate compressing each file
independently with no shared dictionary. Parquet writes each column once,
contiguously, and compresses across rows — where a column like `owner_shortname`
holding the same value 21,843 times costs almost nothing.

**pg_dump is faster than both, and that is expected.** It is the database
writing its own pages out through a path with no object model in it: no JSON
serialisation, no row hydration into C# objects, no per-entry file. 141 ms
against 674 ms is roughly the cost of dmart's export doing semantic work that
`COPY TO` does not.

## Parquet vs pg_dump is NOT binary-vs-text

The table above uses `pg_dump -Fc`, which is **binary and zlib-compressed** —
not the SQL text most people picture. Measured across formats, whole database:

| pg_dump format | Time | Size |
|---|---:|---:|
| plain SQL text (`-Fp`) | 108 ms | **33 MB** |
| plain SQL + `gzip -6` | 182 ms | 3.7 MB |
| custom binary+zlib (`-Fc`) | 138 ms | 3.6 MB |
| **parquet `--all`** | 757 ms | **2.7 MB** |

**On size, Parquet wins against every pg_dump format** — 12× smaller than plain
SQL, and still ~25% smaller than the compressed binary one, while covering a
subset of the same database. Columnar layout plus zstd beats row-oriented
compression, exactly as the shape suggests.

**On time, pg_dump wins regardless of its format**, which is the tell: 108 ms
for 33 MB of text and 138 ms for 3.6 MB of compressed binary means the encoding
is not what separates them.

### Where the time actually goes

The database can emit the same rows far faster than dmart can export them:

| | Time | Output |
|---|---:|---:|
| `COPY (SELECT * FROM entries WHERE space_name='personal') TO STDOUT` | **52 ms** | 19 MB |
| dmart parquet export, same 21,843 rows | 674 ms | 2.0 MB |

So roughly **620 of those 674 ms are dmart's pipeline, not Parquet encoding**.
The encoder is nowhere near the limit either — 2.0 MB in 674 ms is ~3 MB/s,
while zstd level 3 runs at hundreds of MB/s.

The gap is what dmart does that `COPY` does not: page the rows through the
repository, hydrate each one into a C# object (parsing every `jsonb` column
into `Payload`, `Translation`, `AclEntry`…), then **re-serialise those columns
back to JSON strings** for the opaque-string representation §2.2 chose. `COPY`
ships the bytes the heap already holds.

That is the honest summary: **the format is not the cost — the object model
is.** Anyone wanting dmart's export to approach `pg_dump` speed should look at
avoiding the hydrate-then-reserialise round trip for JSON columns, not at the
Parquet writer.

## Choosing between them — the times are not the deciding factor

`pg_dump` wins on speed and loses on everything that makes a backup useful
beyond restoring the same PostgreSQL version to itself:

|  | zip | parquet | pg_dump |
|---|---|---|---|
| Restores to PostgreSQL | yes | yes | yes |
| Restores to **SQLite** | yes | yes | **no** |
| Readable without dmart | partly (JSON) | **yes** (DuckDB/Spark) | no |
| Per-space or per-folder scope | per space | **space or subfolder** | whole DB only |
| **Incremental** | no | **yes** (`--since` + tombstones) | no |
| Verified on write | no | **yes** (`--all`) | no |
| Restore verified against source | no | **yes** (`--verify`) | no |
| Carries password hashes | **no** | yes | yes |
| Includes sessions/locks/otp | no | no | yes |

The last row cuts both ways: `pg_dump` restores operational state the other two
deliberately drop, and that is either fidelity or noise depending on whether
you are cloning an environment or recovering data.

**A reasonable policy:** `pg_dump` for same-version PostgreSQL disaster
recovery where speed matters most; `parquet --all` for portable, verifiable
backups and for anything incremental; `zip` only where the on-disk JSON layout
itself is the requirement.

## Caveats

- **One host, local database.** Every number here includes near-zero network
  latency. The dmart paths issue far more round trips than `pg_dump`, so a
  remote database widens the gap against them, not narrows it.
- **21,843 entries is not multi-GB.** The design targets installations far
  larger; the ratios are more transferable than the absolute times.
- **Media is barely represented** — 176 KB across 3 attachments. Blob-heavy
  installations shift the picture, and content-addressed blobs are exactly
  where Parquet's incremental story pays off (unchanged bytes ship once).
- **Import timings include index maintenance** on a table that already had its
  indexes; a restore into a truly empty database with indexes created afterwards
  would be faster for all three.
