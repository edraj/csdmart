#!/bin/bash
set -e

# Modes:
#   default (fast)  — `dotnet build -c Release` — JIT apphost, ~5-30s.
#                     Produces bin/Release/net10.0/dmart (framework-dependent)
#                     and symlinks bin/dmart -> it so `./bin/dmart serve`
#                     works the same as in --aot mode. Needs `dotnet` on PATH
#                     at run time. Right for dev iteration.
#   --aot           — full AOT publish, ~3-4 minutes. ~45 MB native binary
#                     plus the e_sqlite3 shared library beside it (SQLitePCLRaw
#                     ships no static lib for non-wasm RIDs, so the SQLite
#                     backend cannot be linked in). Ship both files together.
#                     Right for release artifacts and CI / RPM packaging.
MODE="fast"
# Default RID is linux-x64; --rid overrides for cross-platform AOT publish
# (osx-arm64 on Apple Silicon, win-x64 on Windows, etc.). NativeAOT cannot
# cross-compile, so the host must match the requested RID's OS+arch.
RID="linux-x64"
# POSIX `[` rather than bash's `[[`: this file is run as `sh ./build.sh` from
# dist/build-apk.sh, and on a host whose /bin/sh is dash there is no `[[`. The
# failure is silent — a `while` condition is exempt from `set -e`, so the loop
# simply exits without parsing anything, leaving MODE at its default. That is
# how a `--print-version` call came back with a whole build log attached and
# fed it to MSBuild as a property value. Alpine hides this because busybox
# ships `[[` as an external applet.
while [ $# -gt 0 ]; do
  case "$1" in
    --aot|--full|--release) MODE="aot"; shift ;;
    --fast|--dev)           MODE="fast"; shift ;;
    --rid)                  RID="$2"; shift 2 ;;
    --rid=*)                RID="${1#*=}"; shift ;;
    --print-version)        MODE="print-version"; shift ;;
    -h|--help)
      cat <<-EOF
