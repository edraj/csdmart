#!/usr/bin/env bash
# Scan the .NET runtime that is compiled INTO the AOT binary for known CVEs.
#
# WHY THIS IS NOT PART OF THE SECURITY GATE: the gate runs on a bare checkout
# and scans what is in git. The runtime is not in git, and it is not a
# PackageReference either — the SDK injects it — so it appears in NEITHER
# dist/deps/*.lock.json NOR the CycloneDX SBOM. The only artifact that records
# which runtime went in is the build's own *.deps.json, as
#
#     runtimepack.Microsoft.NETCore.App.Runtime.<rid>/<version>
#
# which trivy's dotnet-core analyzer reads. So this check has to run where a
# build has happened, not where the gate runs.
#
# WHY IT MATTERS MORE THAN A NORMAL DEPENDENCY: dmart publishes with
# PublishAot + PublishSelfContained, so the runtime ships inside /usr/bin/dmart.
# A user CANNOT patch it by updating their distro's .NET — it takes a rebuild
# and a re-release. v1.2.7 shipped runtime 10.0.10, carrying CVE-2026-62901
# (HIGH, DoS), CVE-2026-62899 and CVE-2026-62909. Nothing in CI noticed,
# because nothing was looking. This is the thing that looks.
#
# The fix for a finding here is almost always to bump dist/LOCKFILE_SDK: the
# runtime pack version follows the SDK, it is not something this repo pins
# directly.
#
# Usage:
#   dist/scan-runtime-cves.sh bin/Release/net10.0/linux-x64
#   dist/scan-runtime-cves.sh --severity CRITICAL path/to/dir-or-deps.json
#
# Exit codes:  0 clean   1 vulnerabilities found   2 cannot check
set -euo pipefail

TRIVY_VERSION="${TRIVY_VERSION:-0.74.0}"
SEVERITY="${SEVERITY:-HIGH,CRITICAL}"
TARGETS=()

while [ $# -gt 0 ]; do
	case "$1" in
		--severity) SEVERITY="$2"; shift 2 ;;
		-h|--help)
			awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"
			exit 0 ;;
		-*) echo "scan-runtime-cves.sh: unknown argument: $1 (try --help)" >&2; exit 2 ;;
		*) TARGETS+=("$1"); shift ;;
	esac
done

[ "${#TARGETS[@]}" -gt 0 ] || {
	echo "scan-runtime-cves.sh: give me a publish/build directory or a deps.json" >&2
	echo "        (try --help)" >&2
	exit 2
}

# Refuse to "pass" on an empty scan. A deps.json that is not there means the
# build layout moved, and reporting 0 findings for a tree we never read is the
# exact failure this script exists to prevent.
found=0
for t in "${TARGETS[@]}"; do
	[ -e "$t" ] || { echo "scan-runtime-cves.sh: $t does not exist" >&2; exit 2; }
	if [ -d "$t" ]; then
		n=$(find "$t" -name '*.deps.json' | wc -l)
	else
		case "$t" in *.deps.json) n=1 ;; *) n=0 ;; esac
	fi
	echo "== $t: $n deps.json"
	found=$((found + n))
done
if [ "$found" -eq 0 ]; then
	echo "scan-runtime-cves.sh: no *.deps.json under the given targets. Either the" >&2
	echo "        build did not run or its output layout changed — either way the" >&2
	echo "        runtime is going unscanned, which is not a pass." >&2
	exit 2
fi

# Pinned, and cached across invocations on a self-hosted runner. A supply-chain
# check whose own tool version floats is a check you cannot reason about.
# The release build runs this on both linux-x64 and linux-arm64 runners, and
# trivy ships a separate asset per architecture.
case "$(uname -m)" in
	x86_64|amd64)  TRIVY_ARCH="Linux-64bit" ;;
	aarch64|arm64) TRIVY_ARCH="Linux-ARM64" ;;
	*) echo "scan-runtime-cves.sh: no pinned trivy build for $(uname -m)" >&2; exit 2 ;;
esac

BINDIR="${TRIVY_CACHE_DIR:-${TMPDIR:-/tmp}/trivy-${TRIVY_VERSION}-${TRIVY_ARCH}}"
if [ ! -x "$BINDIR/trivy" ]; then
	echo "== fetching trivy $TRIVY_VERSION ($TRIVY_ARCH) into $BINDIR"
	mkdir -p "$BINDIR"
	TGZ="$(mktemp)"
	trap 'rm -f "$TGZ"' EXIT
	curl -fsSL "https://github.com/aquasecurity/trivy/releases/download/v${TRIVY_VERSION}/trivy_${TRIVY_VERSION}_${TRIVY_ARCH}.tar.gz" \
		-o "$TGZ" || { echo "scan-runtime-cves.sh: trivy download failed" >&2; exit 2; }
	tar xzf "$TGZ" -C "$BINDIR" trivy \
		|| { echo "scan-runtime-cves.sh: trivy extract failed" >&2; exit 2; }
fi

# --ignore-unfixed: a finding here is actionable only if there is a runtime to
# bump to. An unfixed runtime CVE is real, but it is not something this repo
# can resolve by moving a version, so it must not block a release.
rc=0
"$BINDIR/trivy" fs --scanners vuln --severity "$SEVERITY" --ignore-unfixed \
	--exit-code 1 --no-progress "${TARGETS[@]}" || rc=$?

if [ "$rc" -eq 0 ]; then
	echo "== no ${SEVERITY} runtime vulnerabilities"
	exit 0
fi

# trivy returns 1 both for findings and for its own failures, so say which this
# was rather than sending someone hunting for CVEs that were never reported.
if [ "$rc" -ne 1 ]; then
	echo "scan-runtime-cves.sh: trivy failed to run (exit $rc) — tool error, not a" >&2
	echo "        finding." >&2
	exit 2
fi

echo >&2
echo "scan-runtime-cves.sh: the runtime compiled into this binary has known" >&2
echo "        ${SEVERITY} vulnerabilities. Because the build is self-contained," >&2
echo "        shipping this means shipping them — a user cannot patch it." >&2
echo "        Fix: bump dist/LOCKFILE_SDK to an SDK carrying a fixed runtime" >&2
echo "        (the runtime pack version follows the SDK)." >&2
exit 1
