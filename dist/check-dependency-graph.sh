#!/usr/bin/env bash
# Record and verify the resolved NuGet dependency graph — every direct and
# transitive package, with content hashes — as a reviewable file in git.
#
# WHY NOT A COMMITTED packages.lock.json, which is what this obviously wants to
# be: a lock file is a build input, and this repository builds on SDKs that
# cannot agree on one. Two independent failures, both observed in CI rather
# than theorised:
#
#   NU1403  A lock file stores content hashes. Distro .NET builds (the Fedora
#           self-hosted runners, the AlmaLinux 9 container) resolve the
#           SDK-injected Microsoft.DotNet.ILCompiler and
#           Microsoft.NET.ILLink.Tasks from a local library-packs folder whose
#           hashes differ from nuget.org's. Hash validation then fails on every
#           restore, not only in locked mode.
#
#   NU1004  A lock file also stores the VERSIONS of those SDK-injected
#           packages, which follow the SDK. AlmaLinux 9 ships 10.0.111, the
#           dotnet/sdk:10.0 images ship 10.0.400, this repo's pinned SDK is in
#           dist/LOCKFILE_SDK. One patch apart is enough to invalidate it.
#
# So the graph is recorded OUTSIDE the build, under dist/deps/, where MSBuild
# never looks. It is generated here, in one place, on one pinned SDK. The
# result: a dependency change still lands as a diff a reviewer sees, and a
# release still verifies that its SBOM describes the graph in git — without any
# of it reaching a builder.
#
# The SDK's OWN packages are stripped from the record. Microsoft.DotNet.
# ILCompiler and Microsoft.NET.ILLink.Tasks arrive with the SDK, not from this
# project, and the same version resolves to different BYTES depending on where
# the SDK came from: a Fedora or AlmaLinux dotnet serves them out of a local
# library-packs folder, a Microsoft tarball out of nuget.org. Recording them
# would mean the file only ever matched the machine that wrote it. They are
# build tooling and they never ship, so what is left here is the set of
# packages that does.
#
# (An earlier version of this comment also claimed they never appear in the
# SBOM. That is not true: v1.2.7's published SBOM lists
# runtime.linux-x64.Microsoft.DotNet.ILCompiler 10.0.10. What the SBOM was
# missing is the opposite thing — the runtime packs that DO ship, compiled
# into the AOT binary. dist/sbom.sh adds those now.)
#
# Exit codes:  0 in sync   1 drift found   2 cannot check
set -euo pipefail

cd "$(dirname "$0")/.."

OUT="dist/deps"

EXPECTED_SDK="$(cat dist/LOCKFILE_SDK)"
ACTUAL_SDK="$(dotnet --version)"
command -v jq >/dev/null 2>&1 || {
	echo "check-dependency-graph: jq is required (it strips the SDK's own" >&2
	echo "        packages out of the record)." >&2
	exit 2
}

# A warning, not a failure. With the SDK's own packages stripped, the record is
# portable across SDKs, so a contributor on a distro dotnet can still run this.
# CI pins the SDK anyway (actions/setup-dotnet + DOTNET_INSTALL_DIR), which is
# what keeps the checked-in file deterministic.
if [ "$ACTUAL_SDK" != "$EXPECTED_SDK" ]; then
	echo "check-dependency-graph: note — SDK is $ACTUAL_SDK, the record was" >&2
	echo "        written by $EXPECTED_SDK (dist/LOCKFILE_SDK). That should not" >&2
	echo "        matter; if you see drift you did not cause, it might." >&2
fi

# Restore in a pristine copy of the tracked tree rather than in place.
# Two reasons, both learned the hard way:
#   * A leftover obj/ from an earlier RID-specific publish changes what restore
#     writes into the graph, so the same commit could record differently on two
#     machines. CI always has a clean checkout; a developer never does.
#   * Generating packages.lock.json files in the working tree is exactly the
#     thing that breaks distro builds if one is left behind.
# `git ls-files` gives the tracked files AS THEY ARE in the worktree, so an
# uncommitted csproj edit — the usual reason to run this — is still what gets
# measured. bin/ and obj/ are gitignored and therefore excluded.
WORK="$(mktemp -d "${TMPDIR:-/tmp}/dmart-depgraph.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
git ls-files -z | while IFS= read -r -d '' f; do
	mkdir -p "$WORK/$(dirname "$f")"
	cp "$f" "$WORK/$f"
done

echo "== restoring with SDK $ACTUAL_SDK in $WORK"
LOCK=-p:RestorePackagesWithLockFile=true
(
	cd "$WORK"
	dotnet restore dmart.slnx $LOCK --nologo
	# Not in the solution, but they are shipped SDK samples and their
	# dependencies deserve the same scrutiny.
	dotnet restore custom_plugins_sdk/sample_hook/sample_hook.csproj $LOCK --nologo
	dotnet restore custom_plugins_sdk/sample_api/sample_api.csproj $LOCK --nologo
)

mkdir -p "$OUT"
rm -f "$OUT"/*.lock.json
while IFS= read -r f; do
	rel="${f#./}"
	dir="$(dirname "$rel")"
	if [ "$dir" = "." ]; then
		slug="dmart"
	else
		slug="$(printf '%s' "$dir" | tr '/' '_')"
	fi
	# Strip the SDK's own packages (see the header) and normalise key order so
	# the file is a function of the graph, not of restore's iteration order.
	jq -S '.dependencies |= with_entries(
	         .value |= with_entries(
	           select(.key | test("^(Microsoft\\.DotNet\\.ILCompiler|Microsoft\\.NET\\.ILLink\\.Tasks|runtime\\..*\\.Microsoft\\.DotNet\\.ILCompiler)$") | not)))' \
	   "$WORK/$f" > "$OUT/$slug.lock.json"
done < <(cd "$WORK" && find . -name packages.lock.json | sort)

COUNT=$(find "$OUT" -name '*.lock.json' | wc -l)
if [ "$COUNT" -lt 6 ]; then
	echo "check-dependency-graph: only $COUNT project graphs recorded — expected" >&2
	echo "        at least 6. A project stopped restoring; that is a real change." >&2
	exit 1
fi
echo "== recorded $COUNT project graphs under $OUT"

# `git status --porcelain`, not `git diff`: a diff only sees files git already
# tracks, so a graph recorded for a NEW project would be untracked and the
# check would pass while saying nothing about it.
if [ -z "$(git status --porcelain -- "$OUT")" ]; then
	echo "== dependency graph is unchanged"
	exit 0
fi

echo >&2
echo "check-dependency-graph: the recorded dependency graph does not match this" >&2
echo "        restore. A package changed — which is exactly what this file" >&2
echo "        exists to make visible. Diff:" >&2
echo >&2
git --no-pager status --short -- "$OUT" >&2
git --no-pager diff -- "$OUT" | head -80 >&2
echo >&2
echo "Fix: if the change is intended, commit $OUT — that diff is the review." >&2
exit 1
