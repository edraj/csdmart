#!/usr/bin/env bash
# Interop check for dmart's LDAP directory face (Ldap/).
#
# Boots a scratch dmart on SQLite, seeds it through dmart's own API, and points
# the REAL clients of matrix-deploy's directory at it, with the filters those
# roles actually send:
#
#   ldapsearch / ldapwhoami / ldapmodify   (openldap-clients, i.e. libldap)
#   Postfix   postmap -q against the three ldap:*.cf maps   (postfix-ldap)
#   Dovecot   doveadm auth test through `passdb ldap { bind = yes }`
#   Dex       the LDAP connector, driven by an OAuth password grant
#   Gitea     a BindDN LDAP auth source, driven by API basic auth
#
# The clients run in containers on the host network, from the same Fedora
# packages i7 runs (dovecot 2.4, postfix-ldap 3.10, openldap 2.6) and the Dex
# and Gitea versions i1 pins. Nothing here touches a real directory.
#
#   usage: bench/ldap-face-interop.sh "<command that runs dmart>" [workdir]
#   e.g.   bench/ldap-face-interop.sh "dotnet bin/Release/net10.0/dmart.dll"
#
# Exit status is the number of failed checks.
set -uo pipefail

DMART="${1:?usage: ldap-face-interop.sh \"<dmart command>\" [workdir]}"
WORK="${2:-$(mktemp -d -t ldap-face-XXXXXX)}"
mkdir -p "$WORK"
WORK="$(cd "$WORK" && pwd)"

HTTP_PORT="${HTTP_PORT:-18282}"
LDAP_PORT="${LDAP_PORT:-13389}"
DEX_PORT="${DEX_PORT:-15556}"
GITEA_PORT="${GITEA_PORT:-13000}"
BASE="dc=imx,dc=sh"
URI="ldap://127.0.0.1:$LDAP_PORT"
API="http://127.0.0.1:$HTTP_PORT"
ADMIN_PW="Admin12345"
SVC_PW="Service12345"
IMAGE="localhost/dmart-ldap-interop:43"
DEX_IMAGE="ghcr.io/dexidp/dex:v2.45.1"
GITEA_IMAGE="docker.io/gitea/gitea:1.27.3-rootless"

FAILS=0
pass() { printf '  PASS  %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; [ -n "${2:-}" ] && printf '        %s\n' "$2"; FAILS=$((FAILS + 1)); }
# check <name> <expected-substring> <actual>
check() { if grep -qF -- "$2" <<<"$3"; then pass "$1"; else fail "$1" "expected '$2', got: $(head -c 400 <<<"$3")"; fi; }
check_not() { if grep -qF -- "$2" <<<"$3"; then fail "$1" "did not expect '$2'"; else pass "$1"; fi; }

# ---------------------------------------------------------------- the server
cat > "$WORK/config.env" <<EOF
DATABASE_DRIVER="sqlite"
SQLITE_PATH="$WORK/dmart.db"
LISTENING_HOST="127.0.0.1"
LISTENING_PORT=$HTTP_PORT
JWT_SECRET="$(head -c 48 /dev/urandom | base64 | tr -d '/+=' | head -c 48)"
ADMIN_PASSWORD="$ADMIN_PW"
LDAP_PORT=$LDAP_PORT
LDAP_HOST="127.0.0.1"
LDAP_BASE_DN="$BASE"
LDAP_SERVICE_ACCOUNTS="dex,mail,gitea"
LDAP_EXTRA_USER_OBJECT_CLASSES="freexPerson,freexUser"
EOF
chmod 600 "$WORK/config.env"

echo "workdir: $WORK"
# A separate statement, so $! is the server and not a subshell around it.
BACKEND_ENV="$WORK/config.env" $DMART serve > "$WORK/server.log" 2>&1 &
SERVER_PID=$!
stop_server() {
  kill "$SERVER_PID" 2>/dev/null
  wait "$SERVER_PID" 2>/dev/null
}
trap stop_server EXIT

for _ in $(seq 120); do
  curl -sf "$API/" >/dev/null 2>&1 && (exec 3<>"/dev/tcp/127.0.0.1/$LDAP_PORT") 2>/dev/null && break
  kill -0 "$SERVER_PID" 2>/dev/null || { echo "server exited:"; tail -20 "$WORK/server.log"; exit 100; }
  sleep 0.5
done

# ------------------------------------------------------------------ the seed
TOKEN=$(curl -sf "$API/user/login" -H 'Content-Type: application/json' \
  -d "{\"shortname\":\"dmart\",\"password\":\"$ADMIN_PW\"}" | jq -r '.records[0].attributes.access_token')
[ -n "$TOKEN" ] && [ "$TOKEN" != null ] || { echo "admin login failed"; exit 100; }

managed() {
  curl -sf "$API/managed/request" -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d "$1"
}
seed() {
  local out; out=$(managed "$1")
  jq -e '.status == "success"' >/dev/null <<<"$out" || { echo "seed failed: $out"; exit 100; }
}

seed '{"space_name":"management","request_type":"create","records":[
  {"resource_type":"group","subpath":"groups","shortname":"matrix","attributes":{"is_active":true,"displayname":{"en":"Matrix chat"}}},
  {"resource_type":"group","subpath":"groups","shortname":"mail","attributes":{"is_active":true,"displayname":{"en":"Mail"}}},
  {"resource_type":"group","subpath":"groups","shortname":"gitea","attributes":{"is_active":true,"displayname":{"en":"Gitea"}}}]}'

