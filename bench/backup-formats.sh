#!/usr/bin/env bash
#
# Export/import performance: json+zip vs Parquet vs pg_dump/pg_restore.
#
# WHAT IS AND IS NOT COMPARABLE
#
# These three do different jobs, and reading the table without that in mind
# will mislead:
#
#   zip / parquet (space)  one space's content — entries, attachments, history
#   parquet --all          every space PLUS users, roles, permissions
#   pg_dump                the whole DATABASE, including tables dmart's own
#                          export deliberately omits (sessions, locks, otp,
#                          the permissions cache) and every index definition
#
# pg_dump is also the only one that is not portable across engines: it restores
# to PostgreSQL and nowhere else, whereas the other two restore to either
# backend and are readable by DuckDB/Spark without dmart at all.
#
# So pg_dump is the RIGHT baseline for "how fast could a full-database backup
# possibly be", and the wrong baseline for "which of dmart's export formats
# should I use".
#
# Usage: bench/backup-formats.sh <publish-dir> <pg-conn-env-file> [runs]
set -euo pipefail

PUB="$(cd "${1:?usage: backup-formats.sh <publish-dir> <config.env> [runs]}" && pwd)"
CONF="$(cd "$(dirname "${2:?need a config.env}")" && pwd)/$(basename "$2")"
RUNS="${3:-3}"
BIN="$PUB/dmart"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Read connection details out of the same config the binary uses, so the
# benchmark cannot accidentally measure a different database.
get() { grep -E "^$1=" "$CONF" | tail -1 | cut -d= -f2- | tr -d '"'; }
PGHOST=$(get DATABASE_HOST); PGPORT=$(get DATABASE_PORT)
PGUSER=$(get DATABASE_USERNAME); PGDB=$(get DATABASE_NAME)
export PGPASSWORD; PGPASSWORD=$(get DATABASE_PASSWORD)

SPACE="${BENCH_SPACE:-personal}"

# Best of N. A single timing on a shared host measures whatever else the host
# was doing; the minimum is the closest thing to the cost of the work itself.
best_of() {
  local label="$1"; shift
  local best=999999 t
  for _ in $(seq 1 "$RUNS"); do
    local start end
    start=$(date +%s%N)
    "$@" >"$WORK/last.out" 2>&1 || { tail -5 "$WORK/last.out"; echo "FAILED: $label" >&2; return 1; }
    # A run that "succeeded" while dropping rows would otherwise be timed as if
    # it had done the work.
    if grep -qE '[1-9][0-9]* failed' "$WORK/last.out"; then
      tail -3 "$WORK/last.out"; echo "FAILED (rows lost): $label" >&2; return 1
    fi
    end=$(date +%s%N)
    t=$(( (end - start) / 1000000 ))
    [ "$t" -lt "$best" ] && best=$t
  done
  printf '%s' "$best"
}

human() { numfmt --to=iec --suffix=B "$1" 2>/dev/null || printf '%sB' "$1"; }
size_of() { du -sb "$1" 2>/dev/null | cut -f1; }

fresh_db() {
  psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d postgres -q \
    -c "DROP DATABASE IF EXISTS $1 WITH (FORCE);" -c "CREATE DATABASE $1;" >/dev/null
}

# Schema plus the USERS, as unmeasured setup.
#
# Content rows carry owner_shortname, a foreign key into users. `migrate`
# creates the schema but no accounts — only starting the server does — so a
# target prepared with migrate alone fails every single entry on its FK, which
# is how the first run of this benchmark "imported" 40,000 history rows and
# zero entries.
#
# The zip and single-space Parquet archives do not carry users by design, so
# the owners have to be there already. `--all` brings its own; seeding it too
# costs it nothing, and keeps all four targets identical.
prepare_target() {
  local db="$1" env="$2"
  BACKEND_ENV="$env" "$BIN" migrate >/dev/null 2>&1 || true
  pg_dump -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" -t users --data-only \
    | psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$db" -q >/dev/null 2>&1 || true
}

env_for() {  # env_for <dbname> -> path to a config pointing at it
  sed "s/^DATABASE_NAME=.*/DATABASE_NAME=\"$1\"/" "$CONF" > "$WORK/$1.env"
  chmod 600 "$WORK/$1.env"
  printf '%s' "$WORK/$1.env"
}

printf '\n%-34s %10s %12s\n' "OPERATION" "TIME" "SIZE"
printf '%s\n' "----------------------------------------------------------------"

