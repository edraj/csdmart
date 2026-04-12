#!/bin/bash
set -e

# Build a portable dmart distribution using Docker/Podman.
# The binary is compiled against musl (Alpine) and bundled with the
# required shared libraries so it runs on ANY Linux — including
# glibc-based distros like RHEL 9, Ubuntu, Debian.
#
# Usage: ./build-portable.sh
# Output: ./dist/ (dmart binary + libs + cxb + plugins + run.sh wrapper)

BUILDER=${CONTAINER_RUNTIME:-$(command -v podman 2>/dev/null || echo docker)}
DIST_DIR="dist"

echo "Building portable distribution with $BUILDER..."

# Build the container image (includes CXB frontend)
$BUILDER build -t dmart-csharp-builder .

# Extract /app + required musl libraries from the final image
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR/lib"

CONTAINER=$($BUILDER create dmart-csharp-builder)
# Copy the app
$BUILDER cp "$CONTAINER:/app/." "$DIST_DIR/"
# Copy musl dynamic linker + required libs so the binary runs on glibc hosts
$BUILDER cp "$CONTAINER:/lib/ld-musl-x86_64.so.1" "$DIST_DIR/lib/"
for lib in libssl.so.3 libcrypto.so.3 libgssapi_krb5.so.2 libkrb5.so.3 \
           libk5crypto.so.3 libkrb5support.so.0 libcom_err.so.2 libz.so.1 \
           libstdc++.so.6 libgcc_s.so.1; do
    $BUILDER cp "$CONTAINER:/usr/lib/$lib" "$DIST_DIR/lib/" 2>/dev/null || \
    $BUILDER cp "$CONTAINER:/lib/$lib" "$DIST_DIR/lib/" 2>/dev/null || true
done
$BUILDER rm "$CONTAINER" > /dev/null

# Symlink plugins/cxb into lib/ so the musl-loaded binary finds them
# (AppContext.BaseDirectory resolves to lib/ when invoked via ld-musl)
[ -d "$DIST_DIR/plugins" ] && ln -sf ../plugins "$DIST_DIR/lib/plugins"
[ -d "$DIST_DIR/cxb" ] && ln -sf ../cxb "$DIST_DIR/lib/cxb"

# Create a wrapper script that sets the library path
cat > "$DIST_DIR/run.sh" << 'WRAPPER'
#!/bin/sh
DIR=$(cd "$(dirname "$0")" && pwd)
export LD_LIBRARY_PATH="$DIR/lib:$LD_LIBRARY_PATH"
cd "$DIR"
exec "$DIR/lib/ld-musl-x86_64.so.1" "$DIR/dmart" "$@"
WRAPPER
chmod +x "$DIST_DIR/run.sh"

echo ""
echo "Portable build output:"
ls -lh "$DIST_DIR/dmart"
du -sh "$DIST_DIR/"
echo ""
echo "Deploy to any Linux server:"
echo "  scp -r $DIST_DIR/ server:/opt/dmart/"
echo "  ssh server 'cd /opt/dmart && ./run.sh serve'"
