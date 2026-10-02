#!/usr/bin/env bash
# Install solution packs into a dmart instance.
#
# Two phases, because not everything can be imported:
#
#   1. `dmart import --type=fs` lands spaces, folders, roles and permissions.
#   2. Groups are created over the HTTP API. Groups do NOT round-trip through
#      import/export — PLAN.md finding 3, measured: a group sitting in the
#      `groups` table was simply absent from `dmart export`'s archive. So the
#      overlay cannot carry them and this script must create them.
#
# Then the server is restarted, or you are told to restart it. dmart's authz
# cache is an in-memory ConcurrentDictionary (AuthzCacheRefresher), NOT a
# materialized view — the docs that describe `mv_user_roles` /
# `mv_role_permissions` describe something that does not exist (PLAN.md
# verification 4). A CLI import therefore cannot invalidate a RUNNING server's
# cache, and new roles/permissions stay invisible until it restarts.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
DMART="${DMART_BIN:-$REPO/bin/dmart}"
PACKS=""
SCALE="small"
URL="${DMART_URL:-}"
ADMIN="${DMART_ADMIN:-dmart}"
SKIP_GROUPS=0
REPLACE=0

usage() {
    cat <<EOF
Usage: $(basename "$0") [--packs a,b,c] [--scale small|medium|large]
                        [--url http://127.0.0.1:8282] [--admin dmart]
                        [--replace] [--skip-groups]

  --packs        comma-separated pack names; default is every non-optional pack
  --scale        dataset size; default small
  --url          dmart base URL for the API phase; default \$DMART_URL
  --admin        admin shortname for the API phase; default dmart
  --replace      pass -r to the import (upsert instead of skip-existing)
  --skip-groups  do only the import phase, no API calls

Environment:
  BACKEND_ENV              config.env the CLI should use (required by dmart)
  DMART_BIN                dmart binary; default <repo>/bin/dmart
  DMART_ADMIN_PASSWORD     admin password for the API phase
  DMART_PACKS_DEMO_PASSWORD  password for generated demo users; never committed
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --packs)   PACKS="$2"; shift 2 ;;
        --packs=*) PACKS="${1#*=}"; shift ;;
        --scale)   SCALE="$2"; shift 2 ;;
        --scale=*) SCALE="${1#*=}"; shift ;;
        --url)     URL="$2"; shift 2 ;;
        --url=*)   URL="${1#*=}"; shift ;;
        --admin)   ADMIN="$2"; shift 2 ;;
        --admin=*) ADMIN="${1#*=}"; shift ;;
        --replace) REPLACE=1; shift ;;
        --skip-groups) SKIP_GROUPS=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

[ -x "$DMART" ] || { echo "dmart binary not found or not executable: $DMART" >&2; exit 1; }

# BACKEND_ENV is the ONLY way to point the CLI at a config file: a
# cwd-relative config.env is deliberately not a lookup step, and exported
# DATABASE_* shell variables are not read in its place
# (Config/DotEnv.cs:62-66). Fail loudly rather than silently installing into
# whatever ~/.dmart/config.env happens to say.
if [ -z "${BACKEND_ENV:-}" ] && [ ! -f "$HOME/.dmart/config.env" ] && [ ! -f /etc/dmart/config.env ]; then
    echo "no dmart config found. Set BACKEND_ENV=/path/to/config.env" >&2
    exit 1
fi

build_args=()
[ -n "$PACKS" ] && build_args+=(--packs "$PACKS")
build_args+=(--scale "$SCALE")

echo "== build"
"$HERE/build.sh" "${build_args[@]}"

SPACES="$HERE/dist/spaces"
[ -d "$SPACES" ] || { echo "build produced no spaces tree at $SPACES" >&2; exit 1; }

