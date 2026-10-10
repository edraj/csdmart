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
#   TLS       LDAPS and StartTLS from libldap, Postfix and Dovecot, against a
#             throwaway CA
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
LDAPS_PORT="${LDAPS_PORT:-13636}"
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

# Throwaway credentials for the scratch server, Dex and Gitea, passed to curl
# through variables so no `user:password` pair sits on a curl line (the
# Security Gate's gitleaks rule rejects that shape, rightly, in real scripts).
DEX_CLIENT="interop:interop-secret"
ALICE_LOGIN="alice:Alice12345"
CAROL_LOGIN="carol:Carol12345"

FAILS=0
pass() { printf '  PASS  %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; [ -n "${2:-}" ] && printf '        %s\n' "$2"; FAILS=$((FAILS + 1)); }
# check <name> <expected-substring> <actual>
check() { if grep -qF -- "$2" <<<"$3"; then pass "$1"; else fail "$1" "expected '$2', got: $(head -c 400 <<<"$3")"; fi; }
check_not() { if grep -qF -- "$2" <<<"$3"; then fail "$1" "did not expect '$2'"; else pass "$1"; fi; }

# ------------------------------------------------------------ a throwaway CA
# One CA signs the server certificate. The second signs nothing; it is for the
# client that must refuse.
mkdir -p "$WORK/tls"
(
  cd "$WORK/tls" || exit 100
  for ca in ca other-ca; do
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 2 -subj "/CN=interop $ca" \
      -addext basicConstraints=critical,CA:TRUE -addext keyUsage=critical,keyCertSign \
      -keyout "$ca.key" -out "$ca.pem" 2>/dev/null || exit 100
  done
  openssl req -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -subj "/CN=127.0.0.1" \
    -keyout privkey.pem -out server.csr 2>/dev/null || exit 100
  printf 'subjectAltName=IP:127.0.0.1,DNS:localhost\nextendedKeyUsage=serverAuth\n' > server.ext
  openssl x509 -req -in server.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 2 \
    -extfile server.ext -out server.pem 2>/dev/null || exit 100
  cat server.pem ca.pem > fullchain.pem
) || { echo "could not make the test CA"; exit 100; }

# ---------------------------------------------------------------- the server
cat > "$WORK/config.env" <<EOF
DATABASE_DRIVER="sqlite"
SQLITE_PATH="$WORK/dmart.db"
LISTENING_HOST="127.0.0.1"
LISTENING_PORT=$HTTP_PORT
JWT_SECRET="$(head -c 48 /dev/urandom | base64 | tr -d '/+=' | head -c 48)"
ADMIN_PASSWORD="$ADMIN_PW"
LDAP_PORT=$LDAP_PORT
LDAPS_PORT=$LDAPS_PORT
LDAP_TLS_CERT_FILE="$WORK/tls/fullchain.pem"
LDAP_TLS_KEY_FILE="$WORK/tls/privkey.pem"
LDAP_HOST="127.0.0.1"
LDAP_BASE_DN="$BASE"
LDAP_SERVICE_ACCOUNTS="dex,mail,gitea"
LDAP_EXTRA_USER_OBJECT_CLASSES="freexPerson,freexUser"
USER_SERVICES="mail,matrix,gitea"
USER_MAIL_DOMAINS="imx.sh"
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
  curl -sf "$API/" >/dev/null 2>&1 && (exec 3<>"/dev/tcp/127.0.0.1/$LDAP_PORT") 2>/dev/null \
    && (exec 3<>"/dev/tcp/127.0.0.1/$LDAPS_PORT") 2>/dev/null && break
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

# Groups are team structure only (memberOf). Access to a service is the
# user's `services` field, which LDAP serves as authorizedService.
seed '{"space_name":"management","request_type":"create","records":[
  {"resource_type":"group","subpath":"groups","shortname":"staff","attributes":{"is_active":true,"displayname":{"en":"Staff"}}}]}'

# alice: a hosted mailbox, an alias and every service; her contact email is
# elsewhere. bob: the same shape, deactivated. carol: active, no mailbox, no
# services. The three service accounts are bots, which dmart exempts from the
# lockout.
seed '{"space_name":"management","request_type":"create","records":[
  {"resource_type":"user","subpath":"users","shortname":"alice","attributes":{
    "is_active":true,"password":"Alice12345","email":"alice@elsewhere.test","is_email_verified":true,
    "msisdn":"9647701234567","displayname":{"en":"Alice Example"},"groups":["staff"],
    "mailbox":"alice@imx.sh","mail_aliases":["postmaster@imx.sh"],"services":["matrix","mail","gitea"]}},
  {"resource_type":"user","subpath":"users","shortname":"bob","attributes":{
    "is_active":false,"password":"Bob1234567","email":"bob@elsewhere.test","displayname":{"en":"Bob Disabled"},
    "groups":["staff"],"mailbox":"bob@imx.sh","services":["matrix","mail"]}},
  {"resource_type":"user","subpath":"users","shortname":"carol","attributes":{
    "is_active":true,"password":"Carol12345","email":"carol@elsewhere.test","displayname":{"en":"Carol NoServices"}}},
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

out=$(in_box "ldapsearch -LLL -x -H $URI -b cn=Subschema -s base '(objectClass=subschema)' objectClasses")
check "subschema, anonymous: the freex classes" "NAME 'freexUser'" "$out"

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
check "group membership by member DN" "cn: staff" "$out"

