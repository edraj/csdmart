#!/usr/bin/env bash
#
# End-to-end backup/restore coverage for the NATIVE binary.
#
# Everything here was verified by hand once and could regress silently: that
# both export formats survive a full cycle, that attachment MEDIA BYTES survive
# it, and that a full Parquet backup restores into a genuinely EMPTY system.
#
# Restoring into a fresh database rather than wiping and re-importing is
# deliberate — it is the actual disaster-recovery shape, and it needs no SQL
# access, so this runs anywhere the binary does.
#
# Content comes from the bundled sample spaces rather than hand-written JSON:
# they are real dmart layouts (50 entries, attachments with media, users, roles
# and permissions), and hand-crafting that shape produced nothing but
# "folder meta path malformed" and foreign-key failures.
#
# Counts are read from the CLI's own output rather than by querying the
# database: it keeps this dependency-free (no sqlite3, no jq, no python) and it
# exercises the reporting an operator actually reads.
#
# Usage: backup-round-trip.sh <path-to-published-dir> [base-port]
set -euo pipefail

PUB="$(cd "${1:?usage: backup-round-trip.sh <publish-dir> [base-port]}" && pwd)"
BIN="$PUB/dmart"
# Default deliberately clear of 5432 (PostgreSQL) — the script binds
# BASE_PORT, +1 and +2, and a base of 5430 lands the third one on it.
BASE_PORT="${2:-5600}"
WORK="$(mktemp -d)"
SERVER_PID=""

cleanup() {
  [ -n "$SERVER_PID" ] && kill "$SERVER_PID" 2>/dev/null || true
  rm -rf "$WORK"
}
trap cleanup EXIT

say() { printf '\n=== %s ===\n' "$1"; }
die() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }

# "  entries      3 rows" -> 3.  A table absent from the output reads as 0
# rather than as a shell error, so a table that stopped being exported fails
# the comparison instead of the script.
count_of() { awk -v t="$2" '$1==t && $3=="rows" {print $2; f=1} END{if(!f) print 0}' "$1"; }

# Each stage is its own isolated system: own database file, own spaces folder.
make_env() {
  local name="$1" port="$2"
  {
    echo 'DATABASE_DRIVER="sqlite"'
    echo "SQLITE_PATH=\"$WORK/$name.db\""
    echo "SPACES_FOLDER=\"$WORK/$name-spaces\""
    echo "LISTENING_PORT=$port"
    echo 'JWT_SECRET="ci-round-trip-secret-ci-round-trip-32b"'
    echo 'ADMIN_PASSWORD="dmart"'
    echo 'MOCK_SMTP_API=true'
  } > "$WORK/$name.env"
  chmod 600 "$WORK/$name.env"
  mkdir -p "$WORK/$name-spaces"
}

# A database is bootstrapped by starting the server once: that is what creates
# the schema AND the admin user. Content rows carry owner_shortname, which is a
# foreign key into users — importing into a database that has never run the
# server fails on every row.
bootstrap() {
  local name="$1" port="$2"
  make_env "$name" "$port"
  BACKEND_ENV="$WORK/$name.env" "$BIN" serve >"$WORK/$name-serve.log" 2>&1 &
  SERVER_PID=$!
  local ready=0
  for _ in $(seq 1 60); do
    if curl -sf "http://127.0.0.1:$port/health/ready" >/dev/null 2>&1; then ready=1; break; fi
    sleep 1
  done
  kill "$SERVER_PID" 2>/dev/null || true
  wait "$SERVER_PID" 2>/dev/null || true
  SERVER_PID=""
  [ "$ready" = "1" ] || { tail -20 "$WORK/$name-serve.log"; die "$name: server never became ready"; }
}

run() {  # run <env> <logfile> <args...>
  local env="$1" log="$2"; shift 2
  BACKEND_ENV="$WORK/$env.env" "$BIN" "$@" >"$log" 2>&1 \
    || { cat "$log"; die "command failed: dmart $*"; }
}

# ------------------------------------------------------------------ source
say "build a source system from the bundled spaces"
bootstrap source "$BASE_PORT"
run source "$WORK/seedfiles.out" seed files-only
run source "$WORK/seed.out" import "$WORK/source-spaces"
grep -E '^Imported' "$WORK/seed.out" || true
grep -qE '0 failed' "$WORK/seed.out" || { cat "$WORK/seed.out"; die "seeding reported failures"; }

SPACE=applications
run source "$WORK/base.out" export "$SPACE" --parquet --output "$WORK/base"
BASE_E=$(count_of "$WORK/base.out" entries)
BASE_A=$(count_of "$WORK/base.out" attachments)
BASE_BLOBS=$(find "$WORK/base/blobs" -type f 2>/dev/null | wc -l)
printf 'baseline: entries=%s attachments=%s blobs=%s\n' "$BASE_E" "$BASE_A" "$BASE_BLOBS"

