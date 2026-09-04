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
# Licences are then attached from a second pass, because yarn.lock records only
# name/version/integrity -- it has no licence field at all, so syft alone emits
# a document where every component is unlicensed. That is not a cosmetic gap: a
# reviewer reading it cannot tell an MIT dependency from a revenue-gated
# commercial one, and Dependency-Track reports the whole tree as "no licence"
# rather than flagging the one term that actually needs a decision.
#
# Two sources, in order:
#   1. node_modules -- authoritative for what this install resolved. Walked
#      recursively so nested (deduped-away) copies are indexed too.
#   2. the npm registry -- only for what step 1 could not find, which in
#      practice is the optional per-platform native binaries (@esbuild/win32-*,
#      lightningcss-*-msvc, @tailwindcss/oxide-*, fsevents ...). Those are never
#      installed on the build machine, so no local lookup can ever resolve them.
#      Best-effort and time-boxed; set FRONTEND_SBOM_OFFLINE=1 to skip it.
#
# Usage: dist/frontend-sbom.sh --version X.Y.Z --out FILE.cdx.json
#        FRONTEND_SBOM_OFFLINE=1 dist/frontend-sbom.sh ...   # node_modules only
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

# Resolve node_modules so `yarn list` can report the runtime tree and so the
# licence pass has a tree to read; this respects the committed yarn.lock and
# changes nothing about the versions.
yarn install --frozen-lockfile >/dev/null 2>&1 || yarn install >/dev/null 2>&1
yarn list --production --json 2>/dev/null > "$WORK/list.json" || true

# syft the lockfile (a copy, so it does not also catalogue the .NET side).
cp yarn.lock package.json "$WORK/" 2>/dev/null || true
"$TOOLDIR/syft" "dir:$WORK" -o "cyclonedx-json@1.6"="$WORK/full.json" -q

VERSION="$VERSION" OUT="$OUT" python3 - "$WORK/full.json" "$WORK/list.json" <<'PY'
import glob, json, os, re, sys, time, urllib.parse, urllib.request

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

# ---------------------------------------------------------------- licences --
# SPDX identifiers CycloneDX will accept in `license.id`. That field is an
# enumeration: a value outside it makes the document fail schema validation,
# which actions/attest-sbom runs before wrapping the SBOM in a predicate. So
# anything not recognised here goes to `license.name` instead -- lossier, but
# always valid. Erring that way is deliberate; the alternative breaks releases.
SPDX_IDS = frozenset("""
0BSD AGPL-3.0 AGPL-3.0-only AGPL-3.0-or-later Apache-2.0 Artistic-2.0
BSD-2-Clause BSD-3-Clause BSD-3-Clause-Clear BlueOak-1.0.0 CC-BY-3.0 CC-BY-4.0
CC-BY-SA-4.0 CC0-1.0 CDDL-1.0 EPL-1.0 EPL-2.0 EUPL-1.2 GPL-2.0 GPL-2.0-only
GPL-2.0-or-later GPL-3.0 GPL-3.0-only GPL-3.0-or-later ISC LGPL-2.1
LGPL-2.1-only LGPL-2.1-or-later LGPL-3.0 LGPL-3.0-only LGPL-3.0-or-later MIT
MIT-0 MPL-2.0 OFL-1.1 PostgreSQL Python-2.0 Ruby UPL-1.0 Unlicense W3C WTFPL
Zlib
""".split())

# npm has three historical spellings of the same field. `licenses` as a list is
# the oldest and means "your choice of", i.e. OR.
def lic_string(pkg):
	one = pkg.get("license")
	if isinstance(one, str) and one.strip():
		return one.strip()
	if isinstance(one, dict) and one.get("type"):
		return str(one["type"]).strip()
	many = pkg.get("licenses")
	if isinstance(many, str) and many.strip():
		return many.strip()
	if isinstance(many, list):
		parts = [x.get("type") if isinstance(x, dict) else x for x in many]
		parts = [str(x).strip() for x in parts if x]
		if len(parts) == 1:
			return parts[0]
		if parts:
			return "(" + " OR ".join(parts) + ")"
	return None

COMPOUND = re.compile(r"\s(?:OR|AND|WITH)\s")
TOKEN = re.compile(r"[A-Za-z0-9.+-]+")

# Returns (cyclonedx_entry, kind). `kind` drives the report: "name" means the
# string is not an SPDX identifier, which is exactly the case a human has to
# look at -- a bare "SEE LICENSE IN LICENSE" is how a dual/commercial licence
# announces itself.
def cdx_license(raw):
	s = (raw or "").strip()
	if not s:
		return None, None
	if s.startswith("(") or COMPOUND.search(s):
		toks = [t for t in TOKEN.findall(s) if t.upper() not in ("OR", "AND", "WITH")]
		if toks and all(t in SPDX_IDS for t in toks):
			return {"expression": s}, "expression"
		return {"license": {"name": s}}, "name"
	if s in SPDX_IDS:
		return {"license": {"id": s}}, "id"
	return {"license": {"name": s}}, "name"

