#!/bin/bash
# Build the CXB Svelte frontend in-tree.
# The cxb/ directory lives inside the C# project and its dist/client/
# output is embedded into the binary via EmbeddedResource in the .csproj.
#
# Usage: ./build-cxb.sh
# Prerequisites: yarn (or npm) available

set -eu

CXB_DIR="cxb"

if [ ! -f "$CXB_DIR/package.json" ]; then
    echo "cxb/package.json not found — is the CXB source present?" >&2
    exit 1
fi

echo "Building CXB frontend..."
cd "$CXB_DIR"
if command -v yarn &> /dev/null; then
    yarn install
    yarn build
else
    npm install
    npm run build
fi
cd - > /dev/null

echo "Done. $(find "$CXB_DIR/dist/client" -type f | wc -l) files ($(du -sh "$CXB_DIR/dist/client" | cut -f1))"
echo "Run './build.sh' to embed them into the binary."
