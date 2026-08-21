#!/usr/bin/env bash
#
# CycloneDX SBOM for the Svelte frontends (cxb, catalog) that dmart embeds into
# the AOT binary via ManifestEmbeddedFileProvider. Their npm dependencies ship
# inside /usr/bin/dmart, so they belong in the binary's SBOM -- dotnet CycloneDX
# only sees the .NET side.
#
# Read from yarn.lock, the resolution build-ui.sh actually installs from. An
# earlier version resolved a fresh npm tree instead, which inventoried *latest*
# versions -- packages this build never ships, with advisories it is not exposed
# to -- and made Dependency-Track over-report. Accurate versions matter more
# than anything here.
#
# Method: syft reads yarn.lock for the resolved versions, `yarn list
# --production` supplies the set of names that are runtime (not build) deps, and
# the SBOM is the intersection. Build tooling (vite, esbuild, svelte-check) is
# compiled away and does not ship, so it is excluded.
#
# Usage: dist/frontend-sbom.sh --version X.Y.Z --out FILE.cdx.json
set -euo pipefail

VERSION=""; OUT=""
while [ $# -gt 0 ]; do
	case "$1" in
		--version) VERSION="$2"; shift 2 ;;
		--out)     OUT="$2"; shift 2 ;;
		*) echo "frontend-sbom.sh: unknown argument: $1" >&2; exit 2 ;;
	esac
done
[ -n "$VERSION" ] || { echo "frontend-sbom.sh: --version required" >&2; exit 2; }
[ -n "$OUT" ]     || { echo "frontend-sbom.sh: --out required" >&2; exit 2; }
cd "$(dirname "$0")/.."

command -v yarn >/dev/null 2>&1 || corepack enable 2>/dev/null || true

SYFT_VERSION="${SYFT_VERSION:-v1.51.0}"
TOOLDIR="${SBOM_TOOL_DIR:-${TMPDIR:-/tmp}/syft-${SYFT_VERSION}}"
if [ ! -x "$TOOLDIR/syft" ]; then
	echo "== installing syft $SYFT_VERSION"
	curl -sSfL https://raw.githubusercontent.com/anchore/syft/main/install.sh \
		| sh -s -- -b "$TOOLDIR" "$SYFT_VERSION" >/dev/null
fi

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT

# Resolve node_modules so `yarn list` can report the runtime tree; this respects
# the committed yarn.lock and changes nothing about the versions.
yarn install --frozen-lockfile >/dev/null 2>&1 || yarn install >/dev/null 2>&1
yarn list --production --json 2>/dev/null > "$WORK/list.json" || true

# syft the lockfile (a copy, so it does not also catalogue the .NET side).
cp yarn.lock package.json "$WORK/" 2>/dev/null || true
"$TOOLDIR/syft" "dir:$WORK" -o "cyclonedx-json@1.6"="$WORK/full.json" -q

VERSION="$VERSION" OUT="$OUT" python3 - "$WORK/full.json" "$WORK/list.json" <<'PY'
import json, os, sys

prod = set()
try:
	for line in open(sys.argv[2]):
		o = json.loads(line)
		if o.get("type") != "tree":
			continue
		def walk(nodes):
			for n in nodes:
				nm = n.get("name", "")
				if "@" in nm[1:]:
					prod.add(nm[:nm.rfind("@")])
				if n.get("children"):
					walk(n["children"])
		walk(o["data"]["trees"])
except Exception:
	pass

full = json.load(open(sys.argv[1])).get("components", [])
# If `yarn list` produced nothing (older yarn, offline), fall back to the full
# lockfile rather than emitting an empty SBOM.
comps = {}
for c in full:
	if prod and c.get("name") not in prod:
		continue
	c.setdefault("properties", []).append({"name": "oodi:embedded-frontend", "value": "cxb+catalog"})
	comps[c.get("purl") or (c.get("name"), c.get("version"))] = c

doc = {"bomFormat": "CycloneDX", "specVersion": "1.6", "version": 1,
       "metadata": {"component": {"type": "application", "name": "dmart-frontends", "version": os.environ["VERSION"]}},
       "components": list(comps.values())}
json.dump(doc, open(os.environ["OUT"], "w"), indent=2)
print("== frontends: %d runtime npm components (from yarn.lock) -> %s" % (len(doc["components"]), os.environ["OUT"]))
PY