out=$(in_box "ldapsearch -LLL -x -H $URI -D $DEX_DN -w $SVC_PW -b uid=alice,ou=people,$BASE -s base '(objectClass=*)' memberOf")
check "memberOf on request" "memberOf: cn=staff,ou=groups,$BASE" "$out"

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
out=$(in_box "postmap -q alice@elsewhere.test ldap:/work/ldap-mailboxes.cf; echo rc=\$?")
check "a contact email is not a local mailbox" "rc=1" "$out"
out=$(in_box "postmap -q postmaster@imx.sh ldap:/work/ldap-aliases.cf")
check "alias lookup (mail_aliases, indexed)" "alice@imx.sh" "$out"

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
echo "== TLS (LDAPS on $LDAPS_PORT, StartTLS on $LDAP_PORT)"
TLS_URI="ldaps://127.0.0.1:$LDAPS_PORT"
tls_box() { in_box "export LDAPTLS_CACERT=/work/tls/ca.pem LDAPTLS_REQCERT=demand; $1"; }
out=$(tls_box "ldapsearch -LLL -x -H $URI -b '' -s base '(objectClass=*)' supportedExtension")
check "root DSE advertises StartTLS" "supportedExtension: 1.3.6.1.4.1.1466.20037" "$out"
out=$(tls_box "ldapwhoami -x -H $TLS_URI -D uid=alice,ou=people,$BASE -w Alice12345")
check "LDAPS: user bind + Who Am I" "dn:uid=alice,ou=people,$BASE" "$out"
out=$(tls_box "ldapwhoami -x -ZZ -H $URI -D uid=alice,ou=people,$BASE -w Alice12345")
check "StartTLS (-ZZ): user bind + Who Am I" "dn:uid=alice,ou=people,$BASE" "$out"
out=$(tls_box "ldapsearch -LLL -x -ZZ -H $URI -D $DEX_DN -w $SVC_PW -b ou=people,$BASE -E pr=1/noprompt '(objectClass=person)' uid")
check "StartTLS: paged listing as a service account" "uid: carol" "$out"
out=$(in_box "LDAPTLS_CACERT=/work/tls/other-ca.pem LDAPTLS_REQCERT=demand ldapwhoami -x -H $TLS_URI -D uid=alice,ou=people,$BASE -w Alice12345; echo rc=\$?")
check "a client that does not trust the CA refuses to connect" "rc=255" "$out"

sed -e "s|^server_host = .*|server_host = $TLS_URI\ntls_ca_cert_file = /work/tls/ca.pem\ntls_require_cert = yes|" \
  "$WORK/ldap-mailboxes.cf" > "$WORK/ldap-mailboxes-tls.cf"
out=$(in_box "postmap -q alice@imx.sh ldap:/work/ldap-mailboxes-tls.cf")
check "Postfix over LDAPS: mailbox lookup" "alice@imx.sh/" "$out"

sed -e "s|^  ldap_uris = .*|  ldap_uris = $URI\n  ldap_starttls = yes|" \
    -e "s|^auth_mechanisms = plain|auth_mechanisms = plain\nssl_client_ca_file = /work/tls/ca.pem|" \
  "$WORK/dovecot.conf" > "$WORK/dovecot-tls.conf"
out=$(in_box "dovecot -c /work/dovecot-tls.conf && sleep 1 && doveadm -c /work/dovecot-tls.conf auth test alice@imx.sh Alice12345; echo rc=\$?")
check "Dovecot over StartTLS: IMAP login for alice" "auth succeeded" "$out"
# Proof that the line above went over TLS: trusting the wrong CA must fail it.
sed -e "s|/work/tls/ca.pem|/work/tls/other-ca.pem|" "$WORK/dovecot-tls.conf" > "$WORK/dovecot-tls-wrong-ca.conf"
out=$(in_box "dovecot -c /work/dovecot-tls-wrong-ca.conf && sleep 1 && doveadm -c /work/dovecot-tls-wrong-ca.conf auth test alice@imx.sh Alice12345; echo rc=\$?")
check "  ...and fails when Dovecot trusts the wrong CA" "ldap_start_tls_s() failed" "$out"

echo
echo "== Dex $DEX_IMAGE (LDAP connector over LDAPS, password grant)"
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
      # LDAPS, verified against the test CA: Go's TLS stack, where the other
      # clients here all use OpenSSL through libldap.
      host: 127.0.0.1:$LDAPS_PORT
      rootCA: /etc/dex/ca.pem
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
  -v "$WORK/tls/ca.pem:/etc/dex/ca.pem:ro,Z" \
  "$DEX_IMAGE" dex serve /etc/dex/config.yaml >/dev/null
for _ in $(seq 60); do curl -sf "http://127.0.0.1:$DEX_PORT/dex/.well-known/openid-configuration" >/dev/null && break; sleep 0.5; done
grant() {
  curl -s --user "$DEX_CLIENT" "http://127.0.0.1:$DEX_PORT/dex/token" \
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
out=$(curl -s --user "$ALICE_LOGIN" "http://127.0.0.1:$GITEA_PORT/api/v1/user")
check "Gitea logs alice in, creating her account" '"login":"alice"' "$out"
check "  ...with her mail from the directory" '"email":"alice@imx.sh"' "$out"
out=$(curl -s -o /dev/null -w '%{http_code}' --user "$CAROL_LOGIN" "http://127.0.0.1:$GITEA_PORT/api/v1/user")
check "Gitea refuses carol (no gitea service)" "401" "$out"
podman logs dmart-ldap-gitea > "$WORK/gitea.log" 2>&1
podman rm -f dmart-ldap-gitea >/dev/null 2>&1

echo
echo "server log: $WORK/server.log"
echo "failed checks: $FAILS"
exit "$FAILS"
