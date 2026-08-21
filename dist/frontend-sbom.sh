#!/usr/bin/env bash
#
# CycloneDX SBOM for the Svelte frontends (cxb, catalog) that dmart embeds into
# the AOT binary via ManifestEmbeddedFileProvider. Their npm dependencies ship
# inside /usr/bin/dmart, so they belong in the binary's SBOM -- dotnet CycloneDX
# only sees the .NET side.
#
# syft, not `npm sbom` / cyclonedx-npm: cxb ships no lockfile and its tree has
# peer-dependency conflicts that make `npm ls` (which those tools drive) exit
# non-zero. syft reads the lockfile directly and tolerates that. Only runtime
# dependencies are kept -- build tooling (vite, esbuild, svelte-check) is
# compiled away and does not ship -- filtered on each lockfile's `dev` flags.
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

SYFT_VERSION="${SYFT_VERSION:-v1.51.0}"
TOOLDIR="${SBOM_TOOL_DIR:-${TMPDIR:-/tmp}/syft-${SYFT_VERSION}}"
if [ ! -x "$TOOLDIR/syft" ]; then
	echo "== installing syft $SYFT_VERSION"
	curl -sSfL https://raw.githubusercontent.com/anchore/syft/main/install.sh \
		| sh -s -- -b "$TOOLDIR" "$SYFT_VERSION" >/dev/null
fi

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
parts=()
for spa in cxb catalog; do
	[ -f "$spa/package.json" ] || continue
	# Resolve each SPA in isolation. cxb and catalog are npm *workspaces* of the
	# repo root, so `npm install` run inside them writes the lockfile at the root,
	# not next to the package -- and cxb ships no committed lockfile at all. A
	# throwaway copy of just this package's manifest sidesteps the workspace and
	# yields a package-lock.json syft can read.
	iso="$WORK/$spa"; mkdir -p "$iso"
	cp "$spa/package.json" "$iso/"
	[ -f "$spa/package-lock.json" ] && cp "$spa/package-lock.json" "$iso/"
	( cd "$iso" && npm install --package-lock-only --no-audit --no-fund \
		--legacy-peer-deps >/dev/null 2>&1 )
	[ -f "$iso/package-lock.json" ] || { echo "frontend-sbom.sh: could not resolve a lockfile for $spa" >&2; exit 1; }
	"$TOOLDIR/syft" "dir:$iso" -o "cyclonedx-json@1.6"="$WORK/$spa.full.json" -q
	parts+=("$spa=$WORK/$spa.full.json=$iso/package-lock.json")
done

VERSION="$VERSION" OUT="$OUT" python3 - "${parts[@]}" <<'PY'
import json, os, sys

def prod(lockp):
	s=set()
	for path,meta in (json.load(open(lockp)).get("packages") or {}).items():
		if not path or meta.get("dev") is True: continue
		n=path.split("node_modules/")[-1]; v=meta.get("version")
		if n and v: s.add((n,v))
	return s

comps={}
for arg in sys.argv[1:]:
	spa, full, lock = arg.split("=")
	keep = prod(lock)
	for c in json.load(open(full)).get("components",[]):
		if (c.get("name"), c.get("version")) not in keep: continue
		c.setdefault("properties",[]).append({"name":"oodi:embedded-frontend","value":spa})
		key=c.get("purl") or (c.get("name"),c.get("version"))
		comps.setdefault(key,c)

doc={"bomFormat":"CycloneDX","specVersion":"1.6","version":1,
     "metadata":{"component":{"type":"application","name":"dmart-frontends","version":os.environ["VERSION"]}},
     "components":list(comps.values())}
json.dump(doc, open(os.environ["OUT"],"w"), indent=2)
print(f"== frontends: {len(doc['components'])} runtime npm components -> {os.environ['OUT']}")
PY