Usage: $0 [--aot] [--rid <runtime-id>]
  (default)         fast JIT build via \`dotnet build\` (~5-30s, dev iteration)
                    -> bin/dmart symlinks framework-dependent apphost
  --aot             full native AOT publish (~3-4m)
                    -> bin/dmart native binary + libe_sqlite3.so beside it
  --rid <runtime>   target runtime identifier for --aot publish
                    (default: linux-x64; e.g. osx-arm64, win-x64)
                    NativeAOT cannot cross-compile — host OS+arch must match
  --print-version   print the InformationalVersion string and exit, so a
                    caller that builds elsewhere (dist/build-apk.sh's
                    container) can resolve it here, where git works, and
                    pass it in via DMART_INFORMATIONAL_VERSION
Environment:
  DMART_INFORMATIONAL_VERSION
                    use this verbatim instead of asking git
EOF
      exit 0 ;;
    *) echo "Unknown arg: $1 (try --help)" >&2; exit 2 ;;
  esac
done

# Collect git metadata — baked into the binary via InformationalVersion.
# `git describe --tags --long` always emits "<tag>-<n>-g<sha>" — even when
# HEAD is exactly on the tag (n=0). Without --long, `git describe` collapses
# to just the tag name on tagged commits and the short SHA disappears from
# `dmart -v` output for release builds.
#
# DMART_INFORMATIONAL_VERSION short-circuits all of it. The aarch64 APK is
# published from a qemu-emulated container where git does not work, and the
# fallbacks below are individually silent — the release simply shipped
# "0.1.0 branch= date=" and every aarch64 binary since has been unable to
# identify itself. dist/build-apk.sh now resolves this on the host, where git
# is known good, and passes the answer in.
if [ -n "$DMART_INFORMATIONAL_VERSION" ]; then
  INFORMATIONAL_VERSION="$DMART_INFORMATIONAL_VERSION"
else
BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")
# actions/checkout lands on a detached HEAD for tag/release builds, so a raw
# `rev-parse --abbrev-ref` returns the literal string "HEAD". Prefer the
# workflow-supplied GITHUB_REF_NAME when that happens so `dmart -v` shows a
# useful label instead of "HEAD".
if [ "$BRANCH" = "HEAD" ] && [ -n "$GITHUB_REF_NAME" ]; then
  BRANCH="$GITHUB_REF_NAME"
fi
DESCRIBE=$(git describe --tags --always --long 2>/dev/null || echo "0.1.0")
VERSION_DATE=$(git show --pretty=format:%ad --date=iso -q 2>/dev/null | head -1 || echo "")
INFORMATIONAL_VERSION="${DESCRIBE} branch=${BRANCH} date=${VERSION_DATE}"

# Say so instead of shipping the fallback quietly. Losing the stamp is not
# fatal to the build, but it is invisible afterwards: the binary answers
# `--version` with 0.1.0 and there is nothing left to say why.
if [ "$DESCRIBE" = "0.1.0" ]; then
  echo "WARNING: git describe failed here — the binary will report version 0.1.0." >&2
  echo "         Set DMART_INFORMATIONAL_VERSION to stamp it explicitly." >&2
fi
fi

if [ "$MODE" = "print-version" ]; then
  printf '%s\n' "$INFORMATIONAL_VERSION"
  exit 0
fi

echo "Version: $INFORMATIONAL_VERSION"
echo "Mode:    $MODE"

# Build UI frontends (cxb + catalog, both embedded into the dmart binary).
# Each SPA is optional and tracked independently: a SPA "needs building" only
# when its source exists AND its dist output is missing. An absent source is
# fine (the csproj's EmbeddedResource glob simply matches zero files), which
# matches sparse checkouts or forks that ship only one of the two.
needs_build=false
[ -f cxb/package.json ]     && [ ! -f cxb/dist/client/index.html ]     && needs_build=true
[ -f catalog/package.json ] && [ ! -f catalog/dist/client/index.html ] && needs_build=true

if [ "$needs_build" = "false" ]; then
    echo "UI frontends ready (dists present or sources absent), skipping"
elif command -v yarn > /dev/null 2>&1 || command -v npm > /dev/null 2>&1; then
    echo "=== Building UI frontends ==="
    ./build-ui.sh || { echo "UI build failed"; exit 1; }
else
    echo "Error: UI dist missing and no yarn/npm on PATH." >&2
    echo "       Run ./build-ui.sh on the host (which has a JS toolchain)" >&2
    echo "       before invoking this build — dmart's RPM builder containers" >&2
    echo "       don't ship Node.js." >&2
    exit 1
fi

mkdir -p bin

if [ "$MODE" = "aot" ]; then
    # AOT publish the single binary (server + CLI client). RID is set
    # by the --rid flag (default linux-x64) parsed at the top of this file.
    echo "RID:     $RID"

    # AOT publish the single binary (server + CLI client)
    dotnet publish dmart.csproj -r "$RID" \
      -p:PublishAot=true \
      -p:StripSymbols=true \
      -p:InformationalVersion="$INFORMATIONAL_VERSION" \
      -c Release

    # Clean up dev-only files from publish output
    PUBLISH_DIR="bin/Release/net10.0/${RID}/publish"
    rm -f "$PUBLISH_DIR"/*.dbg "$PUBLISH_DIR"/*.pdb \
          "$PUBLISH_DIR"/*.Development.json \
          "$PUBLISH_DIR"/*.staticwebassets* \
          "$PUBLISH_DIR"/*.deps.json

    # Replace any prior bin/dmart (could be a symlink from a prior fast build)
    # with the freshly published AOT binary.
    rm -f bin/dmart
    cp "$PUBLISH_DIR/dmart" bin/

    # The SQLite backend's native library is NOT linked into the AOT binary —
    # SQLitePCLRaw ships a static .a only for browser-wasm, so every other RID
    # gets a shared object beside the executable. It must travel with the
    # binary: without it, the process aborts the first time DATABASE_DRIVER=sqlite
    # touches the database, and the failure looks like a missing entry point
    # rather than a missing file.
    if [ -f "$PUBLISH_DIR/libe_sqlite3.so" ]; then
        cp "$PUBLISH_DIR/libe_sqlite3.so" bin/
    elif [ -f "$PUBLISH_DIR/e_sqlite3.dll" ]; then
        cp "$PUBLISH_DIR/e_sqlite3.dll" bin/
    elif [ -f "$PUBLISH_DIR/libe_sqlite3.dylib" ]; then
        cp "$PUBLISH_DIR/libe_sqlite3.dylib" bin/
    else
        echo "WARNING: no e_sqlite3 native library found in $PUBLISH_DIR —" >&2
        echo "         DATABASE_DRIVER=sqlite will fail at runtime." >&2
    fi

    echo ""
    echo "Published (AOT) to $PUBLISH_DIR/"
    ls -lh "$PUBLISH_DIR/dmart"
    du -sh "$PUBLISH_DIR/"
    echo ""
    echo "Binary copied to bin/ (with the e_sqlite3 native library beside it):"
    ls -lh bin/dmart
else
    # Fast JIT build — no AOT codegen, no -r RID, no publish. PublishAot=true
    # in the csproj only kicks in during `dotnet publish`, so plain `build`
    # produces a framework-dependent apphost in seconds.
    dotnet build dmart.csproj \
      -p:InformationalVersion="$INFORMATIONAL_VERSION" \
      -c Release

    BUILD_DIR="bin/Release/net10.0"

    # Symlink bin/dmart to the apphost so callers keep the same calling
    # convention (`./bin/dmart serve …`) regardless of mode. The apphost
    # resolves its DLL via realpath(argv[0]), so the symlink hop is fine.
    # rm -f handles the prior-AOT case where bin/dmart is a 40 MB regular file.
    rm -f bin/dmart
    ln -s "$(pwd)/$BUILD_DIR/dmart" bin/dmart

    echo ""
    echo "Built (JIT) at $BUILD_DIR/"
    ls -lh "$BUILD_DIR/dmart"
    echo ""
    echo "bin/dmart -> $BUILD_DIR/dmart"
    ls -lh bin/dmart
fi