# alice: everything. bob: deactivated. carol: active but granted nothing.
# The three service accounts are bots, which dmart exempts from the lockout.
seed '{"space_name":"management","request_type":"create","records":[
  {"resource_type":"user","subpath":"users","shortname":"alice","attributes":{
    "is_active":true,"password":"Alice12345","email":"alice@imx.sh","is_email_verified":true,
    "msisdn":"9647701234567","displayname":{"en":"Alice Example"},"groups":["matrix","mail","gitea"],
    "payload":{"content_type":"json","body":{"mail_aliases":["postmaster@imx.sh"]}}}},
  {"resource_type":"user","subpath":"users","shortname":"bob","attributes":{
    "is_active":false,"password":"Bob1234567","email":"bob@imx.sh","displayname":{"en":"Bob Disabled"},"groups":["matrix","mail"]}},
  {"resource_type":"user","subpath":"users","shortname":"carol","attributes":{
    "is_active":true,"password":"Carol12345","email":"carol@imx.sh","displayname":{"en":"Carol NoServices"},"groups":[]}},
  {"resource_type":"user","subpath":"users","shortname":"dex","attributes":{"is_active":true,"type":"bot","password":"'"$SVC_PW"'"}},
  {"resource_type":"user","subpath":"users","shortname":"mail","attributes":{"is_active":true,"type":"bot","password":"'"$SVC_PW"'"}},
  {"resource_type":"user","subpath":"users","shortname":"gitea","attributes":{"is_active":true,"type":"bot","password":"'"$SVC_PW"'"}}]}'

# ------------------------------------------------------------- client image
if ! podman image exists "$IMAGE"; then
  echo "building $IMAGE (one-off)..."
  printf 'FROM registry.fedoraproject.org/fedora:43\nRUN dnf -y -q install openldap-clients postfix postfix-ldap dovecot && dnf clean all\n' \
    | podman build -q -t "$IMAGE" -f - "$WORK" >"$WORK/image-build.log" 2>&1 \
    || { echo "image build failed: $WORK/image-build.log"; exit 100; }
fi
in_box() { podman run --rm --network host -v "$WORK:/work:Z" "$IMAGE" bash -c "$1" 2>&1; }

DEX_DN="cn=dex,ou=services,$BASE"
MAIL_DN="cn=mail,ou=services,$BASE"
DEX_FILTER="(&(objectClass=freexUser)(isActive=TRUE)(authorizedService=matrix))"

echo
echo "== libldap (ldapsearch / ldapwhoami / ldapmodify)"
out=$(in_box "ldapsearch -LLL -x -H $URI -b '' -s base '(objectClass=*)' namingContexts supportedControl")
check "root DSE, anonymous" "namingContexts: $BASE" "$out"

out=$(in_box "ldapwhoami -x -H $URI -D uid=alice,ou=people,$BASE -w Alice12345")
check "user bind + Who Am I" "dn:uid=alice,ou=people,$BASE" "$out"

out=$(in_box "ldapwhoami -x -H $URI -D uid=alice,ou=people,$BASE -w wrong; echo rc=\$?")
check "wrong password is invalidCredentials" "rc=49" "$out"

out=$(in_box "ldapwhoami -x -H $URI -D uid=bob,ou=people,$BASE -w Bob1234567; echo rc=\$?")
check "deactivated user cannot bind" "rc=49" "$out"

out=$(in_box "ldapwhoami -x -H $URI -D uid=nobody,ou=people,$BASE -w whatever; echo rc=\$?")
check "unknown DN is invalidCredentials" "rc=49" "$out"

out=$(in_box "ldapwhoami -x -H $URI -D uid=alice,ou=people,$BASE; echo rc=\$?")
check "DN without password is refused" "rc=53" "$out"

out=$(in_box "ldapsearch -LLL -x -H $URI -D $DEX_DN -w $SVC_PW -b ou=people,$BASE '(&$DEX_FILTER(uid=alice))'")
check "Dex filter finds alice" "dn: uid=alice,ou=people,$BASE" "$out"
check "  ...with the freex attributes" "authorizedService: matrix" "$out"
check_not "  ...and never a password" "userPassword" "$out"