# Order matters, and not for taste. Imported content is OWNED by the personas
# (a case comment's owner_shortname is the agent who wrote it), and
# owner_shortname is a foreign key to users(shortname). So the users have to
# exist before the import runs or every persona-owned row fails with
# `FOREIGN KEY constraint failed`.
#
# This works the other way round too: users.roles is a plain text array with no
# foreign key, so a persona can be created holding a role the import has not
# landed yet. The role resolves the moment it arrives.
if [ "$SKIP_GROUPS" = 1 ] || [ -z "$URL" ] || [ -z "${DMART_ADMIN_PASSWORD:-}" ]; then
    echo
    echo "NOTE: the API phase is being skipped, so the personas will not exist"
    echo "      when the import runs and every persona-owned record (the case"
    echo "      comments) will fail its owner_shortname foreign key. Give"
    echo "      --url and DMART_ADMIN_PASSWORD for a complete install."
fi

if [ "$SKIP_GROUPS" = 1 ]; then
    echo
    echo "== groups: skipped (--skip-groups)"
else
    groups="$(python3 - "$HERE" ${PACKS:+--packs="$PACKS"} <<'PY'
import json, os, sys
root = sys.argv[1]
want = None
for a in sys.argv[2:]:
    if a.startswith("--packs="):
        want = [x for x in a.split("=", 1)[1].split(",") if x]
all_names = sorted(d for d in os.listdir(root)
                   if os.path.isfile(os.path.join(root, d, "pack.json")))
names = want if want is not None else [
    n for n in all_names
    if not json.load(open(os.path.join(root, n, "pack.json")))["optional"]]
out = []
for n in names:
    m = os.path.join(root, n, "pack.json")
    if not os.path.isfile(m):
        continue
    out += json.load(open(m))["provides"]["groups"]
print(" ".join(sorted(set(out))))
PY
)"
    if [ -z "$groups" ]; then
        echo
        echo "== groups: none to create"
    else
        echo
        echo "== groups (API — they do not round-trip through import)"
        if [ -z "$URL" ]; then
            echo "  no --url / \$DMART_URL set. Create these over the API once the server is up:"
            for g in $groups; do echo "    $g"; done
            echo "  or re-run: $(basename "$0") --skip-groups=0 --url http://127.0.0.1:8282"
        elif [ -z "${DMART_ADMIN_PASSWORD:-}" ]; then
            echo "  DMART_ADMIN_PASSWORD not set; cannot authenticate. Groups still to create:"
            for g in $groups; do echo "    $g"; done
            exit 1
        else
            token="$(curl -fsS -m 15 -X POST "$URL/user/login" \
                -H 'Content-Type: application/json' \
                -d "$(python3 -c 'import json,os,sys; print(json.dumps({"shortname":sys.argv[1],"password":os.environ["DMART_ADMIN_PASSWORD"]}))' "$ADMIN")" \
              | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d["records"][0]["attributes"]["access_token"])')"
            for g in $groups; do
                body="$(python3 -c '
import json,sys
print(json.dumps({"space_name":"management","request_type":"create","records":[
  {"resource_type":"group","shortname":sys.argv[1],"subpath":"groups",
   "attributes":{"is_active":True}}]}))' "$g")"
                status="$(curl -sS -m 15 -X POST "$URL/managed/request" \
                    -H 'Content-Type: application/json' \
                    -H "Authorization: Bearer $token" \
                    -d "$body" \
                  | python3 "$HERE/lib/report_status.py")"
                printf '  %-24s %s\n' "$g" "$status"
            done
        fi
    fi
fi

# ── personas ──────────────────────────────────────────────────────────────────
# Users DO round-trip through import (unlike groups), but a user needs a
# password to be any use and a demo password must never be committed. So the
# personas are created here, with the password taken from the environment.
if [ "$SKIP_GROUPS" = 1 ]; then
    echo
    echo "== personas: skipped (--skip-groups)"
elif [ -z "$URL" ] || [ -z "${DMART_ADMIN_PASSWORD:-}" ]; then
    echo
    echo "== personas: skipped (needs --url and DMART_ADMIN_PASSWORD)"
elif [ -z "${DMART_PACKS_DEMO_PASSWORD:-}" ]; then
    echo
    echo "== personas: skipped — set DMART_PACKS_DEMO_PASSWORD to create them"
    echo "   (nothing else needs it; the packs install fine without personas)"
