#!/usr/bin/env bash
# Smoke test for directory replication (docs/directory-replica.md), as i7 would
# use it: two dmart processes, a primary and a replica, each on its own SQLite
# file, and Dovecot 2.4 authenticating against the REPLICA's LDAP face.
#
#   1. the replica's first sync, then an IMAP login through it
#   2. a password changed on the primary reaches the replica
#   3. the primary is stopped: lookups and logins keep working
#
#   usage: bench/directory-replica-smoke.sh "<command that runs dmart>" [workdir]
#   e.g.   bench/directory-replica-smoke.sh "dotnet bin/Release/net10.0/dmart.dll"
#
# Needs the client image bench/ldap-face-interop.sh builds. Exit status is the
# number of failed checks.
set -uo pipefail

DMART="${1:?usage: directory-replica-smoke.sh \"<dmart command>\" [workdir]}"
WORK="${2:-$(mktemp -d -t replica-smoke-XXXXXX)}"
mkdir -p "$WORK"
WORK="$(cd "$WORK" && pwd)"

PRIMARY_PORT=18383
REPLICA_PORT=18384
LDAP_PORT=13499
BASE="dc=imx,dc=sh"
URI="ldap://127.0.0.1:$LDAP_PORT"
IMAGE="localhost/dmart-ldap-interop:43"
ADMIN_PW="Admin12345"
SVC_PW="Service12345"
FAILS=0

check() {
  if grep -qF -- "$2" <<<"$3"; then echo "  PASS  $1"
  else echo "  FAIL  $1"; echo "        expected '$2', got: $(head -c 400 <<<"$3")"; FAILS=$((FAILS + 1)); fi
}
in_box() { podman run --rm --network host -v "$WORK:/work:Z" "$IMAGE" bash -c "$1" 2>&1; }
podman image exists "$IMAGE" || { echo "run bench/ldap-face-interop.sh once first: it builds $IMAGE"; exit 100; }

config() {   # name http-port extra-lines
  cat > "$WORK/$1.env" <<EOF
DATABASE_DRIVER="sqlite"
SQLITE_PATH="$WORK/$1.db"
LISTENING_HOST="127.0.0.1"
LISTENING_PORT=$2
JWT_SECRET="$(head -c 48 /dev/urandom | base64 | tr -d '/+=' | head -c 48)"
ADMIN_PASSWORD="$ADMIN_PW"
$3
EOF
  chmod 600 "$WORK/$1.env"
}

config primary $PRIMARY_PORT 'DIRECTORY_FEED_READERS="replbot"
USER_SERVICES="mail"'
config replica $REPLICA_PORT "DIRECTORY_REPLICA_OF=\"http://127.0.0.1:$PRIMARY_PORT\"
DIRECTORY_REPLICA_SHORTNAME=\"replbot\"
DIRECTORY_REPLICA_PASSWORD=\"$SVC_PW\"
DIRECTORY_REPLICA_INTERVAL_SECONDS=2
LDAP_PORT=$LDAP_PORT
LDAP_HOST=\"127.0.0.1\"
LDAP_BASE_DN=\"$BASE\"
LDAP_SERVICE_ACCOUNTS=\"mail\"
LDAP_EXTRA_USER_OBJECT_CLASSES=\"freexPerson,freexUser\""

PIDS=()
start() {   # name -> appends pid
  BACKEND_ENV="$WORK/$1.env" $DMART serve > "$WORK/$1.log" 2>&1 &
  PIDS+=($!)
}
stop_all() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; wait "$p" 2>/dev/null; done; }
trap stop_all EXIT

wait_http() { for _ in $(seq 120); do curl -sf "http://127.0.0.1:$1/" >/dev/null 2>&1 && return 0; sleep 0.5; done; return 1; }

echo "workdir: $WORK"
start primary
PRIMARY_PID=${PIDS[0]}
wait_http $PRIMARY_PORT || { echo "primary did not start"; tail -20 "$WORK/primary.log"; exit 100; }

TOKEN=$(curl -sf "http://127.0.0.1:$PRIMARY_PORT/user/login" -H 'Content-Type: application/json' \
  -d "{\"shortname\":\"dmart\",\"password\":\"$ADMIN_PW\"}" | jq -r '.records[0].attributes.access_token')
