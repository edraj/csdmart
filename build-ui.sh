#!/bin/bash
# Build the in-tree UI frontends:
#   * cxb/     — Svelte SPA, dist/client/ output (SvelteKit convention)
#   * catalog/ — Svelte + Vite SPA, dist/client/ output
#
# Both are embedded into the single dmart binary via EmbeddedResource
# entries in dmart.csproj. Missing sources are skipped gracefully so a
# repo clone without one of them still builds.
#
# cxb and catalog are wired together as yarn workspaces in the repo-root
# package.json, so a single install at the root populates one hoisted
# node_modules tree shared by both. That's why this script does just one
# yarn install instead of one per project.
#
# Usage: ./build-ui.sh
# Prerequisites: yarn (classic) available

set -eu

cd "$(dirname "$0")"

# Single hoisted install at the workspace root — replaces the previous
# per-project loop and the "sequential because of yarn 1 cache races"
# workaround it needed.
if [ -f "package.json" ]; then
    echo "Installing deps (workspace root)..."
    if command -v yarn > /dev/null 2>&1; then
        yarn install
    else
        npm install
    fi
fi

build_one() {
    local name="$1"
    local dir="$2"
    local out_dir="$3"

    if [ ! -f "$dir/package.json" ]; then
        echo "Skipping $name build — $dir/package.json not found" >&2
        return 0
    fi

    echo "Building $name frontend..."
    (
        cd "$dir"
        if command -v yarn > /dev/null 2>&1; then
            yarn build
        else
            npm run build
        fi
    )

    if [ -d "$dir/$out_dir" ]; then
        local count size
        count=$(find "$dir/$out_dir" -type f | wc -l)
        size=$(du -sh "$dir/$out_dir" | cut -f1)
        echo "Done ($name). $count files ($size) under $dir/$out_dir"
    else
        echo "Warning: $dir/$out_dir not produced by build" >&2
    fi
}

# Build in parallel — each `yarn build` only writes to its own project's
# dist, never to the shared yarn cache or hoisted node_modules, so true
# parallelism is safe and shaves roughly the build duration of one project
# off the wall clock.
build_one "CXB"     "cxb"     "dist/client" &
pid_cxb=$!
build_one "catalog" "catalog" "dist/client" &
pid_catalog=$!

set +e
wait "$pid_cxb";     status_cxb=$?
wait "$pid_catalog"; status_catalog=$?
set -e

if [ "$status_cxb" -ne 0 ] || [ "$status_catalog" -ne 0 ]; then
    echo "UI build failed (cxb=$status_cxb catalog=$status_catalog)" >&2
    exit 1
fi

echo "Run './build.sh' to embed the UI outputs into the binary."
