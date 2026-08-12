#!/usr/bin/env python3
"""Compare the SQLite and PostgreSQL backends on four workloads.

    ./bench/sqlite-vs-postgresql.py --rows 20000 --concurrency 16

Workloads, chosen because each one stresses a different property of the
storage layer rather than because they are easy to measure:

  bulk-rebuild      `dmart import` over a flat-file tree. This is the reindex
                    path, and it is the number that decides whether the
                    "rebuildable index" premise is practical on a given tier.
  cold-read         First entry read after the server process starts. Isolates
                    connection setup, statement preparation and first page
                    faults from steady state, which the warm figure alongside
                    it makes visible.
  filtered-search   A payload-filtered query over the whole fixture. The one
                    workload where the two engines run genuinely different
                    plans (GIN/jsonb_path_ops vs json_each).
  concurrent-write  N writers creating entries at once. SQLite admits ONE
                    writer at a time, so this is where the tier's real
                    ceiling is, and reporting only a mean would hide it —
                    the failure count and the tail matter more than the mean.

Nothing here is a microbenchmark. Every number is end-to-end wall clock
through the HTTP API or the CLI, because that is the latency a deployment
actually experiences, and because a per-call microbenchmark of two engines
with different plan shapes mostly measures the benchmark.

Requires: a built `dmart` (bin/Release/net10.0/dmart) and, for the PostgreSQL
leg, a reachable database. Stdlib only.
"""

import argparse
import json
import os
import shutil
import statistics
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BINARY = os.path.join(REPO, "bin", "Release", "net10.0", "dmart")
ADMIN = "dmart"
PASSWORD = "benchpw1"


# ---------------------------------------------------------------------------
# fixture
# ---------------------------------------------------------------------------

def build_fixture(root, space, rows, subpaths=10):
    """Import-canonical flat-file tree: {space}/{subpath}/.dm/{sn}/meta.content.json.

    The payload carries a `bucket` field with a small, known cardinality so the
    filtered-search workload has something to select on that neither engine can
    answer from the primary key. `owner_shortname` is the admin user, which the
    head pass imports first — otherwise every row fails the owner foreign key.
    """
    dm = os.path.join(root, space, ".dm")
    os.makedirs(dm, exist_ok=True)
    with open(os.path.join(dm, "meta.space.json"), "w") as f:
        json.dump({"uuid": str(uuid.uuid4()), "shortname": space, "is_active": True,
                   "owner_shortname": ADMIN, "languages": ["english"]}, f)

    udir = os.path.join(root, space, "users", ".dm", ADMIN)
    os.makedirs(udir, exist_ok=True)
    with open(os.path.join(udir, "meta.user.json"), "w") as f:
        json.dump({"uuid": str(uuid.uuid4()), "shortname": ADMIN, "is_active": True,
                   "owner_shortname": ADMIN, "email": "dmart@example.com"}, f)

    for i in range(rows):
        sub = f"s{i % subpaths}"
        sn = f"e{i}"
        d = os.path.join(root, space, sub, ".dm", sn)
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, "meta.content.json"), "w") as f:
            json.dump({
                "uuid": str(uuid.uuid4()), "shortname": sn, "is_active": True,
                "owner_shortname": ADMIN,
                "payload": {"content_type": "json", "body": {
                    "bucket": f"b{i % 50}",
                    "seq": i,
                    "note": "lorem ipsum dolor sit amet consectetur adipiscing elit " * 2,
                }},
            }, f)
    return space


# ---------------------------------------------------------------------------
# server / config
# ---------------------------------------------------------------------------

def write_config(path, driver, work, port, pg_conn):
    lines = [
        f'SPACES_FOLDER="{os.path.join(work, "spaces")}"',
        f"LISTENING_PORT={port}",
        'JWT_SECRET="bench-secret-bench-secret-bench-secret-48-chars"',
        f'ADMIN_PASSWORD="{PASSWORD}"',
    ]
    if driver == "sqlite":
        lines += ['DATABASE_DRIVER="sqlite"',
                  f'SQLITE_PATH="{os.path.join(work, "dmart.db")}"']
    else:
        host, port_, user, pw, db = pg_conn
        lines += ['DATABASE_DRIVER="postgresql"',
                  f'DATABASE_HOST="{host}"', f'DATABASE_PORT="{port_}"',
                  f'DATABASE_USERNAME="{user}"', f'DATABASE_PASSWORD="{pw}"',
                  f'DATABASE_NAME="{db}"']
    with open(path, "w") as f:
        f.write("\n".join(lines) + "\n")


