#!/usr/bin/env bash
#
# verify-release.sh — establish, from nothing but a release tag, that a csdmart
# release asset was built by our CI from the commit it claims.
#
# This script is written to be read by someone deciding whether to trust a
# binary, not only to be run. Every check below states what it proves. What the
# whole chain does NOT prove is set out in docs/VERIFYING-RELEASES.md — read
# that before treating a green run here as "the binary is safe".
#
# It exits non-zero if ANY check fails. It runs every check first and reports
# them together, because a security review wants the whole picture, not the
# first failure.
#
#   ./scripts/verify-release.sh --tag v1.2.6
#   ./scripts/verify-release.sh --tag v1.2.6 --require-hosted
#   ./scripts/verify-release.sh --tag v1.2.6 --repo someone/csdmart-fork
#
# Requirements: bash 4+, curl, sha256sum, jq, cosign (v2+), gh (v2.49+).
# The GitHub CLI does not need to be authenticated for a public repository.

set -uo pipefail

# ---------------------------------------------------------------- defaults --

REPO_DEFAULT="edraj/csdmart"
ISSUER="https://token.actions.githubusercontent.com"
# The only two workflows permitted to have signed anything on a release. A
# signature from any other workflow in the repo — including one added by
# someone who got a branch merged — fails verification.
WORKFLOWS_DEFAULT="release-verifiable.yml release.yml"
# Assets that MUST be present. Without this a release stripped of its binaries
# would "verify" perfectly by having nothing left to check.
REQUIRED_GLOBS_DEFAULT="dmart-*-linux-x64.tar.gz dmart-*-linux-arm64.tar.gz SHA256SUMS"

TAG=""
REPO="$REPO_DEFAULT"
WORKDIR=""
KEEP=0
OFFLINE=0
REQUIRE_HOSTED=0
COMMIT=""
NO_DOWNLOAD=0
WORKFLOWS="$WORKFLOWS_DEFAULT"
REQUIRED_GLOBS="$REQUIRED_GLOBS_DEFAULT"

usage() {
	# Print the leading comment block verbatim, stopping at the first line
	# that is not a comment, so the header and the help text cannot drift.
	awk 'NR>2 && /^#/ {sub(/^# ?/, ""); print; next} NR>2 {exit}' "$0"
	cat <<-EOF

	Options:
	  --tag <vX.Y.Z>     release tag to verify (required)
	  --repo <o/r>       repository the release belongs to (default: $REPO_DEFAULT)
	  --dir <path>       work directory for downloads (default: a temp dir)
	  --no-download      verify assets already present in --dir
	  --keep             keep the work directory on success
	  --commit <sha>     additionally require the provenance to name this commit
	  --require-hosted   fail any artifact built on a self-hosted runner
	  --offline          verify attestations from the bundled .jsonl assets rather
	                     than GitHub's API. Signature verification still reaches
	                     Rekor — see docs/VERIFYING-RELEASES.md
	  --workflows "a b"  workflow files allowed to have signed (default: $WORKFLOWS_DEFAULT)
	  -h, --help         this text
	EOF
}

while [ $# -gt 0 ]; do
	case "$1" in
		--tag)            TAG="$2"; shift 2 ;;
		--repo)           REPO="$2"; shift 2 ;;
		--dir)            WORKDIR="$2"; shift 2 ;;
		--no-download)    NO_DOWNLOAD=1; shift ;;
		--keep)           KEEP=1; shift ;;
		--commit)         COMMIT="$2"; shift 2 ;;
		--require-hosted) REQUIRE_HOSTED=1; shift ;;
		--offline)        OFFLINE=1; shift ;;
		--workflows)      WORKFLOWS="$2"; shift 2 ;;
		-h|--help)        usage; exit 0 ;;
		*) echo "verify-release.sh: unknown argument: $1 (try --help)" >&2; exit 2 ;;
	esac
done

[ -n "$TAG" ] || { echo "verify-release.sh: --tag is required" >&2; usage >&2; exit 2; }

# ------------------------------------------------------------- bookkeeping --

PASS=0
FAILED=0
declare -a FAILURES=()
declare -a NOTES=()

green() { printf '\033[32m%s\033[0m' "$1"; }
red()   { printf '\033[31m%s\033[0m' "$1"; }
if [ ! -t 1 ]; then green() { printf '%s' "$1"; }; red() { printf '%s' "$1"; }; fi

