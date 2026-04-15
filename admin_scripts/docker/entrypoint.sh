#!/bin/sh
# First-run: initialize PostgreSQL + dmart config.env with generated credentials.
# Subsequent runs: just start OpenRC.

PGDATA="/var/lib/postgresql/18/data"
MARKER="/root/.dmart/.db_initialized"

if [ ! -f "$MARKER" ]; then
  echo "=== First run: initializing PostgreSQL ==="

  # Generate random passwords
  PGPASS=$(tr -dc A-Za-z0-9 </dev/urandom | head -c 16)
  JWT_SECRET=$(tr -dc A-Za-z0-9 </dev/urandom | head -c 48)

  # Initialize PostgreSQL cluster
  mkdir -p /run/postgresql
  chown -R postgres:postgres /run/postgresql
  echo "$PGPASS" > /tmp/pgpass
  su - postgres -c "initdb --auth=scram-sha-256 -U dmart --pwfile=/tmp/pgpass $PGDATA"

  # Start PG temporarily to create the database
  su - postgres -c "pg_ctl start -t 5000 -D $PGDATA -o '-p 15432'"
  PGPASSWORD="$PGPASS" createdb -h 127.0.0.1 -p 15432 -U dmart dmart
  su - postgres -c "pg_ctl stop -D $PGDATA"

  # Write DB credentials into dmart config
  echo "DATABASE_NAME='dmart'" >> /root/.dmart/config.env
  echo "DATABASE_USERNAME='dmart'" >> /root/.dmart/config.env
  echo "DATABASE_PASSWORD='$PGPASS'" >> /root/.dmart/config.env
  echo "JWT_SECRET='$JWT_SECRET'" >> /root/.dmart/config.env

  # Cleanup
  rm -f /tmp/pgpass
  touch "$MARKER"
  echo "=== Database initialized ==="
fi

# Hand off to OpenRC (starts postgresql + dmart services)
exec /sbin/init
