#!/bin/sh
set -e

CONFIG_DIR="/root/.dmart"
CONFIG_ENV="$CONFIG_DIR/config.env"
MARKER="$CONFIG_DIR/.db_initialized"
PGDATA_LEGACY="/var/lib/postgresql/18/data"

# --- Refuse to start on top of a PostgreSQL-era volume ---------------------
#
# Images up to this one bundled PostgreSQL 18 and ran initdb here. This one
# does not ship PostgreSQL at all, so a container reusing that volume would
# come up on an empty SQLite file and serve nothing — with a green health
# check, because /health/ready would be probing the new empty database quite
# successfully. Stop instead, and say what to do.
#
# The data itself is not lost: the flat files under SPACES_FOLDER are the
# source of truth and the SQL store is a rebuildable index over them, which is
# exactly why `dmart import` can reconstruct it.
if [ -d "$PGDATA_LEGACY" ]; then
  cat >&2 << 'EOF'
=== refusing to start: this volume was created by a PostgreSQL-era image ===

This image no longer bundles PostgreSQL. Found an existing cluster at
/var/lib/postgresql/18/data, which nothing in this image can start or read.

Your data is not lost. The flat files under the spaces folder are the source
of truth and the SQL store is a rebuildable index over them. Pick one:

  1. Rebuild the index on SQLite (recommended for a single-node container):
       - start this container with the PostgreSQL data dir NOT mounted
       - dmart import /root/.dmart/spaces

  2. Keep using PostgreSQL:
       - run an external PostgreSQL and point this container at it with
         DATABASE_DRIVER=postgresql plus DATABASE_HOST / DATABASE_NAME /
         DATABASE_USERNAME / DATABASE_PASSWORD
       - or pin the previous all-in-one image tag

  3. Start clean:
       - remove the old volume and let this container initialize fresh

EOF
  exit 1
fi

# --- First run: initialize dmart config -----------------------------------
if [ ! -f "$MARKER" ]; then
  echo "=== First run: initializing ==="

  dmart init
  cat > "$CONFIG_DIR/config.json" << 'CONF'
{
  "title": "DMART Unified Data Platform",
  "footer": "dmart.cc unified data platform",
  "short_name": "dmart",
  "display_name": "dmart",
  "description": "dmart unified data platform",
  "default_language": "en",
  "languages": { "ar": "العربية", "en": "English" },
  "backend": "http://localhost:8000",
  "websocket": "ws://localhost:8000/ws"
}
CONF

  JWT_SECRET=$(tr -dc A-Za-z0-9 </dev/urandom | head -c 48)

  # SQLITE_PATH is absolute and inside the config dir, so it lands on the same
  # volume as the spaces folder and the two stay together in a backup. It is
  # written explicitly rather than left to the "dmart.db relative to CWD"
  # default, which would put it wherever the container happened to start.
  #
  # DATABASE_DRIVER is likewise explicit: this file survives image upgrades,
  # and pinning it means a later edit adding a PostgreSQL connection has to
  # flip the driver too rather than switching backends as a side effect.
  cat >> "$CONFIG_ENV" << EOF
LISTENING_PORT=8000
ALLOWED_CORS_ORIGINS="http://localhost:8000"
DATABASE_DRIVER='sqlite'
SQLITE_PATH='$CONFIG_DIR/dmart.db'
JWT_SECRET='$JWT_SECRET'
EOF

  touch "$MARKER"
  echo "=== Initialized ==="
fi

# --- Run dmart in the foreground ------------------------------------------
#
# exec, not background-and-wait: with nothing else to supervise, dmart becomes
# PID 1 and receives SIGTERM directly, so `podman stop` shuts it down through
# its own lifetime hooks instead of a trap racing a second process.
export BACKEND_ENV="$CONFIG_ENV"
exec /usr/bin/dmart serve --cxb-config "$CONFIG_DIR/config.json"
