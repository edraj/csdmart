#!/usr/bin/env bash
# Starts a seeded dmart server for the Playwright suite — the same script for
# a local run and for CI. The binary must already be built with both SPAs
# embedded (yarn build in cxb/ and catalog/, then dotnet build).
#
#   E2E_BIN    path to the dmart apphost (default: bin/Release, else bin/Debug)
#   E2E_PORT   listening port (default 5399)
#   E2E_WORK   scratch dir for config, spaces and the SQLite file (default e2e/.work)
#   E2E_PASSWORD  admin password written to the config (default dmart)
#
# Prints the base URL; writes the pid to $E2E_WORK/pid. Stop with:
#   kill "$(cat e2e/.work/pid)"
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
PORT=${E2E_PORT:-5399}
WORK=${E2E_WORK:-$ROOT/e2e/.work}
BIN=${E2E_BIN:-}
if [ -z "$BIN" ]; then
  for c in "$ROOT/bin/Release/net10.0/dmart" "$ROOT/bin/Debug/net10.0/dmart"; do
    [ -x "$c" ] && { BIN=$c; break; }
  done
fi
[ -x "${BIN:-}" ] || { echo "no dmart binary; build the SPAs and the server first" >&2; exit 1; }

rm -rf "$WORK"
mkdir -p "$WORK/spaces"
cat > "$WORK/config.env" <<CFG
LISTENING_HOST="127.0.0.1"
LISTENING_PORT=$PORT
DATABASE_DRIVER="sqlite"
SQLITE_PATH="$WORK/dmart.db"
SPACES_FOLDER="$WORK/spaces"
JWT_SECRET="e2e-test-secret-e2e-test-secret-32-bytes"
ADMIN_PASSWORD="${E2E_PASSWORD:-dmart}"
ENABLE_MCP=false
# The suite signs in once per SPA, but retries and local re-runs add up.
AUTH_RATE_LIMIT_PER_MINUTE=120
CFG
chmod 600 "$WORK/config.env"

# Bundled sample spaces → flat files → database. The suite asserts on their
# content (the Website space, its pages, the "Why DMART?" post).
BACKEND_ENV="$WORK/config.env" "$BIN" seed > "$WORK/seed.log" 2>&1

BACKEND_ENV="$WORK/config.env" "$BIN" serve > "$WORK/server.log" 2>&1 &
echo $! > "$WORK/pid"
for _ in $(seq 1 60); do
  curl -sf "http://127.0.0.1:$PORT/cat/index.html" > /dev/null 2>&1 && break
  sleep 0.5
done
if ! curl -sf "http://127.0.0.1:$PORT/cat/index.html" > /dev/null 2>&1; then
  echo "server did not come up on $PORT; see $WORK/server.log" >&2
  kill "$(cat "$WORK/pid")" 2>/dev/null || true
  exit 1
fi
echo "http://127.0.0.1:$PORT"