ok()   { PASS=$((PASS + 1)); printf '  [%s] %s\n' "$(green PASS)" "$1"; }
bad()  { FAILED=$((FAILED + 1)); FAILURES+=("$1"); printf '  [%s] %s\n' "$(red FAIL)" "$1"; }
note() { NOTES+=("$1"); printf '  [note] %s\n' "$1"; }
head2() { printf '\n== %s\n' "$1"; }

# ---------------------------------------------------------------- preflight --

head2 "preflight"
missing=""
for tool in curl sha256sum jq cosign gh; do
	command -v "$tool" >/dev/null 2>&1 || missing="$missing $tool"
done
if [ -n "$missing" ]; then
	echo "verify-release.sh: missing required tool(s):$missing" >&2
	echo "  cosign: https://docs.sigstore.dev/cosign/installation/" >&2
	echo "  gh:     https://cli.github.com/  (needs >= 2.49 for attestations)" >&2
	exit 2
fi
printf '  cosign %s\n' "$(cosign version 2>/dev/null | awk -F': *' '/GitVersion/{print $2; exit}')"
printf '  gh     %s\n' "$(gh --version 2>/dev/null | awk 'NR==1{print $3}')"
printf '  repo   %s\n  tag    %s\n' "$REPO" "$TAG"

# cosign's certificate-extension flags have moved between major versions.
# Probe rather than assume: a flag that silently does not exist would turn a
# hard identity check into no check at all.
COSIGN_HELP="$(cosign verify-blob --help 2>&1 || true)"
cosign_has() { printf '%s' "$COSIGN_HELP" | grep -q -- "$1"; }

# ------------------------------------------------------------------ assets --

if [ -z "$WORKDIR" ]; then
	WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/csdmart-verify-${TAG}.XXXXXX")"
fi
mkdir -p "$WORKDIR"
cd "$WORKDIR" || exit 2

# shellcheck disable=SC2317  # invoked via trap
cleanup() {
	if [ "$FAILED" -eq 0 ] && [ "$KEEP" -eq 0 ] && [ "$NO_DOWNLOAD" -eq 0 ]; then
		rm -rf "$WORKDIR"
	else
		printf '\nassets kept in: %s\n' "$WORKDIR"
	fi
}
trap cleanup EXIT

if [ "$NO_DOWNLOAD" -eq 0 ]; then
	head2 "downloading release assets"
	# gh first (works for private repos and respects an existing login), plain
	# curl second (a public release needs no credentials at all — which matters
	# when the person verifying is a downstream security reviewer with no
	# GitHub account).
	ASSET_JSON=""
	if ASSET_JSON="$(gh api "repos/$REPO/releases/tags/$TAG" 2>/dev/null)"; then
		:
	elif ASSET_JSON="$(curl -sSfL -H 'Accept: application/vnd.github+json' \
			"https://api.github.com/repos/$REPO/releases/tags/$TAG" 2>/dev/null)"; then
		:
	else
		echo "verify-release.sh: cannot read release $TAG in $REPO" >&2
		echo "  (wrong tag? private repo with no gh login? rate-limited?)" >&2
		exit 2
	fi
	COUNT=$(printf '%s' "$ASSET_JSON" | jq -r '.assets | length')
	if [ "${COUNT:-0}" -eq 0 ]; then
		echo "verify-release.sh: release $TAG carries no assets" >&2
		exit 1
	fi
	printf '%s' "$ASSET_JSON" | jq -r '.assets[] | .name + "\t" + .browser_download_url' \
	| while IFS=$'\t' read -r name url; do
		printf '  %s\n' "$name"
		curl -sSfL -o "$name" "$url" || { echo "download failed: $name" >&2; exit 1; }
	done || exit 1
	printf '  %s asset(s)\n' "$COUNT"
fi

shopt -s nullglob

# ------------------------------------------------------- required presence --

