#!/usr/bin/env bash
# Scale run for dmart's LDAP directory face, under constraints meant to look
# like a small server rather than a developer laptop.
#
#   usage: bench/ldap-face-scale.sh <dmart binary> <sqlite|pg> <users> [soak-seconds]
#
#   CORES=4,5          taskset list for dmart; empty leaves it unpinned
#   MEM_DMART=768M     memory cap for dmart (systemd scope MemoryMax, no swap)
#   MEM_PG=1g          memory cap for the PostgreSQL container (pg only)
#   PG_CORES=6,7       taskset list for the PostgreSQL postmaster (pg only)
#   WORK=<dir>         work directory; defaults to one under ~/.cache, i.e. on
#                      disk — /tmp is tmpfs here, and a database in RAM is the
#                      flattering condition this script exists to avoid
#
# What it does:
#   1. Seeds through the API: the `mail` service account and a template user
#      WITHOUT directory fields, so the index tables stay empty.
#   2. Clones the template into N users in SQL, each with a hosted mailbox, one
#      alias, and the services mail+matrix (every third also gitea). Only the
#      users table is written: the next start finds the index empty and
#      DirectoryIndexRepair rebuilds it, which is timed.
#   3. Restarts dmart under the CPU and memory caps.
#   4. Runs each workload alone (bench/ldap-face-load.py), then all of them at
#      once for the soak, reporting per minute, while sampling memory.
#
# PostgreSQL runs in a throwaway container this script creates and removes.
# Results: bench/REPORT-ldap-face.md.
set -euo pipefail

BIN="$(readlink -f "${1:?usage: ldap-face-scale.sh <dmart binary> <sqlite|pg> <users> [soak-seconds]}")"
ENGINE="${2:?sqlite or pg}"
N="${3:?number of users}"
SOAK="${4:-600}"
CORES="${CORES:-}"
MEM_DMART="${MEM_DMART:-}"
MEM_PG="${MEM_PG:-1g}"
PG_CORES="${PG_CORES:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${WORK:-$HOME/.cache/dmart-ldap-scale/$ENGINE-$N}"
rm -rf "$WORK"; mkdir -p "$WORK"
HTTP_PORT=18383 LDAP_PORT=13489 PG_PORT=55433
API="http://127.0.0.1:$HTTP_PORT"
PW="Bench12345"
PG_NAME="dmart-ldap-scale-pg"
SERVER_PID=""

log() { printf '%s %s\n' "$(date +%T)" "$*"; }

cleanup() {
  [ -n "$SERVER_PID" ] && { kill "$SERVER_PID" 2>/dev/null || true; wait "$SERVER_PID" 2>/dev/null || true; }
  [ "$ENGINE" = pg ] && podman rm -f "$PG_NAME" >/dev/null 2>&1 || true
}
trap cleanup EXIT
trap 'exit 143' TERM INT   # so a stopped run still reaches the EXIT trap

# ------------------------------------------------------------------- database
if [ "$ENGINE" = pg ]; then
  PGW="$(head -c 24 /dev/urandom | base64 | tr -d '/+=')"
  podman rm -f "$PG_NAME" >/dev/null 2>&1 || true
  # Sized like a small box: 1 GB for PostgreSQL, a quarter of it shared_buffers.
  podman run -d --name "$PG_NAME" --memory "$MEM_PG" --memory-swap "$MEM_PG" \
    -e POSTGRES_USER=dmart -e POSTGRES_PASSWORD="$PGW" -e POSTGRES_DB=dmart_scale \
    -p "127.0.0.1:$PG_PORT:5432" docker.io/pgvector/pgvector:pg17 \
    -c shared_buffers=256MB -c effective_cache_size=768MB -c max_connections=100 >/dev/null
  until podman exec "$PG_NAME" pg_isready -U dmart -q 2>/dev/null; do sleep 1; done
  sleep 2
  if [ -n "$PG_CORES" ]; then
    # Backends inherit the postmaster's affinity. The container's cpuset
    # cannot be set rootless; the postmaster's affinity can.
    P=$(podman inspect -f '{{.State.Pid}}' "$PG_NAME")
    for c in $P $(pgrep -P "$P"); do taskset -apc "$PG_CORES" "$c" >/dev/null; done
  fi
  psql_() { podman exec -i -e PGPASSWORD="$PGW" "$PG_NAME" psql -q -U dmart -d dmart_scale -v ON_ERROR_STOP=1 "$@"; }
fi

