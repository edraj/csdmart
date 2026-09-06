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
  # MOCK_SMPP_API: this image ships no SMS gateway, and SmsSender's unconfigured
  # path logs "SMS gateway not configured - dropping message" and returns false.
  # Left at its false default, /user/otp-request therefore MINTS a code and
  # silently fails to deliver it, so OTP login cannot be completed and nothing
  # says why. Mocking makes that honest: the code is the fixed MOCK_OTP_CODE
  # below and the log says a mock is active. Set it to false and configure
  # SEND_SMS_OTP_API + SMPP_AUTH_KEY for real delivery.
  #
  # MOCK_SMTP_API is deliberately NOT forced here - email OTP has the same
  # gap, but changing it is a separate call than the one this line answers.
  cat >> "$CONFIG_ENV" << EOF
LISTENING_PORT=8000
ALLOWED_CORS_ORIGINS="http://localhost:8000"
DATABASE_DRIVER='sqlite'
SQLITE_PATH='$CONFIG_DIR/dmart.db'
JWT_SECRET='$JWT_SECRET'
MOCK_SMPP_API=true
MOCK_OTP_CODE='123456'
EOF

  # config.env holds the generated JWT_SECRET; dmart itself warns when this is
  # world-readable, and in a container the default umask leaves it 0644.
  chmod 600 "$CONFIG_ENV"

  touch "$MARKER"
  echo "=== Initialized ==="
  echo "SMS OTP is MOCKED (MOCK_SMPP_API=true) — codes are always 123456."
  echo "Configure SEND_SMS_OTP_API + SMPP_AUTH_KEY and unset it for real delivery."
  echo "Set an admin password before exposing this container:"
  echo "  podman exec -it <container> dmart passwd dmart"
fi

# --- Run dmart in the foreground ------------------------------------------
#
# exec, not background-and-wait: with nothing else to supervise, dmart becomes
# PID 1 and receives SIGTERM directly, so `podman stop` shuts it down through
# its own lifetime hooks instead of a trap racing a second process.
export BACKEND_ENV="$CONFIG_ENV"
exec /usr/bin/dmart serve --cxb-config "$CONFIG_DIR/config.json"
