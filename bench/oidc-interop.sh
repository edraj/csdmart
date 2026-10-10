#!/usr/bin/env bash
# Interop check for dmart's OpenID Connect provider (docs/oidc-provider.md)
# against a real relying party: oauth2-proxy, whose go-oidc verifier checks the
# discovery document, the issuer, the audience, the ID token's signature
# against the JWKS, its expiry and the nonce. The browser is curl with a
# cookie jar.
#
#   1. sign-in through oauth2-proxy: its redirect, dmart's form, the callback,
#      then a request that reaches the upstream as the signed-in user
#   2. single sign-on: a second relying party, same browser, no form
#   3. a user without the client's service is refused
#   4. MAS 1.26 (matrix-deploy's pin) with dmart as its upstream provider, in
#      place of Dex: sign-in, account creation from the imported claims, and
#      provisioning on a stand-in homeserver
#
#   usage: bench/oidc-interop.sh "<command that runs dmart>" [workdir]
#   e.g.   bench/oidc-interop.sh "dotnet bin/Release/net10.0/dmart.dll"
#
# Exit status is the number of failed checks.
set -uo pipefail

DMART="${1:?usage: oidc-interop.sh \"<dmart command>\" [workdir]}"
WORK="${2:-$(mktemp -d -t oidc-interop-XXXXXX)}"
mkdir -p "$WORK"
WORK="$(cd "$WORK" && pwd)"

PORT=18484
ISSUER="http://127.0.0.1:$PORT"
PROXY_IMAGE="quay.io/oauth2-proxy/oauth2-proxy:v7.12.0"
MAS_IMAGE="ghcr.io/element-hq/matrix-authentication-service:1.26.0"
MAS_PORT=18080
HS_PORT=18008
MAS_PG_PORT=55436
# The upstream provider id matrix-deploy keeps (mas_upstream_provider_id).
MAS_PROVIDER=01K4A76P0VWGH4JA6596FYW4H4
ADMIN_PW="Admin12345"
PROXY_SECRET="proxy-secret-$(head -c 12 /dev/urandom | base64 | tr -d '/+=')"
FAILS=0

check() {
  if grep -qF -- "$2" <<<"$3"; then echo "  PASS  $1"
  else echo "  FAIL  $1"; echo "        expected '$2', got: $(head -c 400 <<<"$3")"; FAILS=$((FAILS + 1)); fi
}

cat > "$WORK/clients.json" <<EOF
{"clients": [
  {"client_id": "proxy-a", "client_secret": "$PROXY_SECRET", "name": "Proxy A",
   "redirect_uris": ["http://127.0.0.1:4180/oauth2/callback"], "services": ["matrix"]},
  {"client_id": "proxy-b", "client_secret": "$PROXY_SECRET", "name": "Proxy B",
   "redirect_uris": ["http://127.0.0.1:4181/oauth2/callback"]},
  {"client_id": "mas", "client_secret": "$PROXY_SECRET", "name": "Matrix",
   "redirect_uris": ["http://127.0.0.1:$MAS_PORT/upstream/callback/$MAS_PROVIDER"], "services": ["matrix"]}
]}
EOF
cat > "$WORK/config.env" <<EOF
DATABASE_DRIVER="sqlite"
SQLITE_PATH="$WORK/dmart.db"
LISTENING_HOST="127.0.0.1"
LISTENING_PORT=$PORT
JWT_SECRET="$(head -c 48 /dev/urandom | base64 | tr -d '/+=' | head -c 48)"
ADMIN_PASSWORD="$ADMIN_PW"
OIDC_ISSUER="$ISSUER"
OIDC_SIGNING_KEY_FILE="$WORK/oidc-key.pem"
OIDC_CLIENTS_FILE="$WORK/clients.json"
EOF
chmod 600 "$WORK/config.env"

