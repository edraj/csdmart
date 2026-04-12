#!/bin/bash
# Build the CXB Svelte frontend and copy the dist into the C# project
# for embedding into the binary.
#
# Usage: ./build-cxb.sh
# Prerequisites: yarn (or npm) available, cxb project at ../cxb

set -eu

CXB_DIR="${CXB_DIR:-../cxb}"
DEST="cxb-dist"

if [ ! -d "$CXB_DIR" ]; then
    echo "cxb project not found at $CXB_DIR — set CXB_DIR" >&2
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

echo "Copying dist to $DEST/..."
rm -rf "$DEST"
cp -r "$CXB_DIR/dist/client" "$DEST"

echo "Done. $(find "$DEST" -type f | wc -l) files in $DEST/ ($(du -sh "$DEST" | cut -f1))"
echo "Run 'dotnet publish' to embed them into the binary."
