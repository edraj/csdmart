# SQLite vs PostgreSQL: four workloads (2026-08-12)

Measured with `bench/sqlite-vs-postgresql.py`, which is checked in — re-run it
rather than trusting these numbers on a different host.

```
./bench/sqlite-vs-postgresql.py --rows 20000 --concurrency 16 --writes-per-worker 50
```

## Fixture and host

- 20,000 content entries, ~250-byte JSON payload each, one space, 10 subpaths,
  import-canonical layout. Payload carries `bucket` with cardinality 50, so the
  filtered search selects 400 rows — verified equal on both backends, which is
  also the cross-check that both stores really hold the whole fixture.
- Host: Linux 7.1.7, NVMe, same machine as the client.
- SQLite: one file, WAL, `synchronous=NORMAL` — the PRAGMAs
  `SqliteConnectionFactory` applies in production.
- PostgreSQL 18 in podman on loopback (`-p 55432:5432`). **This is a
  handicap the numbers do not separate out**: every PostgreSQL figure carries a
  container network hop that the SQLite figures do not. It shows most clearly
  in the warm read, where the two engines are doing almost identical work and
  PostgreSQL is 0.5 ms slower. Treat small PostgreSQL-side deficits as
  possibly-artifactual; the large ones are not.
- Every number is end-to-end wall clock through the HTTP API or the CLI. None
  of this is a microbenchmark.
- One small asymmetry, stated rather than corrected because it is noise at this
  scale: the PostgreSQL leg runs `dmart migrate` before the timed import, while
  the SQLite leg creates its schema inside it (an absent file is the normal
  cold-start case there). That charges SQLite a few milliseconds of DDL against
  a 5-second measurement.
- Each figure below reproduced within a few percent on a second full run.

## Results

| Workload | SQLite | PostgreSQL | |
|---|---:|---:|---|
| **bulk rebuild** 20k rows | 5.11 s — 3,913 rows/s | 1.43 s — 13,950 rows/s | PG **3.6×** |
| **cold read** first request | 43.2 ms | 50.1 ms | SQLite 1.2× |
| **warm read** p50 | 0.63 ms | 1.13 ms | SQLite 1.8× |
| **filtered search** p50 | 67.0 ms | 10.7 ms | PG **6.2×** |
| **filtered search** p95 | 76.2 ms | 13.1 ms | |
| **concurrent write** throughput | 1,433 /s | 1,636 /s | PG 1.1× |
| **concurrent write** p50 | 2.6 ms | 8.8 ms | SQLite 3.4× |
| **concurrent write** p99 | 82.5 ms | 49.7 ms | PG 1.7× |
| **concurrent write** max | **555 ms** | 51 ms | PG **10.8×** |
| **concurrent write** failed | 0 / 800 | 0 / 800 | |

## What the numbers mean

**Filtered search is the real gap, and it is structural.** Both dialects emit a
containment test for `@payload.body.bucket:b7`. PostgreSQL spells it
`payload::jsonb @> $1`, which `EXPLAIN (ANALYZE)` on this fixture confirms is a
Bitmap Index Scan on `idx_entries_payload_gin` — 400 rows in 0.32 ms at the SQL
level, so the 10.7 ms HTTP figure is almost entirely request overhead, ACL and
serialization rather than the scan.

SQLite has no equivalent index, so `SqliteSqlDialect.JsonContains` implements
containment structurally: a `json_tree` walk of the probe document, and for each
of its atoms a correlated `NOT EXISTS` over a `json_tree` walk of the row's
payload. That is nested per-row work with no index to prune it, so the cost
grows with both corpus size and document size where PostgreSQL's does not. 67 ms
over 20k rows is fine; the same query shape over 500k rows will not be.

This is the single limit that should decide whether a deployment belongs on this
tier, and it is not fixable by tuning — it is the absence of an index type.

**Concurrent write is the result most likely to be misread.** SQLite's *median*
write is 3.4× FASTER than PostgreSQL's, and its *worst* write is 10.8× slower.
Reporting a mean would have said "SQLite is fine here"; reporting only the max
would have said "SQLite falls over". Both would be wrong. What is actually
happening is that a write that gets the lock is very cheap (no network, no
process boundary) and a write that does not gets queued behind every other
writer.

That diagnosis was checked rather than assumed. Re-running the same 800 writes
through a **single** writer gives max 32 ms and p99 2.2 ms — so the 555 ms tail
is writer contention, not a WAL checkpoint stall, which is the other thing a
half-second SQLite pause usually means. Worth knowing because the two have
different remedies and only one of them is available here.

Notably **nothing failed**: 0 of 800 writes were refused on either backend.
`busy_timeout` plus `SqliteRetry` absorbed the contention into latency instead
of errors, which is the behaviour they were built for. The cost is paid by the
unluckiest request, not by the caller's error handler.

**Bulk rebuild is 3.6× slower on SQLite, and that is acceptable.** PostgreSQL
uses binary COPY into a temp table plus a merge; SQLite writes row by row
through the repositories (see `docs/sqlite-reindex-handoff.md` §4). 20k rows in
5 seconds means a 200k-row store rebuilds in under a minute, which is well
inside what the tier's scope needs. Batching the per-row path inside explicit
`BEGIN IMMEDIATE` transactions was considered and deliberately not done — it
would mean threading a connection through every repository signature. These
numbers are what that decision should be revisited against, and they do not
currently justify it.

**Cold and warm reads favour SQLite**, which is the expected shape: no
connection handshake, no network, no separate process. The cold figure is
dominated by first-request setup on both (JIT, DI graph, first statement
prepare), which is why it is reported next to the warm p50 rather than alone —
in isolation a 43 ms number reads as slow when the steady state is 0.6 ms.

## Where this leaves the tier

Nothing here contradicts the scope in `docs/sqlite-backend-audit.md`: dev, CI,
single-node, small and edge deployments. Reads are faster, rebuilds are
comfortably fast enough, writes do not fail under concurrency — and filtered
search over a large corpus is the one place the missing index type shows up as
a hard ceiling rather than a constant factor.
