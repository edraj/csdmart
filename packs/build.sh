#!/usr/bin/env bash
# Assemble selected packs into one importable spaces tree under packs/dist/.
#
# `dmart import --type=fs <dir>` takes a SPACES ROOT holding one directory per
# space, so the output is exactly that: each pack's space tree copied in under
# its own name, plus a single `management/` tree merged from every selected
# pack's overlay. One import, not one per pack — a permission and the space it
# names have to land in the same run or the first import references a space
# that does not exist yet.
#
# Deliberately NOT a zip: --type=fs is the path that honours --skip-history,
# --space/--subpath remap and --drop-indexes. The zip path refused or ignored
# several of those (see PLAN.md verification 5), and the tree is also far
# easier to inspect when an import fails.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST="$HERE/dist"
PACKS=""
SCALE="small"
# Which storyline's data to fold in. One value plus a data module is all a
# second storyline needs; the machinery above is domain-neutral.
DATASET="${PACKS_DATASET:-shanidar}"
CLEAN=1

usage() {
    cat <<EOF
Usage: $(basename "$0") [--packs a,b,c] [--scale small|medium|large] [--no-clean]

  --packs    comma-separated pack names; default is every non-optional pack
  --scale    dataset size to include; default small (the only committed one)
  --dataset  which storyline to fold in; default shanidar
  --no-clean keep an existing dist/ instead of rebuilding it from scratch

Output: $DIST/spaces/ — hand it to \`dmart import --type=fs\`.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --packs)   PACKS="$2"; shift 2 ;;
        --packs=*) PACKS="${1#*=}"; shift ;;
        --scale)   SCALE="$2"; shift 2 ;;
        --scale=*) SCALE="${1#*=}"; shift ;;
        --dataset)   DATASET="$2"; shift 2 ;;
        --dataset=*) DATASET="${1#*=}"; shift ;;
        --no-clean) CLEAN=0; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

case "$SCALE" in
    small|medium|large) ;;
    *) echo "--scale must be small, medium or large (got '$SCALE')" >&2; exit 2 ;;
esac

# Every directory holding a pack.json is a pack; lib/ and datasets/ are not.
all_packs() {
    local d
    for d in "$HERE"/*/; do
        [ -f "${d}pack.json" ] || continue
        basename "$d"
    done
}

# Default set excludes packs marked optional in their own manifest.
default_packs() {
    local p
    for p in $(all_packs); do
        if [ "$(json_field "$HERE/$p/pack.json" optional)" = "True" ]; then
            continue
        fi
        echo "$p"
    done
}

json_field() {
    python3 -c "import json,sys; print(json.load(open(sys.argv[1])).get(sys.argv[2]))" "$1" "$2"
}

if [ -n "$PACKS" ]; then
    selected="$(echo "$PACKS" | tr ',' ' ')"
else
    selected="$(default_packs)"
fi

# Validate names and resolve dependencies before copying anything, so a typo
# fails before dist/ is half-written.
for p in $selected; do
    [ -f "$HERE/$p/pack.json" ] || {
        echo "no such pack: $p (available: $(all_packs | tr '\n' ' '))" >&2
        exit 2
    }
done

# Pull in dependencies transitively. A pack whose dependency is missing would
# import a permission naming a space that is not there.
resolve_deps() {
    python3 - "$HERE" $selected <<'PY'
import json, os, sys
root, want = sys.argv[1], sys.argv[2:]
seen, order = set(), []
def visit(name, trail):
    if name in trail:
        raise SystemExit("dependency cycle: " + " -> ".join(trail + [name]))
    if name in seen:
        return
    manifest = os.path.join(root, name, "pack.json")
    if not os.path.isfile(manifest):
        raise SystemExit(f"pack '{name}' is required but not present")
    for dep in json.load(open(manifest))["depends"]:
        visit(dep, trail + [name])
    seen.add(name)
    order.append(name)
for n in want:
    visit(n, [])
print(" ".join(order))
PY
}

selected="$(resolve_deps)"
echo "packs:  $selected"
echo "scale:  $SCALE"
echo "data:   $DATASET"

if [ "$CLEAN" = 1 ]; then
    rm -rf "$DIST"
fi
mkdir -p "$DIST/spaces"

for p in $selected; do
    space="$(json_field "$HERE/$p/pack.json" space)"
    # The space tree lands under its space name, not the pack name. They are
    # the same today (one space per pack) but the manifest is what decides.
    if [ -d "$HERE/$p/space" ]; then
        mkdir -p "$DIST/spaces/$space"
        cp -R "$HERE/$p/space/." "$DIST/spaces/$space/"
    fi
    # Management overlays all merge into ONE management tree. Shortnames are
    # <pack>_ prefixed so two packs cannot collide on a role or permission.
    if [ -d "$HERE/$p/management" ]; then
        mkdir -p "$DIST/spaces/management"
        cp -R "$HERE/$p/management/." "$DIST/spaces/management/"
    fi
    # Dataset rows for this scale, when the pack ships any.
    ds="$HERE/datasets/$DATASET/$SCALE/$p"
    if [ -d "$ds" ]; then
        mkdir -p "$DIST/spaces/$space"
        cp -R "$ds/." "$DIST/spaces/$space/"
    fi
done

# Packs DO ship history. They did not until #329: the importer called
# AppendAsync, so it stamped every imported row with the import moment and
# appended a duplicate on each re-run. It now restores an authored uuid and
# timestamp and dedupes on the uuid, which is what makes a twelve-month archive
# possible at all.
#
# So the check inverts: not "refuse history" but "refuse history that would
# silently lose its dates". A line missing uuid or timestamp falls back to
# AppendAsync and gets `now()` — no error, just a quietly wrong archive. That is
# the failure worth catching here.
python3 - "$DIST/spaces" <<'PY'
import json, os, sys
root = sys.argv[1]
bad = []
files = 0
for dirpath, _, names in os.walk(root):
    if "history.jsonl" not in names:
        continue
    files += 1
    path = os.path.join(dirpath, "history.jsonl")
    for n, line in enumerate(open(path), start=1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
        except Exception as exc:
            bad.append(f"{path}:{n} unreadable ({exc})")
            continue
        for field in ("uuid", "timestamp", "owner_shortname"):
            if not row.get(field):
                bad.append(f"{path}:{n} missing {field}")
if bad:
    print("refusing to build — history that would lose its dates:", file=sys.stderr)
    print("\n".join("  " + b for b in bad), file=sys.stderr)
    raise SystemExit(1)
if files:
    print(f"history: {files} file(s), every line dated and attributed")
PY

# A management overlay naming a space that is not in this build would import a
# permission whose subpaths point nowhere. Catch it here rather than at install.
python3 - "$DIST/spaces" $selected <<'PY'
import json, os, sys
dist, selected = sys.argv[1], set(sys.argv[2:])
# management is always present; spaces come from the selected packs.
known = {d for d in os.listdir(dist)} | {"management"}
bad = []
permdir = os.path.join(dist, "management", "permissions", ".dm")
for sn in sorted(os.listdir(permdir)) if os.path.isdir(permdir) else []:
    meta = os.path.join(permdir, sn, "meta.permission.json")
    if not os.path.isfile(meta):
        continue
    for space in json.load(open(meta)).get("subpaths", {}):
        if space not in known and not space.startswith("__"):
            bad.append(f"  {sn} -> space '{space}'")
if bad:
    print("permissions reference spaces not in this build:", file=sys.stderr)
    print("\n".join(bad), file=sys.stderr)
    raise SystemExit(1)
PY

# A pack must not reach outside itself. This is the check that makes a pack from
# someone else's repo safe to install: without it a pack grants itself whatever
# it likes, and installing one means handing it your whole instance.
#
# Three ways a pack could reach out, all refused here:
#
#   1. a permission naming a space other than the pack's own — including
#      __all_spaces__, which grants everything everywhere
#   2. a role pointing at a permission the pack does not provide, which would
#      borrow someone else's grant
#   3. a role, permission or group whose shortname is not <pack>_ prefixed,
#      which could OVERWRITE a dmart-seeded row. A pack shipping a role called
#      `super_admin` would otherwise redefine it on import.
#
# `__all_subpaths__` INSIDE the pack's own space is fine and several packs use
# it — the rule is about which space, not how much of it.
#
# Consequence worth knowing: a pack cannot grant read access to a space it
# merely `links` to. So a servicedesk agent cannot read the kb article a case
# cites, unless the kb pack grants it or the operator adds a permission by
# hand. Relaxing the rule to cover declared `links` would fix that and is
# still reviewable from the manifest — but it is a widening, so it is a
# decision rather than something to slip in here.
python3 - "$HERE" $selected <<'SCOPE_PY'
import json, os, sys
root, packs = sys.argv[1], sys.argv[2:]
bad = []
for name in packs:
    manifest = os.path.join(root, name, "pack.json")
    if not os.path.isfile(manifest):
        continue
    pack = json.load(open(manifest))
    own_space = pack["space"]
    provides = pack.get("provides", {})
    own_perms = set(provides.get("permissions", []))
    prefix = name + "_"

    def shipped(kind):
        d = os.path.join(root, name, "management", kind, ".dm")
        if not os.path.isdir(d):
            return []
        out = []
        for sn in sorted(os.listdir(d)):
            meta = os.path.join(d, sn, f"meta.{kind[:-1]}.json")
            if os.path.isfile(meta):
                out.append((sn, json.load(open(meta))))
        return out

    for sn, perm in shipped("permissions"):
        if not sn.startswith(prefix):
            bad.append(f"{name}: permission '{sn}' is not '{prefix}'-prefixed "
                       "— it could overwrite a row this pack does not own")
        for space in (perm.get("subpaths") or {}):
            if space == "__all_spaces__":
                bad.append(f"{name}: permission '{sn}' claims __all_spaces__ "
                           "— a pack may only grant access to its own space")
            elif space != own_space:
                bad.append(f"{name}: permission '{sn}' claims space '{space}', "
                           f"but the pack owns '{own_space}'")
    for sn, role in shipped("roles"):
        if not sn.startswith(prefix):
            bad.append(f"{name}: role '{sn}' is not '{prefix}'-prefixed "
                       "— it could overwrite a row this pack does not own")
        for p in (role.get("permissions") or []):
            if p not in own_perms:
                bad.append(f"{name}: role '{sn}' holds permission '{p}', which "
                           "this pack does not provide")
    for sn in provides.get("groups", []) + provides.get("public_roles", []):
        if not sn.startswith(prefix) and not sn.startswith("org_"):
            bad.append(f"{name}: '{sn}' is not '{prefix}'-prefixed")

if bad:
    print("refusing to build — a pack reaches outside itself:", file=sys.stderr)
    for b in bad:
        print("  " + b, file=sys.stderr)
    raise SystemExit(1)
SCOPE_PY

# Publish the RESOLVED pack list (dependencies included) so install.sh does not
# have to re-derive it. Three phases in install.sh were each re-implementing the
# same transitive walk in Python; the build already did it properly once.
printf '%s\n' $selected > "$DIST/selected.txt"

spaces="$(find "$DIST/spaces" -mindepth 1 -maxdepth 1 -type d | wc -l)"
metas="$(find "$DIST/spaces" -name 'meta.*.json' | wc -l)"
echo "built:  $spaces space(s), $metas meta file(s) -> $DIST/spaces"
echo
echo "next:   dmart import --type=fs '$DIST/spaces'"
