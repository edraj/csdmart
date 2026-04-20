#!/bin/bash
# Build the in-tree UI frontends:
#   * cxb/     — Svelte SPA, dist/client/ output (SvelteKit convention)
#   * catalog/ — Svelte + Vite SPA, dist/ output
#
# Both are embedded into the single dmart binary via EmbeddedResource
# entries in dmart.csproj. Missing sources are skipped gracefully so a
# repo clone without one of them still builds.
#
# Usage: ./build-ui.sh
# Prerequisites: yarn (or npm) available

set -eu

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
            yarn install
            yarn build
        else
            npm install
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

build_one "CXB" "cxb" "dist/client"
build_one "catalog" "catalog" "dist"

echo "Run './build.sh' to embed the UI outputs into the binary."