# ------------------------------------------------------------------- exports
rm -rf "$WORK/zip" "$WORK/pq" "$WORK/all" "$WORK/dump"
mkdir -p "$WORK/zip"

T=$(best_of "zip export" env BACKEND_ENV="$CONF" "$BIN" export "$SPACE" --output "$WORK/zip/s.zip")
printf '%-34s %8sms %12s\n' "export  zip      (1 space)" "$T" "$(human "$(size_of "$WORK/zip/s.zip")")"

T=$(best_of "parquet export" env BACKEND_ENV="$CONF" "$BIN" export "$SPACE" --parquet --output "$WORK/pq")
printf '%-34s %8sms %12s\n' "export  parquet  (1 space)" "$T" "$(human "$(size_of "$WORK/pq")")"

T=$(best_of "parquet --all" env BACKEND_ENV="$CONF" "$BIN" export --all --parquet --no-verify --output "$WORK/all")
printf '%-34s %8sms %12s\n' "export  parquet  (--all)" "$T" "$(human "$(size_of "$WORK/all")")"

T=$(best_of "pg_dump" pg_dump -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" -Fc -f "$WORK/dump.pgc")
printf '%-34s %8sms %12s\n' "export  pg_dump  (whole DB)" "$T" "$(human "$(size_of "$WORK/dump.pgc")")"

# ------------------------------------------------------------------- imports
#
# Every restore gets a FRESH database FOR EACH RUN, not just for each format.
#
# Two reasons, both of which silently corrupt the numbers otherwise. pg_restore
# into a populated database fails on duplicate keys — which is exactly how the
# first version of this script reported pg_restore as broken when it was fine.
# And the dmart imports run with -r, so a second run against a populated target
# measures an UPDATE of every row rather than the insert being compared.
printf '%s\n' "----------------------------------------------------------------"

# best_of, but re-creating the target database before every timed run.
best_restore() {
  local label="$1" db="$2" prep="$3"; shift 3
  local best=999999 t start end
  for _ in $(seq 1 "$RUNS"); do
    fresh_db "$db"
    [ "$prep" = "prep" ] && prepare_target "$db" "$(env_for "$db")"
    start=$(date +%s%N)
    "$@" >"$WORK/last.out" 2>&1 || { tail -5 "$WORK/last.out"; echo "FAILED: $label" >&2; return 1; }
    end=$(date +%s%N)
    if grep -qE '[1-9][0-9]* failed' "$WORK/last.out"; then
      tail -3 "$WORK/last.out"; echo "FAILED (rows lost): $label" >&2; return 1
    fi
    t=$(( (end - start) / 1000000 ))
    [ "$t" -lt "$best" ] && best=$t
  done
  printf '%s' "$best"
}

ZIPENV=$(env_for bench_zip)
T=$(best_restore "zip import" bench_zip prep env BACKEND_ENV="$ZIPENV" "$BIN" import "$WORK/zip/s.zip" -r)
printf '%-34s %8sms %12s\n' "import  zip      (1 space)" "$T" ""

PQENV=$(env_for bench_pq)
T=$(best_restore "parquet import" bench_pq prep env BACKEND_ENV="$PQENV" "$BIN" import "$WORK/pq" --parquet -r)
printf '%-34s %8sms %12s\n' "import  parquet  (1 space)" "$T" ""

ALLENV=$(env_for bench_all)
T=$(best_restore "parquet --all import" bench_all prep env BACKEND_ENV="$ALLENV" "$BIN" import "$WORK/all" --parquet -r)
printf '%-34s %8sms %12s\n' "import  parquet  (--all)" "$T" ""

# No prep: the dump carries its own schema, and pre-creating it would make
# pg_restore fight objects that already exist.
T=$(best_restore "pg_restore" bench_dump none pg_restore -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d bench_dump --no-owner "$WORK/dump.pgc")
printf '%-34s %8sms %12s\n' "restore pg_restore (whole DB)" "$T" ""

printf '%s\n' "----------------------------------------------------------------"
psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d postgres -q \
  -c "DROP DATABASE IF EXISTS bench_zip WITH (FORCE);" \
  -c "DROP DATABASE IF EXISTS bench_pq WITH (FORCE);" \
  -c "DROP DATABASE IF EXISTS bench_all WITH (FORCE);" \
  -c "DROP DATABASE IF EXISTS bench_dump WITH (FORCE);" >/dev/null
printf 'best of %s runs\n' "$RUNS"
