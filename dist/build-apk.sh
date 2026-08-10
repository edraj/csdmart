#!/bin/bash
set -e

# Build the dmart APK package for Alpine.
#
# On an Alpine host whose arch matches the target and which already has the
# toolchain, this builds natively. Everywhere else it runs the same steps
# inside an `mcr.microsoft.com/dotnet/sdk:10.0-alpine` container, so the
# resulting binary is musl-linked either way. That is not a convenience
# toggle: the release workflow builds both arches on Ubuntu runners, where
# the container is the only way to produce a musl package at all.
#
# Usage:
#   ./dist/build-apk.sh                       # x86_64 (default)
#   ./dist/build-apk.sh --arch x86_64
#   ./dist/build-apk.sh --arch aarch64
#   ./dist/build-apk.sh --native              # force native, fail if unfit
#   ./dist/build-apk.sh --container           # force container
#   VERSION=1.2.3 ./dist/build-apk.sh         # explicit version override
#
# Host requirements:
#   - Native mode: Alpine, host arch == target arch, and
#     `apk add alpine-sdk clang lld zlib-dev zlib-static git jq` plus a
#     .NET 10 SDK. Auto-detected; the script falls back to a container
#     rather than failing when any of that is absent.
#   - Container mode: podman OR docker (CONTAINER_ENGINE env overrides)
#   - UI dists pre-built (cxb/dist/client + catalog/dist/client) or
#     yarn/npm on PATH for ./build-ui.sh to run
#   - For cross-arch container builds (host arch != target arch),
#     qemu-user-static must be registered with binfmt_misc. The script
#     auto-detects and passes --platform to the container engine.

cd "$(dirname "$0")/.."
SRCDIR="$(pwd)"

ARCH="x86_64"
BUILD_MODE="auto"
while [[ $# -gt 0 ]]; do
	case "$1" in
		--arch)   ARCH="$2"; shift 2 ;;
		--arch=*) ARCH="${1#*=}"; shift ;;
		--native)    BUILD_MODE="native"; shift ;;
		--container) BUILD_MODE="container"; shift ;;
		-h|--help)
			cat <<-EOF
			Usage: $0 [--arch x86_64|aarch64] [--native|--container]
			  --arch x86_64     produce dmart-<v>-x86_64.apk (linux-musl-x64)
			  --arch aarch64    produce dmart-<v>-aarch64.apk (linux-musl-arm64)
			  --native          force a host build (errors if host is unfit)
			  --container       force a containerized build
			Default is native on a matching-arch Alpine host with the
			toolchain installed, container otherwise.
			Environment:
			  VERSION           explicit version (default: from git describe)
			  CONTAINER_ENGINE  podman | docker (default: podman)
			EOF
			exit 0 ;;
		*) echo "Unknown arg: $1 (try --help)" >&2; exit 2 ;;
	esac
done

# Map Alpine arch → .NET RID → OCI platform string.
# Alpine uses x86_64/aarch64; .NET uses x64/arm64; OCI uses amd64/arm64.
case "$ARCH" in
	x86_64)  RID="linux-musl-x64";   OCI_PLATFORM="linux/amd64" ;;
	aarch64) RID="linux-musl-arm64"; OCI_PLATFORM="linux/arm64" ;;
	*) echo "Unsupported arch: $ARCH (use x86_64 or aarch64)" >&2; exit 2 ;;
esac

# Version derivation — mirrors dist/build-rpm.sh so RPMs and APKs cut
# from the same commit get identical version strings.
if [ -z "$VERSION" ]; then
	GIT_DESC=$(git describe --tags 2>/dev/null || echo "v0.1.0")
	BASE_VER=$(echo "$GIT_DESC" | cut -d '-' -f 1 | sed 's/^v//')
	MINOR=$(echo "$GIT_DESC" | cut -d '-' -f 2 -s)
	VERSION="${BASE_VER}${MINOR:+.$MINOR}"
fi

HOST_ARCH=$(uname -m)