out=$(in_box "ldapsearch -LLL -x -H $URI -D $DEX_DN -w $SVC_PW -b ou=people,$BASE '$DEX_FILTER' uid")
check "Dex filter over everyone: alice" "uid: alice" "$out"
check_not "Dex filter excludes deactivated bob" "uid: bob" "$out"
check_not "Dex filter excludes unauthorized carol" "uid: carol" "$out"
check_not "service accounts are not people" "uid: dex" "$out"

out=$(in_box "ldapsearch -LLL -x -H $URI -D uid=alice,ou=people,$BASE -w Alice12345 -b $BASE '(objectClass=*)' dn")
check "a user sees their own entry" "uid=alice" "$out"
check_not "  ...and nobody else's" "uid=carol" "$out"

out=$(in_box "ldapsearch -LLL -x -H $URI -b ou=people,$BASE '(uid=alice)'; echo rc=\$?")
check "anonymous cannot search the tree" "rc=50" "$out"

out=$(in_box "ldapsearch -LLL -x -H $URI -D $DEX_DN -w $SVC_PW -b ou=people,$BASE -E pr=1/noprompt '(objectClass=person)' uid")
check "paged results deliver every page" "uid: carol" "$out"

out=$(in_box "ldapsearch -LLL -x -H $URI -D $DEX_DN -w $SVC_PW -b ou=groups,$BASE '(member=uid=alice,ou=people,$BASE)' cn member")
check "group membership by member DN" "cn: gitea" "$out"

out=$(in_box "ldapsearch -LLL -x -H $URI -D $DEX_DN -w $SVC_PW -b uid=alice,ou=people,$BASE -s base '(objectClass=*)' memberOf")
check "memberOf on request" "memberOf: cn=mail,ou=groups,$BASE" "$out"

out=$(in_box "ldapsearch -LLL -x -H $URI -D $DEX_DN -w $SVC_PW -b uid=zed,ou=people,$BASE -s base; echo rc=\$?")
check "missing entry is noSuchObject" "rc=32" "$out"

out=$(in_box "printf 'dn: uid=alice,ou=people,$BASE\nchangetype: modify\nreplace: mail\nmail: x@y.z\n' | ldapmodify -x -H $URI -D $DEX_DN -w $SVC_PW; echo rc=\$?")
check "writes are refused" "rc=53" "$out"

echo
echo "== Postfix (postmap -q, the matrix-deploy maps)"
for map in mailboxes aliases; do
  case $map in
    mailboxes) filter="(&(objectClass=freexUser)(isActive=TRUE)(authorizedService=mail)(mail=%s))"; extra="result_format = %s/" ;;
    aliases)   filter="(&(objectClass=freexUser)(isActive=TRUE)(authorizedService=mail)(mailAlias=%s))"; extra="" ;;
  esac
  cat > "$WORK/ldap-$map.cf" <<EOF
server_host = $URI
version = 3
bind = yes
bind_dn = $MAIL_DN
bind_pw = $SVC_PW
search_base = ou=people,$BASE
scope = sub
query_filter = $filter
result_attribute = mail
$extra
EOF
done
out=$(in_box "postmap -q alice@imx.sh ldap:/work/ldap-mailboxes.cf")
check "mailbox lookup" "alice@imx.sh/" "$out"
out=$(in_box "postmap -q bob@imx.sh ldap:/work/ldap-mailboxes.cf; echo rc=\$?")
check "no mailbox for deactivated bob" "rc=1" "$out"
out=$(in_box "postmap -q postmaster@imx.sh ldap:/work/ldap-aliases.cf")
check "alias lookup (payload mail_aliases)" "alice@imx.sh" "$out"

