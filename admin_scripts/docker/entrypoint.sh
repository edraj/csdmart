#!/bin/sh
set -e

PGDATA="/var/lib/postgresql/18/data"
MARKER="/root/.dmart/.db_initialized"

# --- First run: initialize PostgreSQL ---
if [ ! -f "$MARKER" ]; then
  echo "=== First run: initializing PostgreSQL ==="

  PGPASS=$(tr -dc A-Za-z0-9 </dev/urandom | head -c 16)
  JWT_SECRET=$(tr -dc A-Za-z0-9 </dev/urandom | head -c 48)

  mkdir -p /run/postgresql
  chown -R postgres:postgres /run/postgresql
  echo "$PGPASS" > /tmp/pgpass
  su - postgres -c "initdb --auth=scram-sha-256 -U dmart --pwfile=/tmp/pgpass $PGDATA"

  su - postgres -c "pg_ctl start -D $PGDATA -l /dev/null"
  PGPASSWORD="$PGPASS" createdb -h 127.0.0.1 -U dmart dmart
  su - postgres -c "pg_ctl stop -D $PGDATA -m fast"

  echo "DATABASE_NAME='dmart'" >> /root/.dmart/config.env
  echo "DATABASE_USERNAME='dmart'" >> /root/.dmart/config.env
  echo "DATABASE_PASSWORD='$PGPASS'" >> /root/.dmart/config.env
  echo "JWT_SECRET='$JWT_SECRET'" >> /root/.dmart/config.env

  rm -f /tmp/pgpass
  touch "$MARKER"
  echo "=== Database initialized ==="
fi

# --- Start PostgreSQL in background ---
mkdir -p /run/postgresql
chown postgres:postgres /run/postgresql
touch /var/log/postgresql.log && chown postgres:postgres /var/log/postgresql.log
su - postgres -c "pg_ctl start -D $PGDATA -l /var/log/postgresql.log"

# --- Graceful shutdown: stop dmart, then PG ---
shutdown() {
  [ -n "$DMART_PID" ] && kill -TERM "$DMART_PID" 2>/dev/null && wait "$DMART_PID" 2>/dev/null
  su - postgres -c "pg_ctl stop -D $PGDATA -m fast" 2>/dev/null
  exit 0
}
trap shutdown TERM INT

# --- Start dmart in background, wait for it ---
export BACKEND_ENV="/root/.dmart/config.env"
/usr/bin/dmart serve --cxb-config /root/.dmart/config.json &
DMART_PID=$!
wait "$DMART_PID"
