#!/bin/bash
set -e

# Build a portable dmart binary using Docker/Podman.
# The binary is compiled against musl (Alpine) and runs on ANY Linux
# distribution — no glibc version dependency.
#
# Usage: ./build-portable.sh
# Output: ./dist/dmart (+ cxb/ + plugins/ + config files)

BUILDER=${CONTAINER_RUNTIME:-$(command -v podman 2>/dev/null || echo docker)}
DIST_DIR="dist"

echo "Building portable binary with $BUILDER..."

# Build the container image (includes CXB frontend)
$BUILDER build -t dmart-csharp-builder .

# Extract the binary + assets from the final image
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

# Copy /app contents out of the image
CONTAINER=$($BUILDER create dmart-csharp-builder)
$BUILDER cp "$CONTAINER:/app/." "$DIST_DIR/"
$BUILDER rm "$CONTAINER" > /dev/null

echo ""
echo "Portable build output:"
ls -lh "$DIST_DIR/dmart"
du -sh "$DIST_DIR/"
echo ""
echo "Deploy to any Linux server:"
echo "  scp -r $DIST_DIR/ server:/opt/dmart/"
echo "  ssh server 'cd /opt/dmart && ./dmart serve'"
