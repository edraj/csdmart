#!/usr/bin/env bash
# Remove installed packs from a dmart instance.
#
# Dropping a pack is dropping its space — which is exactly why each pack owns
# one (PLAN.md "Pack list"). What a space drop does NOT remove is the
# management overlay: the pack's roles and permissions live in the `management`
# space alongside dmart's own, so they are deleted by shortname instead.
#
# Everything a pack installs is <pack>_ prefixed or inside the pack's own
# space, so this never touches dmart's seeded roles, permissions or users.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
PACKS=""
URL="${DMART_URL:-}"
ADMIN="${DMART_ADMIN:-dmart}"
YES=0

usage() {
    cat <<EOF
Usage: $(basename "$0") [--packs a,b,c] --url http://127.0.0.1:8282 [--admin dmart] [--yes]

  --packs  comma-separated pack names; default is every pack present
  --url    dmart base URL; default \$DMART_URL
  --admin  admin shortname; default dmart
  --yes    do not prompt

Environment: DMART_ADMIN_PASSWORD

Deletes, for each named pack: its space (and therefore all its content), and
its <pack>_ prefixed roles and permissions from the management space.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --packs)   PACKS="$2"; shift 2 ;;
        --packs=*) PACKS="${1#*=}"; shift ;;
        --url)     URL="$2"; shift 2 ;;
        --url=*)   URL="${1#*=}"; shift ;;
        --admin)   ADMIN="$2"; shift 2 ;;
        --admin=*) ADMIN="${1#*=}"; shift ;;
        --yes|-y)  YES=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

[ -n "$URL" ] || { echo "--url is required (reset works over the API)" >&2; exit 2; }
[ -n "${DMART_ADMIN_PASSWORD:-}" ] || { echo "DMART_ADMIN_PASSWORD is required" >&2; exit 2; }

if [ -n "$PACKS" ]; then
    selected="$(echo "$PACKS" | tr ',' ' ')"
else
    selected=""
    for d in "$HERE"/*/; do
        [ -f "${d}pack.json" ] || continue
        selected="$selected $(basename "$d")"
    done
fi

for p in $selected; do
    [ -f "$HERE/$p/pack.json" ] || { echo "no such pack: $p" >&2; exit 2; }
done

# Name every space and shortname that will go, and let the operator read the
# list before anything is deleted. A space drop takes all its content with it.
echo "This will DELETE from $URL:"
for p in $selected; do
    space="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['space'])" "$HERE/$p/pack.json")"
    echo "  space '$space' and everything in it"
    python3 - "$HERE/$p/pack.json" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
for kind in ("roles", "permissions", "groups"):
    for sn in m["provides"][kind]:
        print(f"    management {kind[:-1]}: {sn}")
PY
done

if [ "$YES" != 1 ]; then
    printf 'Proceed? [y/N] '
    read -r reply
    case "$reply" in y|Y|yes|YES) ;; *) echo "aborted"; exit 1 ;; esac
fi

token="$(curl -fsS -m 15 -X POST "$URL/user/login" \
    -H 'Content-Type: application/json' \
    -d "$(python3 -c 'import json,os,sys; print(json.dumps({"shortname":sys.argv[1],"password":os.environ["DMART_ADMIN_PASSWORD"]}))' "$ADMIN")" \
  | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d["records"][0]["attributes"]["access_token"])')"

delete_one() {
    local space="$1" rt="$2" subpath="$3" shortname="$4"
    local body
    body="$(python3 -c '
import json,sys
print(json.dumps({"space_name":sys.argv[1],"request_type":"delete","records":[
  {"resource_type":sys.argv[2],"shortname":sys.argv[4],"subpath":sys.argv[3],
   "attributes":{}}]}))' "$space" "$rt" "$subpath" "$shortname")"
    local out
    out="$(curl -fsS -m 20 -X POST "$URL/managed/request" \
        -H 'Content-Type: application/json' -H "Authorization: Bearer $token" \
        -d "$body" || echo '{"status":"unreachable"}')"
    python3 -c 'import sys,json; d=json.load(sys.stdin); print(d.get("status","?"))' <<<"$out"
}

for p in $selected; do
    manifest="$HERE/$p/pack.json"
    space="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['space'])" "$manifest")"
    echo
    echo "== $p"
    # Overlay first: a permission naming a space that has just been dropped is
    # harmless, but a half-deleted overlay left behind by a failure part-way is
    # the thing that makes a re-install confusing.
    while read -r kind sn; do
        [ -n "$sn" ] || continue
        printf '  %-12s %-28s %s\n' "$kind" "$sn" "$(delete_one management "$kind" "${kind}s" "$sn")"
    done < <(python3 - "$manifest" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
for kind in ("role", "permission", "group"):
    for sn in m["provides"][kind + "s"]:
        print(kind, sn)
PY
)
    printf '  %-12s %-28s %s\n' space "$space" "$(delete_one "$space" space / "$space")"
done

cat <<EOF

== done

Restart the dmart server: the deleted roles and permissions stay in its
in-process authz cache until it does.
EOF