head2 "required assets are present"
for glob in $REQUIRED_GLOBS; do
	# shellcheck disable=SC2206 # deliberate glob expansion
	matches=( $glob )
	if [ ${#matches[@]} -eq 0 ]; then
		bad "missing required asset matching '$glob'"
	else
		ok "present: ${matches[*]}"
	fi
done

# Content assets are everything that is not signature material, a checksum
# manifest, or an attestation bundle. Every one of them must be signed: an
# unsigned asset sitting on a release is exactly the thing this script exists
# to catch, so it is a failure, never a skip.
declare -a CONTENT=()
for f in *; do
	[ -f "$f" ] || continue
	case "$f" in
		*.sig|*.pem|*.attestation.jsonl|SHA256SUMS|SHA256SUMS-*) continue ;;
	esac
	CONTENT+=("$f")
done

# --------------------------------------------------------------- checksums --

head2 "checksums"
for manifest in SHA256SUMS SHA256SUMS-all; do
	[ -f "$manifest" ] || continue
	if out=$(sha256sum -c "$manifest" 2>&1); then
		ok "$manifest: $(printf '%s' "$out" | grep -c ': OK') file(s) match"
	else
		bad "$manifest: checksum mismatch or missing file"
		printf '%s\n' "$out" | sed 's/^/        /'
	fi
done
# An asset that no manifest mentions has not been checksummed by anyone.
for f in "${CONTENT[@]}"; do
	if ! grep -qs -- " [ *]\?$f\$" SHA256SUMS SHA256SUMS-all; then
		note "$f is not listed in any SHA256SUMS manifest (its signature still is checked below)"
	fi
done

# ------------------------------------------------------------- signatures --

# Escape regex metacharacters so a repo or tag containing a dot cannot widen
# the identity we are pinning to.
# shellcheck disable=SC2016  # the sed program is deliberately literal
esc() { printf '%s' "$1" | sed 's/[.[\*^$()+?{|]/\\&/g'; }
REPO_RE="$(esc "$REPO")"
TAG_RE="$(esc "$TAG")"
WF_RE="$(printf '%s' "$WORKFLOWS" | tr ' ' '\n' | sed 's/\./\\./g' | paste -sd'|')"
IDENTITY_RE="^https://github\.com/${REPO_RE}/\.github/workflows/(${WF_RE})@refs/tags/${TAG_RE}\$"

head2 "cosign signatures (keyless, Fulcio certificate + Rekor)"
printf '  identity must match: %s\n' "$IDENTITY_RE"
printf '  OIDC issuer must be: %s\n' "$ISSUER"

verify_sig() {
	local f="$1"
	if [ ! -f "$f.sig" ] || [ ! -f "$f.pem" ]; then
		bad "$f: no signature material (.sig/.pem missing) — nothing vouches for this asset"
		return
	fi
	local -a args=(
		verify-blob
		--certificate "$f.pem"
		--signature "$f.sig"
		--certificate-identity-regexp "$IDENTITY_RE"
		--certificate-oidc-issuer "$ISSUER"
	)
	# Belt-and-suspenders on top of the SAN regex: these assert the same facts
	# from separate certificate extensions, so a certificate that satisfies the
	# SAN pattern but was issued for a different repository still fails.
	cosign_has '--certificate-github-workflow-repository' && \
		args+=(--certificate-github-workflow-repository "$REPO")
	cosign_has '--certificate-github-workflow-ref' && \
		args+=(--certificate-github-workflow-ref "refs/tags/$TAG")
	args+=("$f")
	if out=$(cosign "${args[@]}" 2>&1); then
		ok "$f: signature verified"
	else
		bad "$f: cosign verify-blob FAILED"
		printf '%s\n' "$out" | sed 's/^/        /'
	fi
}

for f in "${CONTENT[@]}"; do verify_sig "$f"; done
for m in SHA256SUMS SHA256SUMS-all; do [ -f "$m" ] && verify_sig "$m"; done

# ------------------------------------------------------------ attestations --

head2 "SLSA build provenance (gh attestation verify)"

verify_attestation() {
	local f="$1"
	local predicate="$2"
	local label="$3"
	local bundle="$f.attestation.jsonl"
	local -a args=(attestation verify "$f" --repo "$REPO"
		--cert-identity-regex "$IDENTITY_RE"
		--cert-oidc-issuer "$ISSUER"
		--source-ref "refs/tags/$TAG"
		--predicate-type "$predicate")
	[ -n "$COMMIT" ] && args+=(--source-digest "sha256:$COMMIT")
	[ "$REQUIRE_HOSTED" -eq 1 ] && args+=(--deny-self-hosted-runners)
	if [ "$OFFLINE" -eq 1 ]; then
		if [ ! -f "$bundle" ]; then
			bad "$f: --offline requested but $bundle is not on the release"
			return
		fi
		args+=(--bundle "$bundle")
	fi
	if out=$(gh "${args[@]}" 2>&1); then
		ok "$f: $label verified"
	else
		bad "$f: $label verification FAILED"
		printf '%s\n' "$out" | sed 's/^/        /'
	fi
}

# Only the shipped artefacts carry provenance. Signature material and SBOMs
# are covered by their subject's attestation and by the checksum manifest.
declare -a ARTIFACTS=()
for f in "${CONTENT[@]}"; do
	case "$f" in
		*.cdx.json) continue ;;
	esac
	ARTIFACTS+=("$f")