{
  if [ "$ENGINE" = pg ]; then
    printf 'DATABASE_DRIVER="postgresql"\nDATABASE_HOST="127.0.0.1"\nDATABASE_PORT=%s\n' "$PG_PORT"
    printf 'DATABASE_USERNAME="dmart"\nDATABASE_PASSWORD="%s"\nDATABASE_NAME="dmart_scale"\n' "$PGW"
  else
    echo "DATABASE_DRIVER=\"sqlite\""
    echo "SQLITE_PATH=\"$WORK/dmart.db\""
  fi
  cat <<EOF
LISTENING_HOST="127.0.0.1"
LISTENING_PORT=$HTTP_PORT
JWT_SECRET="$(head -c 48 /dev/urandom | base64 | tr -d '/+=' | head -c 48)"
ADMIN_PASSWORD="Admin12345"
AUTH_RATE_LIMIT_PER_MINUTE=1000
LDAP_PORT=$LDAP_PORT
LDAP_BASE_DN="dc=bench"
LDAP_SERVICE_ACCOUNTS="mail"
LDAP_EXTRA_USER_OBJECT_CLASSES="freexPerson,freexUser"
EOF
} > "$WORK/config.env"
chmod 600 "$WORK/config.env"

start_server() {  # start_server <timeout-seconds> [constrained]
  local limit="$1" constrained="${2:-}"
  local cmd=("$BIN" serve)
  [ -n "$constrained" ] && [ -n "$CORES" ] && cmd=(taskset -c "$CORES" "${cmd[@]}")
  if [ -n "$constrained" ] && [ -n "$MEM_DMART" ]; then
    cmd=(systemd-run --user --scope --quiet -p "MemoryMax=$MEM_DMART" -p MemorySwapMax=0 -- "${cmd[@]}")
  fi
  BACKEND_ENV="$WORK/config.env" "${cmd[@]}" >> "$WORK/server.log" 2>&1 &
  SERVER_PID=$!
  local t0=$SECONDS
  until curl -sf "$API/" >/dev/null 2>&1 && (exec 3<>"/dev/tcp/127.0.0.1/$LDAP_PORT") 2>/dev/null; do
    kill -0 "$SERVER_PID" 2>/dev/null || { tail -20 "$WORK/server.log"; exit 1; }
    [ $((SECONDS - t0)) -gt "$limit" ] && { echo "server not up after ${limit}s"; exit 1; }
    sleep 0.5
  done
  STARTED_IN=$((SECONDS - t0))
}
stop_server() { kill "$SERVER_PID" 2>/dev/null || true; wait "$SERVER_PID" 2>/dev/null || true; SERVER_PID=""; }

# ------------------------------------------------------------------- seeding
start_server 120
TOKEN=$(curl -sf "$API/user/login" -H 'Content-Type: application/json' \
  -d '{"shortname":"dmart","password":"Admin12345"}' | jq -r '.records[0].attributes.access_token')
curl -sf "$API/managed/request" -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d '{
  "space_name":"management","request_type":"create","records":[
  {"resource_type":"user","subpath":"users","shortname":"mail","attributes":{"is_active":true,"type":"bot","password":"'"$PW"'"}},
  {"resource_type":"user","subpath":"users","shortname":"template","attributes":{"is_active":true,"password":"'"$PW"'"}}]}' \
  | jq -e '.status == "success"' >/dev/null || { echo "API seed failed"; exit 1; }
stop_server

log "seeding $N users ($ENGINE, workdir $WORK)"
T0=$SECONDS
if [ "$ENGINE" = pg ]; then
  psql_ <<SQL
INSERT INTO users (uuid, shortname, space_name, subpath, is_active, displayname, owner_shortname,
                   resource_type, password, roles, groups, type, language, email, is_email_verified,
                   force_password_change, query_policies, is_deleted, created_at, updated_at,
                   mailbox, mail_aliases, services)
SELECT gen_random_uuid(), 'u' || lpad(i::text, 7, '0'), t.space_name, t.subpath, true,
       jsonb_build_object('en', 'User ' || i), 'u' || lpad(i::text, 7, '0'),
       'user', t.password, '[]'::jsonb, '[]'::jsonb, t.type, t.language,
       'u' || lpad(i::text, 7, '0') || '@contact.test', true, false, t.query_policies, false,
       t.created_at, t.updated_at,
       'u' || lpad(i::text, 7, '0') || '@bench.test',
       jsonb_build_array('alias' || i || '@bench.test'),
       CASE WHEN i % 3 = 0 THEN '["mail","matrix","gitea"]'::jsonb ELSE '["mail","matrix"]'::jsonb END
FROM generate_series(1, $N) AS i, users t WHERE t.shortname = 'template';
ANALYZE users;
SQL
else
  python3 - "$WORK/dmart.db" "$N" <<'PY'