echo
echo "== Dovecot 2.4 (passdb ldap, bind = yes)"
cat > "$WORK/dovecot.conf" <<EOF
dovecot_config_version = 2.4.0
dovecot_storage_version = 2.4.0
protocols {
}
base_dir = /run/dovecot
auth_mechanisms = plain
passdb ldap {
  ldap_uris = $URI
  ldap_auth_dn = $MAIL_DN
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
dove() {
  in_box "dovecot -c /work/dovecot.conf && sleep 1 && doveadm -c /work/dovecot.conf auth test $1 $2; echo rc=\$?"
}
out=$(dove alice@imx.sh Alice12345)
check "IMAP login for alice" "auth succeeded" "$out"
out=$(dove alice@imx.sh wrong)
check "wrong password rejected" "auth failed" "$out"
out=$(dove carol@imx.sh Carol12345)
check "no mail service for carol" "auth failed" "$out"

echo
echo "== Dex $DEX_IMAGE (LDAP connector, password grant)"
cat > "$WORK/dex.yaml" <<EOF
issuer: http://127.0.0.1:$DEX_PORT/dex
storage:
  type: memory
web:
  http: 127.0.0.1:$DEX_PORT
oauth2:
  skipApprovalScreen: true
  passwordConnector: ldap
connectors:
  - type: ldap
    id: ldap
    name: LDAP
    config:
      host: 127.0.0.1:$LDAP_PORT
      insecureNoSSL: true
      bindDN: "$DEX_DN"
      bindPW: "$SVC_PW"
      usernamePrompt: Username
      userSearch:
        baseDN: "ou=people,$BASE"
        filter: "$DEX_FILTER"
        username: uid
        idAttr: uid
        emailAttr: mail
        nameAttr: displayName
        preferredUsernameAttr: uid
staticClients:
  - id: interop
    name: Interop
    secret: interop-secret
    redirectURIs: ["http://127.0.0.1/cb"]
EOF
podman rm -f dmart-ldap-dex >/dev/null 2>&1
podman run -d --name dmart-ldap-dex --network host -v "$WORK/dex.yaml:/etc/dex/config.yaml:ro,Z" \
  "$DEX_IMAGE" dex serve /etc/dex/config.yaml >/dev/null
for _ in $(seq 60); do curl -sf "http://127.0.0.1:$DEX_PORT/dex/.well-known/openid-configuration" >/dev/null && break; sleep 0.5; done
grant() {
  curl -s -u interop:interop-secret "http://127.0.0.1:$DEX_PORT/dex/token" \
    -d grant_type=password -d "username=$1" -d "password=$2" -d 'scope=openid email profile'
}
out=$(grant alice Alice12345)
if jq -e '.id_token' >/dev/null 2>&1 <<<"$out"; then
  claims=$(jq -r '.id_token' <<<"$out" | cut -d. -f2 | tr '_-' '/+' | base64 -d 2>/dev/null)
  check "Dex issues an ID token for alice" '"email":"alice@imx.sh"' "$claims"
else
  fail "Dex issues an ID token for alice" "$out"
fi
out=$(grant bob Bob1234567)
check "Dex refuses deactivated bob" "access_denied" "$out"
out=$(grant carol Carol12345)
check "Dex refuses carol (no matrix service)" "access_denied" "$out"
podman logs dmart-ldap-dex > "$WORK/dex.log" 2>&1
podman rm -f dmart-ldap-dex >/dev/null 2>&1

echo
echo "== Gitea $GITEA_IMAGE (LDAP via BindDN source, matrix-deploy's flags)"
podman rm -f dmart-ldap-gitea >/dev/null 2>&1
podman run -d --name dmart-ldap-gitea --network host \
  -e GITEA__security__INSTALL_LOCK=true \
  -e GITEA__server__HTTP_ADDR=127.0.0.1 -e GITEA__server__HTTP_PORT="$GITEA_PORT" \
  -e GITEA__server__ROOT_URL="http://127.0.0.1:$GITEA_PORT/" -e GITEA__server__DISABLE_SSH=true \
  -e GITEA__database__DB_TYPE=sqlite3 \
  "$GITEA_IMAGE" >/dev/null
for _ in $(seq 120); do curl -sf "http://127.0.0.1:$GITEA_PORT/api/healthz" >/dev/null && break; sleep 0.5; done
podman exec dmart-ldap-gitea gitea admin auth add-ldap \
  --name dmart --security-protocol unencrypted --host 127.0.0.1 --port "$LDAP_PORT" \
  --user-search-base "ou=people,$BASE" \
  --user-filter "(&(objectClass=freexUser)(isActive=TRUE)(authorizedService=gitea)(uid=%s))" \
  --username-attribute uid --firstname-attribute givenName --surname-attribute sn --email-attribute mail \
  --bind-dn "cn=gitea,ou=services,$BASE" --bind-password "$SVC_PW" --synchronize-users \
  > "$WORK/gitea-auth.log" 2>&1 || fail "gitea admin auth add-ldap" "$(cat "$WORK/gitea-auth.log")"
out=$(curl -s -u alice:Alice12345 "http://127.0.0.1:$GITEA_PORT/api/v1/user")
check "Gitea logs alice in, creating her account" '"login":"alice"' "$out"
check "  ...with her mail from the directory" '"email":"alice@imx.sh"' "$out"
out=$(curl -s -o /dev/null -w '%{http_code}' -u carol:Carol12345 "http://127.0.0.1:$GITEA_PORT/api/v1/user")
check "Gitea refuses carol (no gitea service)" "401" "$out"
podman logs dmart-ldap-gitea > "$WORK/gitea.log" 2>&1
podman rm -f dmart-ldap-gitea >/dev/null 2>&1

echo
echo "server log: $WORK/server.log"
echo "failed checks: $FAILS"
exit "$FAILS"