# Guard the FIXTURE, not just the code: if the bundled spaces ever stop
# carrying an attachment with media, every media assertion below would pass
# vacuously.
[ "$BASE_E" -gt 0 ]     || die "the source space has no entries — fixture is broken"
[ "$BASE_A" -gt 0 ]     || die "the source space has no attachments — fixture is broken"
[ "$BASE_BLOBS" -gt 0 ] || die "no blobs — the fixture has no attachment MEDIA to test with"

# ------------------------------------------------------------------ zip cycle
say "zip: export, then restore into a clean system"
run source "$WORK/zipexp.out" export "$SPACE" --output "$WORK/rt.zip"

bootstrap zip $((BASE_PORT + 1))
run zip "$WORK/zipimp.out" import "$WORK/rt.zip" -r
grep -qE '0 failed' "$WORK/zipimp.out" || { cat "$WORK/zipimp.out"; die "zip restore reported failures"; }

run zip "$WORK/zipcheck.out" export "$SPACE" --parquet --output "$WORK/zipcheck"
Z_E=$(count_of "$WORK/zipcheck.out" entries)
Z_A=$(count_of "$WORK/zipcheck.out" attachments)
Z_BLOBS=$(find "$WORK/zipcheck/blobs" -type f 2>/dev/null | wc -l)
printf 'after zip restore: entries=%s attachments=%s blobs=%s\n' "$Z_E" "$Z_A" "$Z_BLOBS"

[ "$Z_E" = "$BASE_E" ] || die "zip restore: entries $Z_E != $BASE_E"
[ "$Z_A" = "$BASE_A" ] || die "zip restore: attachments $Z_A != $BASE_A"
# Media survived only if the restored system re-exports blobs of its own.
[ "$Z_BLOBS" = "$BASE_BLOBS" ] \
  || die "zip restore: $Z_BLOBS blobs vs $BASE_BLOBS — attachment MEDIA did not survive"

# -------------------------------------------------------------- parquet cycle
say "parquet: full backup, then restore into a clean system, verified"
run source "$WORK/allexp.out" export --all --parquet --output "$WORK/full"
grep -q 'Verified' "$WORK/allexp.out" || { cat "$WORK/allexp.out"; die "--all did not verify the archive"; }

bootstrap pq $((BASE_PORT + 2))
# -r, not the default: the target was BOOTSTRAPPED, so it already holds
# management rows the server created with its own uuids. Without -r those are
# skipped and the archive's versions never land — which verification then
# correctly reports as 4 differing entries. Restoring a backup over a
# freshly-initialised system is what -r is for.
run pq "$WORK/pqimp.out" import "$WORK/full" --parquet -r --verify
grep -q 'match the archive' "$WORK/pqimp.out" \
  || { cat "$WORK/pqimp.out"; die "restore verification did not report a match"; }

run pq "$WORK/pqcheck.out" export "$SPACE" --parquet --output "$WORK/pqcheck"
P_E=$(count_of "$WORK/pqcheck.out" entries)
P_A=$(count_of "$WORK/pqcheck.out" attachments)
P_BLOBS=$(find "$WORK/pqcheck/blobs" -type f 2>/dev/null | wc -l)
printf 'after parquet restore: entries=%s attachments=%s blobs=%s\n' "$P_E" "$P_A" "$P_BLOBS"

[ "$P_E" = "$BASE_E" ] || die "parquet restore: entries $P_E != $BASE_E"
[ "$P_A" = "$BASE_A" ] || die "parquet restore: attachments $P_A != $BASE_A"
[ "$P_BLOBS" = "$BASE_BLOBS" ] \
  || die "parquet restore: $P_BLOBS blobs vs $BASE_BLOBS — attachment MEDIA did not survive"

# A full backup must carry the users, or the restored system has no accounts —
# the difference between a backup and a content archive.
grep -qE '^  users +[1-9]' "$WORK/allexp.out" \
  || { cat "$WORK/allexp.out"; die "--all exported no users"; }

# ------------------------------------------------------------------ increment
say "incremental: chained off the baseline"
run source "$WORK/inc.out" export "$SPACE" --parquet --since "$WORK/base" --output "$WORK/inc"
I_E=$(count_of "$WORK/inc.out" entries)
# Nothing changed since the baseline, so the increment must be EMPTY. A
# non-empty one means the watermark comparison is broken — which is exactly how
# the UTC-vs-local-clock bug announced itself.
[ "$I_E" = "0" ] || die "increment carried $I_E entries when nothing had changed"
test -d "$WORK/inc/deletions" || die "an increment must carry a deletions table"

say "OK — both formats survive a full cycle into a clean system, media included"
