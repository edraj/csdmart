# dmart on a Raspberry Pi Zero 2 W (2026-09-18)

Measured with `bench/pi-zero-2w.sh`, which is checked in — re-run it rather
than trusting these numbers on a different board, SD card or image.

```
./bench/pi-zero-2w.sh <admin-password>     # run ON the device
```

The point of this report is not that dmart is fast on a Pi. It is not. The
point is the **footprint and the ceiling**: dmart v1.5.11 serves from a 416 MB
board, with PostgreSQL alongside it, and the memory it uses under concurrent
authentication is bounded rather than proportional to load. On hardware this
size that distinction is the whole thing — an unbounded peak is not a slow
request, it is an OOM kill.

## Host

- **Raspberry Pi Zero 2 W Rev 1.0** — 4 × Cortex-A53, **416 MB usable** (not
  512; the GPU split takes the rest), governor observed at 700–900 MHz against
  a 1000 MHz maximum. It never sustained its top clock during any run here.
- **Alpine 3.24.2**, kernel 6.18.35-0-rpi, aarch64. Root is a **tmpfs** (Alpine
  diskless / `lbu`), so the OS runs from RAM; `/var/lib/dmart` is ext4 on the
  SD card and holds everything that persists.
- **dmart v1.5.11-0-g105045d**, the `linux-musl-arm64` fully-static AOT build.
  No .NET runtime installed, one 46 MB binary.
- **PostgreSQL 18.6**, tuned down for the board: `shared_buffers=32MB`,
  `max_connections=20`, `work_mem=2MB`.
- Fixture: **5,000 content entries across 10 subpaths**, ~250-byte JSON payload
  each, `bucket` cardinality 50 — the same shape `bench/sqlite-vs-postgresql.py`
  builds, so the figures line up with `REPORT-sqlite-vs-postgresql.md`.

**Everything below was measured on the device.** No network hop is in any
figure. That matters more than usual here: the board's only fast link is USB
gadget ethernet, and putting a laptop in the loop would have measured the link.

## The measurement caveat, stated once

The Alpine image ships `wget` and `jq` and nothing else useful — no `curl`, no
`python3`, no `ab`. Busybox `date` has **one-second** resolution (no `%N`) and
the shell `time` builtin has 10 ms. So every latency here except login is a
**batch of 30 divided by 30**, and each request in a batch pays one `wget`
fork+exec.

That overhead is not negligible on a 1 GHz A53, which is why the harness
measures it: `/health/ready` — a real request that does `SELECT 1` through the
pool — runs **30 in 0.19 s, i.e. 6.3 ms each**. Treat that as the floor.
Server-side figures net of it are given below and are the honest ones.

An early draft of this report nearly carried two wrong numbers, both caught by
checking rather than by reasoning: a 600 kB "dmart RSS" that was actually
`avahi-daemon` (its cmdline contains `dmart-pi.local`, so `pgrep dmart` matches
it), and a 5 ms "login" that was measuring HTTP failures from a quoting bug.
The harness verifies `status=success` on every login and asserts the filtered
search selects exactly 100 rows before timing anything, so neither can recur
silently.

## Results

| Workload | Measured | Net of the 6.3 ms floor |
|---|---:|---:|
| **footprint, idle** dmart + PostgreSQL | **106 MB PSS** of 416 MB | — |
| **bulk import** 5,000 entries | **11.25 s — 444 rows/s** | — |
| **login** Argon2id verify, sequential | **280–320 ms** (6/6 verified) | ~295 ms |
| **warm read** one entry by shortname, ×30 | 0.62 s — 20.7 ms | **~14 ms** |
| **filtered search** 100 of 5,000 rows, ×30 | 1.66 s — 55.3 ms | **~49 ms** |
| **4 concurrent logins** | **650 ms wall, 4/4 success** | — |

Footprint is **PSS**, not summed RSS. Summing RSS across PostgreSQL's backends
double-counts `shared_buffers` and overstates it by roughly 3× on this
configuration — the honest split is dmart 62 MB, PostgreSQL 44 MB.

## The number this was written for

Four simultaneous logins, on a 416 MB board:

```
idle RSS          69.3 MB
peak RSS         124.7 MB     +55.4 MB
idle RSS after    69.4 MB     fully reclaimed
all four          success     no 503, no OOM
```

**+55.4 MB for four concurrent logins is ≈ 3 × 19 MiB.** Three Argon2 buffers
were resident at once and the fourth request queued behind the budget limiter,
then everything was returned. A single login shows the same thing in miniature:
RSS rises 69.3 → 88.6 MB and falls straight back, which is one
`PasswordHashMemoryKb` (19,456 KB) buffer appearing and being freed.

The counterfactual is the reason this board works at all. dmart ≤ 1.5.7
hard-coded `m=102400` — 100 MiB per hash — for parity with dmart Python. Those
same four concurrent logins would have asked for **~400 MB on a 416 MB board**,
and the kernel would have killed the process. The v1.5.8 → v1.5.11 sequence
(native libargon2, `m=19456`, a memory budget that queues rather than
allocates, and bounds on the jq and uniqueness paths) is what moved this board
from "boots" to "serves".

## Against the x86 baseline

From `REPORT-sqlite-vs-postgresql.md`, PostgreSQL on an NVMe x86 host:

| | x86 | Pi Zero 2 W | |
|---|---:|---:|---|
| bulk rebuild | 13,950 rows/s | 444 rows/s | **31× slower** |
| warm read p50 | 1.13 ms | ~14 ms | **~12× slower** |

Roughly an order of magnitude, which is about what four 1 GHz A53 cores and an
SD card should cost against a modern x86 core and NVMe. Nothing here suggests a
pathology specific to small hardware; it is the hardware.

## What this does NOT show

- **No sustained-load or soak figure.** Every workload here is tens of
  requests. Nothing ran for an hour, so nothing here speaks to thermal
  throttling, SD-card wear, or memory behaviour over days.
- **No concurrent-write ceiling.** PostgreSQL was configured with
  `max_connections=20`; that was not approached.
- **Login throughput is ~6/s at best** (4 in 650 ms) and each login costs
  ~19 MiB while it runs. A board like this is not where you put a login storm.
  The budget makes that degrade into queueing rather than into an OOM, which is
  the property worth having — but it is still a ceiling, and a low one.
- **SD-card class is uncontrolled.** Every write figure here depends on it, and
  the card was not characterised.
- The fixture is 5,000 rows. The x86 report uses 20,000. Import rate is roughly
  linear, but the read figures are from a smaller working set that fits more
  comfortably in 32 MB of `shared_buffers` than a larger one would.