echo "workdir: $WORK"
BACKEND_ENV="$WORK/config.env" $DMART serve > "$WORK/server.log" 2>&1 &
SERVER_PID=$!
HS_PID=""
cleanup() {
  podman rm -f dmart-oidc-proxy-a dmart-oidc-proxy-b dmart-oidc-mas dmart-oidc-mas-pg >/dev/null 2>&1
  [ -n "$HS_PID" ] && kill "$HS_PID" 2>/dev/null
  kill "$SERVER_PID" 2>/dev/null; wait "$SERVER_PID" 2>/dev/null
}
trap cleanup EXIT
for _ in $(seq 120); do curl -sf "$ISSUER/.well-known/openid-configuration" >/dev/null && break; sleep 0.5; done

TOKEN=$(curl -sf "$ISSUER/user/login" -H 'Content-Type: application/json' \
  -d "{\"shortname\":\"dmart\",\"password\":\"$ADMIN_PW\"}" | jq -r '.records[0].attributes.access_token')
curl -sf "$ISSUER/managed/request" -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d '{
  "space_name":"management","request_type":"create","records":[
  {"resource_type":"user","subpath":"users","shortname":"alice","attributes":{"is_active":true,"password":"Alice12345",
    "email":"alice@elsewhere.test","mailbox":"alice@imx.sh","services":["matrix"],"displayname":{"en":"Alice Example"}}},
  {"resource_type":"user","subpath":"users","shortname":"carol","attributes":{"is_active":true,"password":"Carol12345",
    "email":"carol@elsewhere.test"}}]}' | jq -e '.status == "success"' >/dev/null || { echo "seeding failed"; exit 100; }

proxy() {   # name port client-id
  podman rm -f "dmart-oidc-$1" >/dev/null 2>&1
  podman run -d --name "dmart-oidc-$1" --network host "$PROXY_IMAGE" \
    --provider=oidc --oidc-issuer-url="$ISSUER" --client-id="$3" --client-secret="$PROXY_SECRET" \
    --redirect-url="http://127.0.0.1:$2/oauth2/callback" --http-address="127.0.0.1:$2" \
    --email-domain='*' --upstream=static://200 --cookie-secure=false --skip-provider-button=true \
    --cookie-secret="$(head -c 32 /dev/urandom | base64 | tr -d '/+=' | head -c 32)" \
    --code-challenge-method=S256 --scope="openid profile email" >/dev/null
  for _ in $(seq 60); do curl -s -o /dev/null "http://127.0.0.1:$2/ping" && return 0; sleep 0.5; done
  echo "oauth2-proxy $1 did not start"; podman logs "dmart-oidc-$1" | tail -5; exit 100
}

# One browser: a cookie jar, following nothing on its own.
JAR="$WORK/cookies.txt"
browse() { curl -s -c "$JAR" -b "$JAR" -o "$WORK/last.html" -D "$WORK/last.headers" -w '%{http_code}' "$@"; }
location() { grep -i '^location:' "$WORK/last.headers" | tail -1 | cut -d' ' -f2- | tr -d '\r'; }

# Follows a relying party's sign-in from its start URL to the end. Fills in
# dmart's form when one appears. Prints the final status and where it ended.
sign_in() {   # start-url user password
  local url="$1" code
  for _ in $(seq 10); do
    code=$(browse "$url")
    if [ "$code" = 200 ] && grep -q 'name="form_token"' "$WORK/last.html"; then
      local fields=() name value
      while read -r name value; do fields+=(--data-urlencode "$name=$value"); done < <(
        grep -o '<input type="hidden" name="[a-z_]*" value="[^"]*">' "$WORK/last.html" \
          | sed -E 's/.*name="([a-z_]+)" value="([^"]*)".*/\1 \2/' | python3 -c '
import html,sys
for line in sys.stdin: print(html.unescape(line.rstrip("\n")))')
      code=$(browse -X POST "$ISSUER/oidc/authorize" "${fields[@]}" --data-urlencode "username=$2" --data-urlencode "password=$3")
    fi
    case "$code" in
      30?) url=$(location); case "$url" in http*) ;; *) url="${url_base:-}$url" ;; esac ;;
      *) echo "$code $url"; return ;;
    esac
  done
  echo "loop $url"
}

