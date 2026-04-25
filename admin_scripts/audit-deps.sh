#!/bin/bash
# Audit transitive dependencies bundled into cxb + catalog dist outputs.
#
# For every package present in a chunk, resolve where it came from:
#   - direct dep declared in <ui>/package.json
#   - transitive dep pulled in by a direct dep (and through which chain)
#   - orphan: bundled but no chain back to anything declared (= bug worth investigating)
#
# Also reports per-package gzipped chunk size, sorted descending, so the
# expensive transitive deps are visible first.
#
# Usage: ./admin_scripts/audit-deps.sh [cxb|catalog]   (defaults to both)
# Prerequisite: ./build-ui.sh has produced dist/client outputs.

set -eu

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

UIS=("${@:-cxb catalog}")
EXIT=0
for ui in $UIS; do
    if ! [ -d "$ui/dist/client" ]; then
        echo "skip $ui — $ui/dist/client missing (run ./build-ui.sh first)" >&2
        continue
    fi
    if ! [ -f "$ui/package.json" ]; then
        echo "skip $ui — $ui/package.json missing" >&2
        continue
    fi
    echo "=== Auditing $ui dependency closure ==="
    if ! node "$REPO_ROOT/admin_scripts/audit-deps.mjs" "$ui"; then
        EXIT=1
    fi
    echo ""
done

exit $EXIT
