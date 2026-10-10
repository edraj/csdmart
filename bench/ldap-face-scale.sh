#!/usr/bin/env bash
# Scale check for dmart's LDAP directory face: N users, the queries real
# clients send, on a server pinned to few cores.
#
# Seeds through the API first — groups, the `mail` service account and one
# template user — so every column dmart expects is filled the way dmart fills
# it, then clones the template N times in SQL. Then runs
# bench/ldap-face-load.py for each workload. Results: bench/REPORT-ldap-face.md.
#
#   usage: bench/ldap-face-scale.sh <dmart binary> <sqlite|pg> <users> [cores]
#   cores is a taskset list (e.g. 4,5); omit it to leave the server unpinned.
#   pg needs PG_CONN_ARGS="host port user password db" for a THROWAWAY database
#   in the `ldapspike-pg` container.
set -euo pipefail

BIN="$(readlink -f "${1:?usage: ldap-face-scale.sh <dmart binary> <sqlite|pg> <users> [cores]}")"
ENGINE="${2:?sqlite or pg}"
N="${3:?number of users}"
CORES="${4:-}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d -t ldap-scale-XXXXXX)"
HTTP_PORT=18383 LDAP_PORT=13489
API="http://127.0.0.1:$HTTP_PORT"
PW="Bench12345"

{
  echo "DATABASE_DRIVER=\"$([ "$ENGINE" = pg ] && echo postgresql || echo sqlite)\""
  if [ "$ENGINE" = pg ]; then
    read -r PGH PGP PGU PGW PGD <<<"${PG_CONN_ARGS:?set PG_CONN_ARGS}"
    printf 'DATABASE_HOST="%s"\nDATABASE_PORT=%s\nDATABASE_USERNAME="%s"\nDATABASE_PASSWORD="%s"\nDATABASE_NAME="%s"\n' \
      "$PGH" "$PGP" "$PGU" "$PGW" "$PGD"
  else
    echo "SQLITE_PATH=\"$WORK/dmart.db\""
  fi
  cat <<EOF
LISTENING_HOST="127.0.0.1"
LISTENING_PORT=$HTTP_PORT
JWT_SECRET="$(head -c 48 /dev/urandom | base64 | tr -d '/+=' | head -c 48)"
ADMIN_PASSWORD="Admin12345"
LDAP_PORT=$LDAP_PORT
LDAP_BASE_DN="dc=bench"
LDAP_SERVICE_ACCOUNTS="mail"
LDAP_EXTRA_USER_OBJECT_CLASSES="freexPerson,freexUser"
EOF
} > "$WORK/config.env"
chmod 600 "$WORK/config.env"

start_server() {
  if [ -n "$CORES" ]; then
    BACKEND_ENV="$WORK/config.env" taskset -c "$CORES" "$BIN" serve >> "$WORK/server.log" 2>&1 &
  else
    BACKEND_ENV="$WORK/config.env" "$BIN" serve >> "$WORK/server.log" 2>&1 &
  fi
  SERVER_PID=$!
  for _ in $(seq 240); do
    curl -sf "$API/" >/dev/null 2>&1 && (exec 3<>"/dev/tcp/127.0.0.1/$LDAP_PORT") 2>/dev/null && return 0
    kill -0 "$SERVER_PID" 2>/dev/null || { tail -20 "$WORK/server.log"; exit 1; }
    sleep 0.5
  done
  echo "server never came up"; exit 1
}
stop_server() { kill "$SERVER_PID" 2>/dev/null || true; wait "$SERVER_PID" 2>/dev/null || true; }
trap stop_server EXIT

start_server
TOKEN=$(curl -sf "$API/user/login" -H 'Content-Type: application/json' \
  -d '{"shortname":"dmart","password":"Admin12345"}' | jq -r '.records[0].attributes.access_token')
req() { curl -sf "$API/managed/request" -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d "$1" \
  | jq -e '.status == "success"' >/dev/null || { echo "seed failed: $1"; exit 1; }; }