echo
echo "== oauth2-proxy $PROXY_IMAGE as a relying party"
proxy proxy-a 4180 proxy-a
out=$(curl -s "$ISSUER/.well-known/openid-configuration")
check "discovery names the token endpoint" "\"token_endpoint\":\"$ISSUER/oidc/token\"" "$out"

url_base="http://127.0.0.1:4180"
out=$(sign_in "http://127.0.0.1:4180/oauth2/start?rd=%2F" alice@imx.sh Alice12345)
check "sign-in through oauth2-proxy reaches the upstream" "200 http://127.0.0.1:4180/" "$out"
out=$(curl -s -b "$JAR" http://127.0.0.1:4180/oauth2/userinfo)
check "  ...as alice, with her hosted mailbox" '"email":"alice@imx.sh"' "$out"
check "  ...and her username" '"preferredUsername":"alice"' "$out"

echo
echo "== single sign-on: a second relying party, same browser"
proxy proxy-b 4181 proxy-b
url_base="http://127.0.0.1:4181"
out=$(sign_in "http://127.0.0.1:4181/oauth2/start?rd=%2F" nobody nothing)
check "signed in without the form" "200 http://127.0.0.1:4181/" "$out"

echo
echo "== a user without the client's service"
rm -f "$JAR"
url_base="http://127.0.0.1:4180"
out=$(sign_in "http://127.0.0.1:4180/oauth2/start?rd=%2F" carol Carol12345)
check "proxy A refuses carol: dmart answered access_denied" "error=access_denied" "$out"
check "  ...and she never reached the upstream" "oauth2/callback" "$out"

echo
echo "== MAS $MAS_IMAGE with dmart as its upstream provider"
# A stand-in homeserver for MAS's provisioning calls: it logs each request and
# answers as a fresh Synapse would (the localpart is free, writes succeed).
cat > "$WORK/hs_stub.py" <<'PY'
import json, sys
from http.server import BaseHTTPRequestHandler, HTTPServer
class H(BaseHTTPRequestHandler):
    def answer(self):
        n = int(self.headers.get("content-length") or 0)
        body = self.rfile.read(n).decode(errors="replace") if n else ""
        sys.stderr.write(f"HS {self.command} {self.path} {body}\n"); sys.stderr.flush()
        out = {"available": True} if "localpart_available" in self.path else {}
        data = json.dumps(out).encode()
        self.send_response(200); self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(data))); self.end_headers(); self.wfile.write(data)
    do_GET = do_POST = do_PUT = do_DELETE = answer
    def log_message(self, *a): pass
HTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
PY
python3 "$WORK/hs_stub.py" "$HS_PORT" 2> "$WORK/homeserver.log" &
HS_PID=$!
podman rm -f dmart-oidc-mas-pg >/dev/null 2>&1
podman run -d --name dmart-oidc-mas-pg -p "127.0.0.1:$MAS_PG_PORT:5432" -e POSTGRES_USER=mas -e POSTGRES_PASSWORD=mas \
  -e POSTGRES_DB=mas docker.io/library/postgres:17-alpine >/dev/null