# Native needs all of: an Alpine host (musl + apk + abuild), a host arch
# matching the target (we do not cross-compile outside a container), and
# the toolchain already installed — native mode deliberately does not
# `apk add` anything, since that would need root for a plain local build.
native_ready() {
	[ -f /etc/alpine-release ] || return 1
	[ "$HOST_ARCH" = "$ARCH" ] || return 1
	for t in abuild abuild-keygen clang ld.lld dotnet git jq; do
		command -v "$t" >/dev/null 2>&1 || return 1
	done
	# NativeAOT statically links zlib for System.IO.Compression, so the
	# .a has to be there and not just zlib-dev's headers.
	[ -f /usr/lib/libz.a ] || return 1
	return 0
}

if [ "$BUILD_MODE" = "auto" ]; then
	if native_ready; then BUILD_MODE="native"; else BUILD_MODE="container"; fi
elif [ "$BUILD_MODE" = "native" ] && ! native_ready; then
	echo "--native requested but this host cannot build natively." >&2
	echo "Needs: Alpine, host arch ($HOST_ARCH) == target ($ARCH), a .NET 10 SDK, and" >&2
	echo "  apk add alpine-sdk clang lld zlib-dev zlib-static git jq" >&2
	exit 1
fi

echo "Building dmart-${VERSION} APK for ${ARCH} (${BUILD_MODE})..."

# Resolve the InformationalVersion here rather than letting build.sh ask git
# from wherever it ends up running. The aarch64 APK is produced in a
# qemu-emulated container where git does not work, and build.sh's fallbacks
# are silent: every aarch64 release so far has shipped a binary that answers
# `--version` with "0.1.0 branch= date=". The host always has a working git —
# VERSION above is already derived from `git describe` — so ask build.sh here
# and hand the result to whichever environment does the build.
if [ -n "$DMART_INFORMATIONAL_VERSION" ]; then
	export DMART_INFORMATIONAL_VERSION
else
	# A plain assignment, deliberately not `export VAR=$(...)`: with the
	# latter the exit status is export's own, always 0, so `set -e` would not
	# notice build.sh failing and we would hand the build an empty string —
	# which it treats as unset and answers by asking git itself, landing right
	# back on the failure this exists to avoid.
	host_version=$(sh ./build.sh --print-version)

	# If the host cannot resolve a version either, pass nothing rather than a
	# known-bad answer: an environment that still has a working git deserves
	# its own attempt instead of being overridden with the fallback.
	case "$host_version" in
		0.1.0*)
			echo "WARNING: no version resolvable on this host; leaving it to the build environment." >&2
			;;
		*)
			export DMART_INFORMATIONAL_VERSION="$host_version"
			;;
	esac
fi
# An `[ ... ] && echo` one-liner would be wrong here: when the test is false
# the list exits non-zero, and `set -e` at the top of this script takes that
# as a build failure.
if [ -n "$DMART_INFORMATIONAL_VERSION" ]; then
	echo "Stamping version: $DMART_INFORMATIONAL_VERSION"
fi

# UI dists must exist before the build runs — the Alpine SDK image has no
# Node.js toolchain, and a native build has no reason to assume one either.
# Build on the host if missing; CI pre-extracts from the shared ui-tarballs
# artifact and skips this path.
needs_ui=false
[ -f cxb/package.json ]     && [ ! -f cxb/dist/client/index.html ]     && needs_ui=true
[ -f catalog/package.json ] && [ ! -f catalog/dist/client/index.html ] && needs_ui=true
if [ "$needs_ui" = true ]; then
	echo "Building UI frontends locally (pre-build)..."
	./build-ui.sh || { echo "UI build failed" >&2; exit 1; }
else
	echo "UI frontends ready (dists present or sources absent), skipping"
fi

mkdir -p dist/out

# Wipe prior bin/obj for this RID so a stale linux-x64 build doesn't
# leak into the musl publish. Other RIDs' outputs are left alone.
rm -rf "bin/Release/net10.0/${RID}" "obj/Release/net10.0/${RID}"