else
    echo
    echo "== personas (API — a user needs a password, which is never committed)"
    # Only personas whose every role was actually installed: asking for just
    # the kb pack should not create a service-desk agent holding a role that
    # does not exist. Dependencies are resolved the same way build.sh does
    # them, or a persona whose role came from a pulled-in dependency would be
    # skipped for no reason.
    installed_roles="$(python3 - "$HERE" ${PACKS:+--packs="$PACKS"} <<'ROLES_PY'
import json, os, sys
root = sys.argv[1]
want = None
for a in sys.argv[2:]:
    if a.startswith("--packs="):
        want = [x for x in a.split("=", 1)[1].split(",") if x]
names = sorted(d for d in os.listdir(root)
               if os.path.isfile(os.path.join(root, d, "pack.json")))
sel, seen = [], set()
def visit(n):
    if n in seen or not os.path.isfile(os.path.join(root, n, "pack.json")):
        return
    m = json.load(open(os.path.join(root, n, "pack.json")))
    for d in m["depends"]:
        visit(d)
    seen.add(n)
    sel.append(n)
for n in (want if want is not None else
          [x for x in names
           if not json.load(open(os.path.join(root, x, "pack.json")))["optional"]]):
    visit(n)
roles = []
for n in sel:
    roles += json.load(open(os.path.join(root, n, "pack.json")))["provides"]["roles"]
print(" ".join(sorted(set(roles))))
ROLES_PY
)"
    if [ -z "${token:-}" ]; then
        token="$(curl -fsS -m 15 -X POST "$URL/user/login" \
            -H 'Content-Type: application/json' \
            -d "$(python3 -c 'import json,os,sys; print(json.dumps({"shortname":sys.argv[1],"password":os.environ["DMART_ADMIN_PASSWORD"]}))' "$ADMIN")" \
          | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d["records"][0]["attributes"]["access_token"])')"
    fi
    personas="$HERE/datasets/shanidar/personas.json"
    if [ ! -f "$personas" ]; then
        echo "  no personas.json — run: python3 $HERE/lib/gen_dataset.py --scale small" >&2
    else
        while IFS=$'\t' read -r sn body; do
            [ -n "$sn" ] || continue
            status="$(curl -sS -m 20 -X POST "$URL/managed/request" \
                -H 'Content-Type: application/json' \
                -H "Authorization: Bearer $token" \
                -d "$body" \
              | python3 "$HERE/lib/report_status.py" || echo request_failed)"
            printf '  %-20s %s\n' "$sn" "$status"
        done < <(python3 - "$personas" "$installed_roles" <<'USERS_PY'
import json, os, sys
doc = json.load(open(sys.argv[1]))
installed = set(sys.argv[2].split())
pw = os.environ["DMART_PACKS_DEMO_PASSWORD"]
for p in doc["personas"]:
    if not set(p["roles"]) <= installed:
        continue
    body = {"space_name": "management", "request_type": "create", "records": [{
        "resource_type": "user", "shortname": p["shortname"], "subpath": "users",
        "attributes": {
            "is_active": True,
            "displayname": {"en": p["displayname"]},
            "email": p["email"],
            "msisdn": p["msisdn"],
            "roles": p["roles"],
            "groups": p["groups"],
            "password": pw,
        }}]}
    print(p["shortname"] + "\t" + json.dumps(body))
USERS_PY
)
    fi
fi

echo
echo "== import"
import_args=(import --type=fs)
[ "$REPLACE" = 1 ] && import_args+=(-r)
# --skip-history is passed even though packs ship none: it costs nothing, and
# it is the guard if a dataset ever starts carrying history. It was a no-op on
# the zip path until #324; --type=fs has always honoured it.
import_args+=(--skip-history "$SPACES")
"$DMART" "${import_args[@]}"

cat <<EOF

== done

Restart the dmart server before using the new roles and permissions. dmart's
authz cache is in-process (AuthzCacheRefresher), so a CLI import cannot
invalidate a running server's copy — the roles exist in the database but the
server will not see them until it restarts.
EOF