podman run --rm "$MAS_IMAGE" config generate 2>/dev/null > "$WORK/mas-generated.yaml"
python3 - "$WORK/mas-generated.yaml" "$WORK/mas.yaml" <<PY
import sys, yaml
c = yaml.safe_load(open(sys.argv[1]))
c["http"]["listeners"][0]["binds"] = [{"address": "127.0.0.1:$MAS_PORT"}]
c["http"]["listeners"][1]["binds"] = [{"host": "127.0.0.1", "port": $MAS_PORT + 1}]
c["http"]["public_base"] = c["http"]["issuer"] = "http://127.0.0.1:$MAS_PORT/"
c["database"]["uri"] = "postgresql://mas:mas@127.0.0.1:$MAS_PG_PORT/mas"
c["matrix"].update({"homeserver": "localhost", "endpoint": "http://127.0.0.1:$HS_PORT/"})
c["passwords"] = {"enabled": False}
# matrix-deploy's provider, pointed at dmart. Discovery mode "insecure" only
# because this issuer is plain http on loopback; the ID token is still verified.
c["upstream_oauth2"] = {"providers": [{
  "id": "$MAS_PROVIDER", "issuer": "$ISSUER", "human_name": "dmart", "discovery_mode": "insecure",
  "client_id": "mas", "client_secret": "$PROXY_SECRET", "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email",
  "claims_imports": {
    "localpart": {"action": "require", "template": "{{ user.preferred_username }}"},
    "displayname": {"action": "suggest", "template": "{{ user.name }}"},
    "email": {"action": "suggest", "template": "{{ user.email }}"}}}]}
yaml.safe_dump(c, open(sys.argv[2], "w"))
PY
for _ in $(seq 30); do podman exec dmart-oidc-mas-pg pg_isready -U mas >/dev/null 2>&1 && break; sleep 1; done
sleep 2
mas_cli() { podman run --rm --network host -v "$WORK:/w:Z" "$MAS_IMAGE" "$@" -c /w/mas.yaml >> "$WORK/mas-setup.log" 2>&1; }
mas_cli database migrate && mas_cli config sync || { echo "MAS setup failed"; tail -5 "$WORK/mas-setup.log"; exit 100; }
podman run -d --name dmart-oidc-mas --network host -v "$WORK:/w:Z" "$MAS_IMAGE" server -c /w/mas.yaml >/dev/null
for _ in $(seq 60); do curl -sf "http://127.0.0.1:$((MAS_PORT + 1))/health" >/dev/null && break; sleep 0.5; done

rm -f "$JAR"
url_base="http://127.0.0.1:$MAS_PORT"
out=$(sign_in "http://127.0.0.1:$MAS_PORT/upstream/authorize/$MAS_PROVIDER" alice@imx.sh Alice12345)
check "MAS took dmart's code and ID token, and asks to create the account" "200 http://127.0.0.1:$MAS_PORT/upstream/link/" "$out"
page=$(cat "$WORK/last.html")
check "  ...as @alice, from preferred_username" 'value="@alice:localhost"' "$page"
check "  ...with her hosted mailbox, from email" 'value="alice@imx.sh"' "$page"
check "  ...and her display name, from name" 'value="Alice Example"' "$page"
link=$(cut -d' ' -f2 <<<"$out")
csrf=$(grep -o 'name="csrf" value="[^"]*"' "$WORK/last.html" | cut -d'"' -f4)
code=$(browse -X POST "$link" --data-urlencode "csrf=$csrf" --data-urlencode action=register \
  --data-urlencode import_email=on --data-urlencode import_display_name=on)
check "creating the account succeeds" "303" "$code"
# MAS registers in steps: follow them to the end, as a browser would.
for _ in $(seq 6); do
  case "$code" in 30?) next=$(location); case "$next" in /*) next="$url_base$next";; esac; code=$(browse "$next") ;; *) break ;; esac
done
# Then it provisions through its job queue, a moment after the account exists.
for _ in $(seq 60); do grep -q provision_user "$WORK/homeserver.log" && break; sleep 0.5; done
check "  ...and MAS provisions it on the homeserver" "provision_user" "$(cat "$WORK/homeserver.log")"
check "  ...with her mailbox and name" "alice@imx.sh" "$(grep provision_user "$WORK/homeserver.log")"
podman logs dmart-oidc-mas > "$WORK/mas.log" 2>&1

echo
echo "server log: $WORK/server.log"
echo "failed checks: $FAILS"
exit "$FAILS"
