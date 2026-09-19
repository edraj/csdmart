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

## The 10-hour write soak

The workloads above are tens of requests each. This one ran **1,200 cycles over
10.14 hours** — every 30 seconds: a login (full Argon2id verify), three creates,
an update, a query, and three deletes of the batch from 20 cycles back. The
deletes are the design: row count reaches a steady ~60 and stays there, so the
figure measures write AMPLIFICATION rather than the cost of a growing table.

```
1,200 cycles · 10.14 h · errors 0 · login failures 0
```

| | |
|---|---|
| Resident memory | 69.7 → 73.5 MB (**+3.75 MB**) |
| Peak (VmHWM) | **124.3 MB, constant for the entire run** |
| Temperature | 39.7–48.3 °C, mean 44.9 (a Pi throttles at 80) |
| SD written | 263.3 MB → **26.0 MB/h · 0.61 GB/day** |
| Entries | bounded throughout, as designed |

### The +3.75 MB is a plateau, not a leak

A single start-and-end pair cannot tell those apart, and the distinction
matters: 0.37 MB/h sustained linearly is ~270 MB in a month, which on a 416 MB
board is fatal. The shape settles it.

| quarter | span | RSS | delta |
|---|---|---|---:|
| Q1 | 0.1–2.5 h | 69.7 → 74.0 MB | **+4.29** |
| Q2 | 2.6–5.1 h | 74.0 → 74.8 MB | **+0.81** |
| Q3 | 5.2–7.6 h | 74.9 → 73.7 MB | **−1.21** |
| Q4 | 7.7–10.1 h | 73.7 → 73.5 MB | **−0.21** |

Growth decays by roughly 5x in the second quarter and then turns negative. Means
over successive sixths — 71.8, 73.7, 74.5, 74.3, 73.6, 73.6 MB — rise, peak, and
come back down to a level they then hold. **A leak cannot do that**: it holds its
slope. This is a working set filling and settling, which is also what the
constant high-water mark says.

### Wear

An earlier draft of this report said idle writes were **zero**, on the strength
of a 300-second sample. That was a measurement artifact and is corrected here.
PostgreSQL's `checkpoint_timeout` on this board is 15 minutes, so a five-minute
window can easily contain no checkpoint at all and read as zero. Re-measured
over a full 15-minute window with dmart and PostgreSQL up and **no** request
traffic — the soak stopped and verified stopped at both ends of the window:

| workload | write-ops/h | SD written | per day | to 100 TBW |
|---|---:|---:|---:|---:|
| idle, no traffic | 0 | **1.66 MB/h** | 0.04 GB | ~7,200 yr |
| light soak — 2 creates + 2 deletes / 300 s | 48 | **5.68 MB/h** | 0.13 GB | ~2,100 yr |
| heavy soak — 3 creates + 1 update + 3 deletes / 30 s | 828 | **25.97 MB/h** | 0.61 GB | ~460 yr |

Two things follow, and only the second one is the one to repeat.

**Idle is low, not zero.** ~1.7 MB/h is the floor a box pays for being switched
on with a database attached — checkpoints and autovacuum, not dmart. It is still
two orders of magnitude below the usual appliance failure mode, where logging and
atime churn grind a card down continuously; much of that credit belongs to the
image (root on tmpfs under Alpine's `lbu`) rather than to dmart. But "wear scales
with work, not uptime" was too strong. Wear scales *mostly* with work.

**Write cost is not linear in work.** The marginal cost of a write-op falls from
~86 KB between idle and light load to ~27 KB between light and heavy — the
per-checkpoint overhead is fixed, so it amortizes as the rate rises. A deployment
doing very little work pays proportionally more per operation than a busy one.

At 0.61 GB/day under the heaviest load measured, a card rated for even 100 TBW is
not the constraint, and that conclusion survives the correction comfortably.

Caveat on n: the idle figure is a single 15-minute window containing one
checkpoint, not a long-run average.

## What this does NOT show

- **No multi-day behaviour.** Ten hours covers no daily cycle, so nothing here
  speaks to log rotation, autovacuum on a longer period, ambient temperature
  swings between night and day, or heap fragmentation, which typically needs
  days. An unattended harness (`/etc/periodic/15min/dmart-soak`, shipped in the
  board image) now runs a low-rate version of this indefinitely and records
  reboots, OOM kills and dmart restarts across power cuts, which is the only way
  to reach those. It is cron-driven rather than an OpenRC service on purpose:
  the image is Alpine diskless, and `lbu` cannot persist `/etc/init.d` — apk's
  protected paths exclude it, so an init-script version of this harness did not
  survive its first reboot.
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