# cxb and catalog, read from the root package.json rather than hardcoded so a
# third app added later is picked up without editing this script. yarn hoists
# almost everything to the root, but a version conflict leaves a copy under the
# workspace, and that copy is the one the app actually resolves.
try:
	root_pkg = json.load(open("package.json", encoding="utf-8"))
	ws = root_pkg.get("workspaces") or []
	WORKSPACES = ws.get("packages", []) if isinstance(ws, dict) else list(ws)
except Exception:
	WORKSPACES = []

# Index every package.json under node_modules, including nested copies yarn
# could not dedupe. Keyed by (name, version) because two versions of the same
# package can carry different licences -- a relicensing mid-major is exactly
# the event an SBOM exists to record.
def index_installed():
	exact = {}
	# glob, not plain concatenation: `workspaces` may hold patterns
	# ("packages/*"), and a literal path built from one would just fail
	# os.path.isdir and skip that tree without saying so.
	bases = ["node_modules"]
	for w in WORKSPACES:
		bases.extend(glob.glob(w + "/node_modules"))
	for base in bases:
		if not os.path.isdir(base):
			continue
		for dirpath, dirnames, filenames in os.walk(base):
			dirnames[:] = [d for d in dirnames if not d.startswith(".")]
			if "package.json" not in filenames:
				continue
			try:
				pkg = json.load(open(os.path.join(dirpath, "package.json"), encoding="utf-8"))
			except Exception:
				continue
			name, ver = pkg.get("name"), pkg.get("version")
			if not name or not ver:
				continue
			got = lic_string(pkg)
			if got:
				exact.setdefault((name, ver), got)
	return exact

installed = index_installed()

REGISTRY_BUDGET = float(os.environ.get("FRONTEND_SBOM_REGISTRY_BUDGET", "90"))
offline = os.environ.get("FRONTEND_SBOM_OFFLINE") == "1"
deadline = time.monotonic() + REGISTRY_BUDGET

# One version manifest, not the whole packument: /<name>/<version> is a few KB
# where /<name> can be megabytes for a package with a long release history.
def registry_license(name, version):
	if offline or time.monotonic() > deadline:
		return None
	url = "https://registry.npmjs.org/%s/%s" % (urllib.parse.quote(name, safe="@/"), version)
	try:
		with urllib.request.urlopen(url, timeout=5) as r:
			return lic_string(json.load(r))
	except Exception:
		return None

from_local = from_registry = 0
review, undeclared = [], []
for c in comps.values():
	if c.get("licenses"):
		continue  # syft already knew; do not second-guess it
	name, ver = c.get("name"), c.get("version")
	raw = installed.get((name, ver))
	src = "local"
	if raw is None:
		raw = registry_license(name, ver)
		src = "registry"
	entry, kind = cdx_license(raw)
	if entry is None:
		undeclared.append("%s@%s" % (name, ver))
		continue
	c["licenses"] = [entry]
	if src == "local":
		from_local += 1
	else:
		from_registry += 1
	if kind == "name":
		review.append("%s@%s  %s" % (name, ver, raw))

doc = {"bomFormat": "CycloneDX", "specVersion": "1.6", "version": 1,
       "metadata": {"component": {"type": "application", "name": "dmart-frontends", "version": os.environ["VERSION"]}},
       "components": list(comps.values())}
json.dump(doc, open(os.environ["OUT"], "w"), indent=2)

total = len(doc["components"])
covered = from_local + from_registry
print("== frontends: %d runtime npm components (from yarn.lock) -> %s" % (total, os.environ["OUT"]))
print("== licences: %d from node_modules, %d from registry, %d undeclared%s"
      % (from_local, from_registry, len(undeclared), " (offline)" if offline else ""))
if review:
	# Printed, never suppressed: a non-SPDX string is the one licence outcome
	# that needs a person, and a count alone would not say which package.
	print("== %d component(s) carry a non-SPDX licence and need review:" % len(review))
	for r in sorted(review):
		print("     " + r)
if undeclared:
	print("== %d component(s) declare no licence:" % len(undeclared))
	for u in sorted(undeclared)[:20]:
		print("     " + u)
	if len(undeclared) > 20:
		print("     ... and %d more" % (len(undeclared) - 20))

# Regression guard, not a quality bar. The two sources fail independently, so
# the realistic breakage is total (a rename under node_modules, a registry
# outage with FRONTEND_SBOM_OFFLINE unset). Losing one source still clears this
# floor; losing both does not, which is the case worth refusing to publish --
# an SBOM asserting 400-odd unlicensed components reads as a licence finding
# rather than the tooling failure it is.
FLOOR = 0.50
if total and covered < total * FLOOR:
	sys.exit("frontend-sbom.sh: only %d/%d components resolved a licence — the\n"
	         "         licence pass did not work. Refusing to publish a document\n"
	         "         that would read as '%d unlicensed dependencies'."
	         % (covered, total, total - covered))
PY
