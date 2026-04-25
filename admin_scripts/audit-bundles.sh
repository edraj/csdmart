#!/bin/bash
# Audit cxb + catalog bundles for prismjs-shaped landmines.
#
# Scans each built chunk for module-top-level identifier references that
# resolve to nothing in scope and aren't a known browser/Node global.
# These are the patterns that crash at chunk load with `ReferenceError:
# Foo is not defined` (the prism-init bug from v0.8.16).
#
# Usage: ./admin_scripts/audit-bundles.sh
# Prerequisite: ./build-ui.sh has produced cxb/dist/client and catalog/dist/client.
#
# Exits 0 only when every chunk is clean. Each finding prints as
#   <chunk-path>: <identifier> @ line:col

set -eu

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

if ! [ -d node_modules/acorn ] || ! [ -d node_modules/acorn-walk ]; then
    echo "acorn / acorn-walk not present in node_modules. Run 'yarn install' first." >&2
    exit 2
fi

EXIT=0
for ui in cxb catalog; do
    chunks_dir="$ui/dist/client/assets"
    if ! [ -d "$chunks_dir" ]; then
        echo "skip $ui — $chunks_dir missing (run ./build-ui.sh first)" >&2
        continue
    fi

    echo "=== Auditing $ui bundle ==="
    if ! node "$REPO_ROOT/admin_scripts/audit-bundles.mjs" "$chunks_dir"; then
        EXIT=1
    fi
done

exit $EXIT