done

for f in "${ARTIFACTS[@]}"; do
	verify_attestation "$f" "https://slsa.dev/provenance/v1" "build provenance"
done

# SBOM attestations are only claimed for artifacts that have a .cdx.json
# sibling on the release; asserting them for the rest would fail for the wrong
# reason.
head2 "SBOM attestations (CycloneDX)"
sbom_seen=0
for f in "${ARTIFACTS[@]}"; do
	# Every SBOM on a release is named "<artifact>.cdx.json" — the workflows
	# rename it to match, so the pairing needs no knowledge of RIDs or of how
	# each package is named.
	if [ -f "$f.cdx.json" ]; then
		sbom_seen=1
		verify_attestation "$f" "https://cyclonedx.org/bom" "SBOM attestation"
		if jq -e '.bomFormat == "CycloneDX" and (.components | length) > 0' "$f.cdx.json" >/dev/null 2>&1; then
			ok "$f.cdx.json: CycloneDX, $(jq -r '.components | length' "$f.cdx.json") components"
		else
			bad "$f.cdx.json: not a CycloneDX document with components"
		fi
	fi
done
[ "$sbom_seen" -eq 1 ] || note "no .cdx.json assets on this release — nothing to check"

# ------------------------------------------------- what the provenance says --

# Not a pass/fail check: this reports the facts a reviewer has to weigh for
# themselves. Where the artifact was built (a GitHub-hosted runner or one of
# our own machines) is the single most load-bearing one, and it is invisible
# unless something prints it.
head2 "what the provenance actually says"
for f in "${ARTIFACTS[@]}"; do
	json=$(gh attestation verify "$f" --repo "$REPO" \
		--cert-identity-regex "$IDENTITY_RE" --cert-oidc-issuer "$ISSUER" \
		--format json 2>/dev/null)
	[ -n "$json" ] || { printf '  %-52s (provenance unreadable)\n' "$f"; continue; }
	env=$(printf '%s' "$json" | jq -r '[.. | .runnerEnvironment? // empty] | first // "unknown"')
	sha=$(printf '%s' "$json" | jq -r '[.. | .sourceRepositoryDigest? // empty] | first // "unknown"')
	wf=$(printf '%s' "$json"  | jq -r '[.. | .subjectAlternativeName? // empty] | first // "unknown"')
	printf '  %s\n    runner: %s\n    commit: %s\n    signer: %s\n' "$f" "$env" "$sha" "$wf"
	if [ "$env" = "self-hosted" ]; then
		note "$f was built on a SELF-HOSTED runner (see docs/VERIFYING-RELEASES.md — the 'not a workstation' claim is weaker for these)"
	fi
done

# ----------------------------------------------------------------- verdict --

head2 "verdict"
printf '  %s check(s) passed, %s failed\n' "$PASS" "$FAILED"
if [ ${#NOTES[@]} -gt 0 ]; then
	printf '\n  notes:\n'
	printf '    - %s\n' "${NOTES[@]}"
fi
if [ "$FAILED" -gt 0 ]; then
	printf '\n  %s — do not deploy these artifacts. Failures:\n' "$(red 'VERIFICATION FAILED')"
	printf '    - %s\n' "${FAILURES[@]}"
	printf '\n  What each failure means: docs/VERIFYING-RELEASES.md\n'
	exit 1
fi
printf '\n  %s — every asset on %s %s is checksummed, signed by a run of\n' "$(green VERIFIED)" "$REPO" "$TAG"
printf '  %s in this repository, and carries build provenance naming the tagged commit.\n' "$WORKFLOWS"
printf '  This does NOT establish that the source is safe, nor that the build is\n'
printf '  reproducible. See docs/VERIFYING-RELEASES.md.\n'
exit 0