def start_server(cfg, port, log_path):
    log = open(log_path, "w")
    proc = subprocess.Popen([BINARY, "serve"], cwd=os.path.dirname(BINARY),
                            env={**os.environ, "BACKEND_ENV": cfg},
                            stdout=log, stderr=subprocess.STDOUT)
    for _ in range(120):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{port}/health/ready", timeout=2)
            return proc
        except Exception:
            if proc.poll() is not None:
                raise SystemExit(f"server exited early; see {log_path}")
            time.sleep(0.5)
    proc.kill()
    raise SystemExit(f"server never became ready; see {log_path}")


def api(port, path, body=None, token=None, method=None):
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}",
        data=json.dumps(body).encode() if body is not None else None,
        method=method or ("POST" if body is not None else "GET"))
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read())


def login(port):
    r = api(port, "/user/login", {"shortname": ADMIN, "password": PASSWORD})
    return r["records"][0]["attributes"]["access_token"]


# ---------------------------------------------------------------------------
# workloads
# ---------------------------------------------------------------------------

def bulk_rebuild(cfg, src, rows):
    """`dmart import` of the whole tree. Wall clock includes process start and
    the filesystem walk, matching bench/REPORT-import-default-batching.md so
    the two reports are comparable."""
    t0 = time.perf_counter()
    p = subprocess.run([BINARY, "import", src, "--no-validate"],
                       cwd=os.path.dirname(BINARY),
                       env={**os.environ, "BACKEND_ENV": cfg},
                       capture_output=True, text=True)
    dt = time.perf_counter() - t0
    if p.returncode != 0 or " 0 rows" in p.stdout:
        raise SystemExit(f"import failed:\n{p.stdout}\n{p.stderr}")
    return {"seconds": dt, "rows_per_sec": rows / dt}


def cold_read(port, token, space):
    """First read after the process started, then the steady-state median.

    Reported as a pair on purpose. A cold figure alone is unreadable — it is
    only meaningful next to what the same call costs once everything is warm.
    """
    t0 = time.perf_counter()
    api(port, f"/managed/entry/content/{space}/s0/e0", token=token)
    cold = time.perf_counter() - t0

    warm = []
    for i in range(50):
        t = time.perf_counter()
        api(port, f"/managed/entry/content/{space}/s{i % 10}/e{i}", token=token)
        warm.append(time.perf_counter() - t)
    return {"cold_ms": cold * 1000, "warm_p50_ms": statistics.median(warm) * 1000}


def filtered_search(port, token, space, reps=30):
    """Payload-filtered query over the whole fixture."""
    q = {"type": "search", "space_name": space, "subpath": "/",
         "search": "@payload.body.bucket:b7", "limit": 100, "retrieve_json_payload": True}
    counts = set()
    times = []
    for _ in range(reps):
        t = time.perf_counter()
        r = api(port, "/managed/query", q, token=token)
        times.append(time.perf_counter() - t)
        counts.add(r.get("attributes", {}).get("total"))
    if len(counts) != 1:
        raise SystemExit(f"unstable result count across reps: {counts}")
    times.sort()
    return {"p50_ms": statistics.median(times) * 1000,
            "p95_ms": times[int(len(times) * 0.95) - 1] * 1000,
            "matched": counts.pop()}