req '{"space_name":"management","request_type":"create","records":[
  {"resource_type":"group","subpath":"groups","shortname":"mail","attributes":{"is_active":true}},
  {"resource_type":"group","subpath":"groups","shortname":"matrix","attributes":{"is_active":true}}]}'
req '{"space_name":"management","request_type":"create","records":[
  {"resource_type":"user","subpath":"users","shortname":"mail","attributes":{"is_active":true,"type":"bot","password":"'"$PW"'"}},
  {"resource_type":"user","subpath":"users","shortname":"template","attributes":{"is_active":true,"password":"'"$PW"'",
    "email":"template@bench.test","is_email_verified":true,"groups":["mail","matrix"]}}]}'
stop_server

echo "seeding $N users ($ENGINE)..."
T0=$(date +%s)
if [ "$ENGINE" = pg ]; then
  PGPASSWORD="$PGW" podman exec -i -e PGPASSWORD="$PGW" ldapspike-pg psql -q -U "$PGU" -d "$PGD" -v ON_ERROR_STOP=1 <<SQL
INSERT INTO users (uuid, shortname, space_name, subpath, is_active, displayname, owner_shortname, payload,
                   resource_type, password, roles, groups, type, language, email, is_email_verified,
                   force_password_change, query_policies, is_deleted, created_at, updated_at)
SELECT gen_random_uuid(), 'u' || lpad(i::text, 7, '0'), t.space_name, t.subpath, true,
       jsonb_build_object('en', 'User ' || i), 'u' || lpad(i::text, 7, '0'),
       jsonb_build_object('content_type', 'json', 'body',
                          jsonb_build_object('mail_aliases', jsonb_build_array('alias' || i || '@bench.test'))),
       'user', t.password, '[]'::jsonb, t.groups, t.type, t.language,
       'u' || lpad(i::text, 7, '0') || '@bench.test', true, false, t.query_policies, false, t.created_at, t.updated_at
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
INSERT INTO users (uuid, shortname, space_name, subpath, is_active, displayname, owner_shortname, payload,
                   resource_type, password, roles, groups, type, language, email, is_email_verified,
                   force_password_change, query_policies, is_deleted, created_at, updated_at)
SELECT lower(substr(h,1,8)||'-'||substr(h,9,4)||'-4'||substr(h,14,3)||'-a'||substr(h,18,3)||'-'||substr(h,21,12)),
       printf('u%07d', i), t.space_name, t.subpath, 1,
       json_object('en', 'User ' || i), printf('u%07d', i),
       json_object('content_type', 'json', 'body', json_object('mail_aliases', json_array('alias' || i || '@bench.test'))),
       'user', t.password, '[]', t.groups, t.type, t.language,
       printf('u%07d@bench.test', i), 1, 0, t.query_policies, 0, t.created_at, t.updated_at
FROM (SELECT i, hex(randomblob(16)) AS h FROM s), users t WHERE t.shortname = 'template'
""")
c.commit()
c.execute("ANALYZE")
c.close()
PY
fi
echo "seeded in $(( $(date +%s) - T0 ))s"

start_server
echo "server on cores ${CORES:-any} ($(nproc --all) online, $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs))"
load() { python3 "$HERE/ldap-face-load.py" --port "$LDAP_PORT" --base dc=bench --users "$N" --password "$PW" "$@"; }
load --workload uid  --conns 1 --seconds 10
load --workload uid  --conns 8 --seconds 10
load --workload mail --conns 8 --seconds 10
load --workload bind --conns 8 --seconds 10
load --workload alias --conns 1 --seconds 20
# PSS, not RSS. Every pooled SQLite connection maps the same database file
# (mmap_size), and RSS counts those shared pages once per mapping: a server
# with 418 MiB of PSS reported 1.8 GiB of RSS here. Anonymous memory is the
# process's own heap; the rest is the database file.
awk '/^Pss:/{p=$2} /^Pss_Anon:/{a=$2} END{printf "server memory after the run: PSS %d MiB, of which anonymous %d MiB\n", p/1024, a/1024}' \
  "/proc/$SERVER_PID/smaps_rollup"
echo "workdir: $WORK"