import sqlite3, sys
db, n = sys.argv[1], int(sys.argv[2])
c = sqlite3.connect(db)
c.execute(f"""
WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < {n})
INSERT INTO users (uuid, shortname, space_name, subpath, is_active, displayname, owner_shortname,
                   resource_type, password, roles, groups, type, language, email, is_email_verified,
                   force_password_change, query_policies, is_deleted, created_at, updated_at,
                   mailbox, mail_aliases, services)
SELECT lower(substr(h,1,8)||'-'||substr(h,9,4)||'-4'||substr(h,14,3)||'-a'||substr(h,18,3)||'-'||substr(h,21,12)),
       printf('u%07d', i), t.space_name, t.subpath, 1,
       json_object('en', 'User ' || i), printf('u%07d', i),
       'user', t.password, '[]', '[]', t.type, t.language,
       printf('u%07d@contact.test', i), 1, 0, t.query_policies, 0, t.created_at, t.updated_at,
       printf('u%07d@bench.test', i),
       json_array('alias' || i || '@bench.test'),
       CASE WHEN i % 3 = 0 THEN '["mail","matrix","gitea"]' ELSE '["mail","matrix"]' END
FROM (SELECT i, hex(randomblob(16)) AS h FROM s), users t WHERE t.shortname = 'template'
""")
c.commit()
c.execute("ANALYZE")
c.close()
PY
fi
log "seeded in $((SECONDS - T0))s"

start_server 1800 constrained
log "constrained start (includes rebuilding the directory index): ${STARTED_IN}s"
# Surfaced, not just logged: the PostgreSQL run once "started" in 32 s because
# the rebuild had timed out and given up, and every address lookup then missed.
grep -h -o 'Rebuilt the user directory index[^"]*\|rebuilding the user directory index failed' "$WORK/server.log" | tail -1 | sed 's/^/    /' || true
log "dmart on cores ${CORES:-any}, memory cap ${MEM_DMART:-none}; host: $(nproc --all) CPUs, $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs)"
[ "$ENGINE" = pg ] && log "postgres: memory cap $MEM_PG, cores ${PG_CORES:-any}, shared_buffers 256MB"
if [ "$ENGINE" = pg ]; then
  psql_ -Atc "SELECT 'db size ' || pg_size_pretty(pg_database_size('dmart_scale'))"
else
  log "db size $(du -h "$WORK/dmart.db" | cut -f1)"
fi

mem() {
  local m
  m=$(awk '/^Pss:/{p=$2} /^Pss_Anon:/{a=$2} END{printf "dmart PSS %d MiB (anon %d MiB)", p/1024, a/1024}' \
        "/proc/$(pgrep -f -n "$BIN serve" || echo "$SERVER_PID")/smaps_rollup" 2>/dev/null || echo "dmart ?")
  [ "$ENGINE" = pg ] && m="$m; postgres $(podman stats --no-stream --format '{{.MemUsage}}' "$PG_NAME" 2>/dev/null)"
  echo "$m"
}

load() { python3 "$HERE/ldap-face-load.py" --port "$LDAP_PORT" --base dc=bench --users "$N" --password "$PW" "$@"; }

log "== each workload alone"
load --workload uid  --conns 1 --seconds 10
load --workload uid  --conns 8 --seconds 10
load --workload mail --conns 8 --seconds 10
load --workload alias --conns 8 --seconds 10
load --workload bind --conns 8 --seconds 10
load --workload http --conns 4 --seconds 10 --http-port "$HTTP_PORT"
load --workload listing --conns 1 --seconds 1800 --listings 1 --page-size 500 | grep -E "^listing|^results|^workload"
log "memory: $(mem)"

log "== soak: everything at once for ${SOAK}s"
pids=()
load --workload mail    --conns 4 --seconds "$SOAK" --report-every 60 > "$WORK/soak-mail.txt" & pids+=($!)
load --workload alias   --conns 2 --seconds "$SOAK" --report-every 60 > "$WORK/soak-alias.txt" & pids+=($!)
load --workload uid     --conns 2 --seconds "$SOAK" --report-every 60 > "$WORK/soak-uid.txt" & pids+=($!)
load --workload bind    --conns 2 --seconds "$SOAK" --report-every 60 > "$WORK/soak-bind.txt" & pids+=($!)
load --workload http    --conns 2 --seconds "$SOAK" --report-every 60 --http-port "$HTTP_PORT" > "$WORK/soak-http.txt" & pids+=($!)
load --workload listing --conns 1 --seconds "$SOAK" --report-every 60 --page-size 500 > "$WORK/soak-listing.txt" & pids+=($!)
for _ in $(seq $((SOAK / 60))); do sleep 60; log "memory: $(mem)"; done
for p in "${pids[@]}"; do wait "$p" || true; done
for w in mail alias uid bind http listing; do
  echo "--- $w"; grep -E "^\[|^workload|^results|^listing" "$WORK/soak-$w.txt" | grep -v "^\[" ; grep "^\[" "$WORK/soak-$w.txt" | sed -n '1p;$p'
done
log "server errors during the run: $(grep -c -E '"LogLevel":"(Error|Critical)"' "$WORK/server.log" || true)"
log "workdir: $WORK"