curl -sf "http://127.0.0.1:$PRIMARY_PORT/managed/request" -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d '{
  "space_name":"management","request_type":"create","records":[
  {"resource_type":"user","subpath":"users","shortname":"replbot","attributes":{"is_active":true,"type":"bot","password":"'"$SVC_PW"'"}},
  {"resource_type":"user","subpath":"users","shortname":"mail","attributes":{"is_active":true,"type":"bot","password":"'"$SVC_PW"'"}},
  {"resource_type":"user","subpath":"users","shortname":"alice","attributes":{"is_active":true,"password":"Alice12345",
    "email":"alice@elsewhere.test","mailbox":"alice@imx.sh","mail_aliases":["postmaster@imx.sh"],"services":["mail"]}}]}' \
  | jq -e '.status == "success"' >/dev/null || { echo "seeding the primary failed"; exit 100; }

start replica
for _ in $(seq 120); do grep -q "in sync with the primary" "$WORK/replica.log" && break; sleep 0.5; done

cat > "$WORK/dovecot.conf" <<EOF
dovecot_config_version = 2.4.0
dovecot_storage_version = 2.4.0
protocols {
}
base_dir = /run/dovecot
auth_mechanisms = plain
passdb ldap {
  ldap_uris = $URI
  ldap_auth_dn = cn=mail,ou=services,$BASE
  ldap_auth_dn_password = $SVC_PW
  ldap_base = ou=people,$BASE
  ldap_scope = subtree
  bind = yes
  filter = (&(objectClass=freexUser)(isActive=TRUE)(authorizedService=mail)(mail=%{user}))
}
userdb static {
  fields {
    uid = 5000
    gid = 5000
    home = /tmp/%{user}
  }
}
log_path = /dev/stderr
EOF
cat > "$WORK/aliases.cf" <<EOF
server_host = $URI
version = 3
bind = yes
bind_dn = cn=mail,ou=services,$BASE
bind_pw = $SVC_PW
search_base = ou=people,$BASE
scope = sub
query_filter = (&(objectClass=freexUser)(isActive=TRUE)(authorizedService=mail)(mailAlias=%s))
result_attribute = mail
EOF
dove() { in_box "dovecot -c /work/dovecot.conf && sleep 1 && doveadm -c /work/dovecot.conf auth test $1 $2; echo rc=\$?"; }

echo
echo "== 1. first sync, then IMAP through the replica"
check "the replica completed its first sync" "in sync with the primary" "$(cat "$WORK/replica.log")"
check "Dovecot login for alice against the replica" "auth succeeded" "$(dove alice@imx.sh Alice12345)"
check "Postfix alias map against the replica" "alice@imx.sh" "$(in_box "postmap -q postmaster@imx.sh ldap:/work/aliases.cf")"

echo
echo "== 2. a password changed on the primary"
ATOKEN=$(curl -sf "http://127.0.0.1:$PRIMARY_PORT/user/login" -H 'Content-Type: application/json' \
  -d '{"shortname":"alice","password":"Alice12345"}' | jq -r '.records[0].attributes.access_token')
out=$(curl -s "http://127.0.0.1:$PRIMARY_PORT/user/profile" -H 'Content-Type: application/json' -H "Authorization: Bearer $ATOKEN" \
  -d '{"attributes":{"password":"Changed12345","old_password":"Alice12345"}}')
check "changed on the primary" '"status":"success"' "$out"
sleep 5
check "  ...the new one works on the replica" "auth succeeded" "$(dove alice@imx.sh Changed12345)"
check "  ...the old one does not" "auth failed" "$(dove alice@imx.sh Alice12345)"

echo
echo "== 3. the primary goes away"
kill "$PRIMARY_PID"; wait "$PRIMARY_PID" 2>/dev/null
sleep 5
check "Dovecot login still works" "auth succeeded" "$(dove alice@imx.sh Changed12345)"
check "the alias map still answers" "alice@imx.sh" "$(in_box "postmap -q postmaster@imx.sh ldap:/work/aliases.cf")"
check "the replica says why it cannot sync" "still serving the copy" "$(cat "$WORK/replica.log")"

echo
echo "failed checks: $FAILS"
exit "$FAILS"