def concurrent_write(port, token, space, concurrency, per_worker):
    """N writers creating entries simultaneously.

    Failures are counted, not swallowed. SQLite serializes writers, and the
    honest question is not "how fast" but "does anything get REFUSED, and how
    long does the unluckiest writer wait" — a mean would hide both.
    """
    def one(n):
        sn = f"cw{n}_{uuid.uuid4().hex[:8]}"
        body = {"space_name": space, "request_type": "create", "records": [{
            "resource_type": "content", "subpath": "/bench", "shortname": sn,
            "attributes": {"is_active": True,
                           "payload": {"content_type": "json", "body": {"n": n}}}}]}
        t = time.perf_counter()
        try:
            r = api(port, "/managed/request", body, token=token)
            ok = r.get("status") == "success"
        except Exception:
            ok = False
        return ok, time.perf_counter() - t

    total = concurrency * per_worker
    t0 = time.perf_counter()
    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        results = list(pool.map(one, range(total)))
    wall = time.perf_counter() - t0

    lat = sorted(d for _, d in results)
    failed = sum(1 for ok, _ in results if not ok)
    return {"writes_per_sec": total / wall, "failed": failed, "total": total,
            "p50_ms": statistics.median(lat) * 1000,
            "p99_ms": lat[int(len(lat) * 0.99) - 1] * 1000,
            "max_ms": lat[-1] * 1000}


# ---------------------------------------------------------------------------

def reset_postgres(pg_conn):
    host, port, user, pw, db = pg_conn
    env = {**os.environ, "PGPASSWORD": pw}
    base = ["-h", host, "-p", str(port), "-U", user]
    subprocess.run(["dropdb", "-w", "--if-exists", *base, db], env=env, check=True)
    subprocess.run(["createdb", "-w", *base, db], env=env, check=True)
    # `dmart import` does not create the schema on PostgreSQL the way it does
    # on SQLite (there, an absent file is the normal cold-start case).
    subprocess.run([BINARY, "migrate", "-q"], cwd=os.path.dirname(BINARY),
                   env={**os.environ, "BACKEND_ENV": os.environ["BENCH_CFG"]},
                   check=True, capture_output=True)


def run_driver(driver, args, src, space, pg_conn):
    work = tempfile.mkdtemp(prefix=f"dmart-bench-{driver}-")
    os.makedirs(os.path.join(work, "spaces"), exist_ok=True)
    cfg = os.path.join(work, "config.env")
    port = args.port if driver == "sqlite" else args.port + 1
    write_config(cfg, driver, work, port, pg_conn)
    os.environ["BENCH_CFG"] = cfg

    if driver == "postgresql":
        reset_postgres(pg_conn)

    out = {}
    print(f"  [{driver}] bulk-rebuild ...", flush=True)
    out["bulk_rebuild"] = bulk_rebuild(cfg, src, args.rows)

    print(f"  [{driver}] starting server ...", flush=True)
    proc = start_server(cfg, port, os.path.join(work, "server.log"))
    try:
        token = login(port)
        print(f"  [{driver}] cold-read ...", flush=True)
        out["cold_read"] = cold_read(port, token, space)
        print(f"  [{driver}] filtered-search ...", flush=True)
        out["filtered_search"] = filtered_search(port, token, space)
        print(f"  [{driver}] concurrent-write ...", flush=True)
        out["concurrent_write"] = concurrent_write(
            port, token, space, args.concurrency, args.writes_per_worker)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
    out["_work"] = work
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rows", type=int, default=20000)
    ap.add_argument("--concurrency", type=int, default=16)
    ap.add_argument("--writes-per-worker", type=int, default=25)
    ap.add_argument("--port", type=int, default=5310)
    ap.add_argument("--pg", default="127.0.0.1:55432:dmart:scratchpw:dmartbench",
                    help="host:port:user:password:database")
    ap.add_argument("--drivers", default="sqlite,postgresql")
    args = ap.parse_args()

    if not os.path.exists(BINARY):
        raise SystemExit(f"{BINARY} not found — run: dotnet build dmart.slnx -c Release")

    host, port, user, pw, db = args.pg.split(":")
    pg_conn = (host, port, user, pw, db)

    src = tempfile.mkdtemp(prefix="dmart-bench-src-")
    space = "bench" + uuid.uuid4().hex[:6]
    print(f"building fixture: {args.rows} entries in {src}", flush=True)
    build_fixture(src, space, args.rows)

    results = {}
    try:
        for driver in args.drivers.split(","):
            print(f"--- {driver} ---", flush=True)
            results[driver] = run_driver(driver, args, src, space, pg_conn)
    finally:
        shutil.rmtree(src, ignore_errors=True)

    print()
    print(json.dumps({k: {kk: vv for kk, vv in v.items() if not kk.startswith("_")}
                      for k, v in results.items()}, indent=2))
    for driver, r in results.items():
        shutil.rmtree(r["_work"], ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
