#!/usr/bin/env bash
# Fail if the committed packages.lock.json files do not match what a restore
# resolves right now.
#
# This is where lock-file enforcement lives, rather than in RestoreLockedMode.
# A lock file records two package references the SDK injects rather than the
# project declaring them (Microsoft.DotNet.ILCompiler,
# Microsoft.NET.ILLink.Tasks) at the version bundled with whichever SDK ran the
# restore — so a lock file written by SDK 10.0.110 fails NU1004 under 10.0.111
# or 10.0.400, both of which this repo builds on. Locked mode therefore cannot
# be switched on across the packaging containers.
#
# A drift check can be, because it runs in ONE place with ONE pinned SDK. Run
# it on a hosted runner with actions/setup-dotnet pinned to the version named
# in dist/LOCKFILE_SDK, and it answers the question that actually matters:
# does the dependency graph in git still describe what a build resolves?
#
# When it fails, the fix is to restore locally with that same SDK and commit
# the resulting diff — the diff IS the review artefact.
#
# Exit codes:  0 in sync   1 drift found   2 cannot check (wrong SDK, dirty tree)
set -euo pipefail

cd "$(dirname "$0")/.."

EXPECTED_SDK="$(cat dist/LOCKFILE_SDK)"
ACTUAL_SDK="$(dotnet --version)"
if [ "$ACTUAL_SDK" != "$EXPECTED_SDK" ]; then
	echo "check-lockfiles: SDK is $ACTUAL_SDK, but the lock files were written" >&2
	echo "                 by $EXPECTED_SDK (dist/LOCKFILE_SDK)." >&2
	echo "                 The SDK pins ILCompiler/ILLink.Tasks in the lock" >&2
	echo "                 file, so any other version reports false drift." >&2
	exit 2
fi

if ! git diff --quiet -- '*packages.lock.json'; then
	echo "check-lockfiles: packages.lock.json files are already modified in the" >&2
	echo "                 working tree, so there is nothing to compare against." >&2
	echo >&2
	echo "  If a previous run of this script regenerated them, that diff IS the" >&2
	echo "  dependency change — review and commit it:" >&2
	echo "      git diff -- '*packages.lock.json'" >&2
	echo "  Otherwise stash the changes and re-run to check the committed state." >&2
	exit 2
fi

echo "== restoring with SDK $ACTUAL_SDK"
dotnet restore dmart.slnx --nologo
# Not in the solution, but they carry lock files too.
dotnet restore custom_plugins_sdk/sample_hook/sample_hook.csproj --nologo
dotnet restore custom_plugins_sdk/sample_api/sample_api.csproj --nologo

if git diff --quiet -- '*packages.lock.json'; then
	echo "== lock files are in sync"
	exit 0
fi

echo >&2
echo "check-lockfiles: the committed lock files do not match this restore." >&2
echo "                 A dependency changed without the lock file being" >&2
echo "                 updated — which is exactly what the lock file exists" >&2
echo "                 to make visible. Diff:" >&2
echo >&2
git --no-pager diff --stat -- '*packages.lock.json' >&2
git --no-pager diff -- '*packages.lock.json' | head -80 >&2
echo >&2
echo "Fix: install .NET SDK $EXPECTED_SDK, run this script locally, and commit" >&2
echo "     the resulting lock file changes." >&2
exit 1