# The build proper: AOT publish → stage APKBUILD inputs → abuild → copy the
# .apk out. Held in one variable so the native and container paths run
# byte-identical steps and cannot drift. Everything it varies on arrives as
# an environment variable: VERSION, ARCH, RID, APK_OUTDIR, and
# INSTALL_TOOLCHAIN (set only for the container, which starts from a bare
# SDK image).
#
# APK_OUTDIR is deliberately not called OUTDIR. MSBuild reads the process
# environment as global properties and matches names case-insensitively, so
# an exported OUTDIR silently becomes the OutDir property and redirects the
# entire build — publish output lands in that directory instead of
# bin/Release/net10.0/$RID/publish, and the copy below then finds nothing.
BUILD_SCRIPT=$(cat <<'INNER'
set -e

# The container starts from a bare SDK image and is root, so it installs
# its own toolchain. A native build refuses to touch the host's packages
# and relies on the caller's native_ready() check instead.
#
#   alpine-sdk  — abuild, build-base, fakeroot
#   clang + lld — the NativeAOT linker toolchain
#   zlib-static — NativeAOT statically links zlib for System.IO.Compression;
#                 zlib-dev alone gives headers but not the .a archive, and
#                 the link then fails with a misleading "cannot find -lz"
#   git         — ./build.sh git-describe version stamping
#   jq          — the runtime dep declared in APKBUILD; abuild folds runtime
#                 deps into its builddeps virtual package for validation,
#                 so pre-installing avoids a second apk round-trip in -r
if [ -n "$INSTALL_TOOLCHAIN" ]; then
	apk add --no-cache --quiet \
		abuild alpine-sdk clang lld zlib-dev zlib-static git jq
fi

# AOT publish. build.sh handles the InformationalVersion stamping
# (git describe + branch + date) so the same logic runs in CI, local
# builds, and inside the container.
sh ./build.sh --aot --rid "$RID"

# Stage APKBUILD inputs in a clean directory. abuild treats the parent
# dir name as the repo name; calling it "apkbuild" gives us a predictable
# path under $HOME/packages later.
APKROOT="${TMPDIR:-/tmp}/apkbuild"
rm -rf "$APKROOT" && mkdir -p "$APKROOT"

cp "bin/Release/net10.0/$RID/publish/dmart" "$APKROOT/dmart"
cp dist/dmart.service                       "$APKROOT/"
cp dist/apk/dmart.openrc-init               "$APKROOT/"
cp dist/dmart.bash dist/dmart.fish          "$APKROOT/"
cp config.env.sample                        "$APKROOT/"

# Plugin configs bundled as a tarball so the APKBUILD has one named
# source entry instead of a moving glob. Extracted into
# /usr/lib/dmart/plugins/ inside package().
tar -czf "$APKROOT/plugins.tar.gz" -C plugins .

# Render APKBUILD from template with version + arch.
sed -e "s|__VERSION__|$VERSION|g" \
    -e "s|__ARCH__|$ARCH|g" \
    dist/apk/APKBUILD.in > "$APKROOT/APKBUILD"

# Install scripts — names follow Alpine convention so abuild packs them
# as triggers when listed in $install.
cp dist/apk/dmart.pre-install dist/apk/dmart.post-install \
   dist/apk/dmart.post-deinstall \
   "$APKROOT/"

# apk runs post-upgrade (not post-install) when replacing an installed
# version, and does not fall back between the two. Ship the same script
# under both names rather than keeping a second copy in git that would
# drift from the first.
cp dist/apk/dmart.post-install "$APKROOT/dmart.post-upgrade"

cd "$APKROOT"

# Signing key. Ephemeral in the container (fresh image every run, never
# persisted); on a host it is created once and reused, because -a appends
# to abuild.conf and re-running would stack duplicate keys forever.
# Downloaders use `apk add --allow-untrusted dmart-*.apk` either way.
# -n = no email prompt.
if ! grep -qs '^PACKAGER_PRIVKEY=' "$HOME/.abuild/abuild.conf"; then
	abuild-keygen -a -n >/dev/null
fi

# Trusting our own key lets abuild sign the repo index without apk
# complaining. Only possible as root, and only the container is.
if [ "$(id -u)" = 0 ]; then
	cp "$HOME"/.abuild/*.rsa.pub /etc/apk/keys/ 2>/dev/null || true
fi

# Keep abuild's output inside $HOME regardless of what abuild.conf says,
# so the find below has one place to look on both paths.
REPODEST="$HOME/packages"
export REPODEST

# -F force-runs abuild as root, which it otherwise refuses; that is the
# documented container/CI escape hatch and is neither needed nor accepted
# as a normal user. -r installs missing builddeps via apk, which also
# needs root — natively the toolchain is already present by construction.
if [ "$(id -u)" = 0 ]; then
	abuild -F checksum
	abuild -F -r
else
	# Naming targets rather than letting `abuild` run its default `all`.
	# The last thing `all` does is rebuild and sign the repository index,
	# which requires this build's throwaway public key to be in
	# /etc/apk/keys — root-only. Unprivileged, that step fails with
	# "UNTRUSTED signature" *after* the .apk is already sitting complete in
	# REPODEST, turning a finished build into a non-zero exit. We consume
	# the .apk file directly and never serve it from a repo, so the index
	# is nothing we need.
	abuild checksum
	abuild unpack prepare rootpkg
fi

# abuild drops the .apk at $REPODEST/<repo>/<arch>/, where the repo name
# is the *grandparent* of APKBUILD rather than its parent. Find by name
# instead of hard-coding that, so the layout convention is not load-bearing.
APK_OUT=$(find "$REPODEST" -type f -name "dmart-$VERSION-r0.apk" | head -1)
[ -n "$APK_OUT" ] || {
	echo "No dmart-$VERSION-r0.apk under $REPODEST:"
	find "$REPODEST" -type f 2>/dev/null
	exit 1
}

# Final name embeds the arch so the two release jobs do not collide when
# both upload to the same release tag.
cp "$APK_OUT" "$APK_OUTDIR/dmart-$VERSION-$ARCH.apk"
INNER
)

if [ "$BUILD_MODE" = "native" ]; then
	# apk-tools installs to /sbin, which is on root's PATH but not on a
	# normal login user's. abuild shells out to `apk --print-arch` to derive
	# CBUILD and dies with "Unable to deduce build architecture. Install
	# apk-tools, or set CBUILD." when it cannot find it — confusing advice,
	# since apk-tools is installed. The container never hits this because it
	# runs as root.
	VERSION="$VERSION" ARCH="$ARCH" RID="$RID" \
	DMART_INFORMATIONAL_VERSION="$DMART_INFORMATIONAL_VERSION" \
	APK_OUTDIR="$SRCDIR/dist/out" \
	PATH="/sbin:/usr/sbin:$PATH" \
		sh -c "$BUILD_SCRIPT"
else
	ENGINE="${CONTAINER_ENGINE:-podman}"
	command -v "$ENGINE" >/dev/null 2>&1 || {
		echo "Container engine '$ENGINE' not found on PATH" >&2; exit 1;
	}

	# Auto-detect cross-arch and pass --platform only when needed. On CI
	# (self-hosted x86_64 → x86_64 apk, ubuntu-24.04-arm → aarch64 apk)
	# host == target, so this is a no-op and there's no QEMU overhead.
	PLATFORM_FLAG=""
	if [ "$HOST_ARCH" != "$ARCH" ]; then
		PLATFORM_FLAG="--platform=${OCI_PLATFORM}"
		echo "Cross-arch build: host=$HOST_ARCH target=$ARCH (using QEMU via $PLATFORM_FLAG)"
	fi

	# Persistent NuGet cache shared with host-side dotnet so cold runs
	# don't re-download every package. Matches dist/build-rpm.sh's pattern.
	HOST_NUGET_CACHE="${HOME}/.nuget/packages"
	mkdir -p "$HOST_NUGET_CACHE"

	$ENGINE run --rm $PLATFORM_FLAG \
		--network=host \
		-v "${SRCDIR}:/src:z" \
		-v "${HOST_NUGET_CACHE}:/nuget-packages:z" \
		-e VERSION="$VERSION" \
		-e DMART_INFORMATIONAL_VERSION="$DMART_INFORMATIONAL_VERSION" \
		-e ARCH="$ARCH" \
		-e RID="$RID" \
		-e APK_OUTDIR=/src/dist/out \
		-e INSTALL_TOOLCHAIN=1 \
		-e NUGET_PACKAGES=/nuget-packages \
		-e HOME=/root \
		-w /src \
		mcr.microsoft.com/dotnet/sdk:10.0-alpine \
		sh -c "$BUILD_SCRIPT"
fi

echo ""
echo "=== APK built (${BUILD_MODE}) ==="
ls -lh "dist/out/dmart-${VERSION}-${ARCH}.apk"
