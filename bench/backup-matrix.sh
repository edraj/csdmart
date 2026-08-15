#!/usr/bin/env bash
#
# One consolidated table: every export/import option, time and compressed size,
# all measured in a single run against one database.
#
# Separate from backup-formats.sh, which compares the three families and
# explains the trade. This one is the reference table — every variant, so a
# number can be looked up without stitching together figures from different
# runs of different builds.
#
# Sizes are the bytes you would STORE, so uncompressed variants are listed with
# their real size rather than being quietly piped through a compressor.
#
# Usage: bench/backup-matrix.sh <publish-dir> <config.env> [runs]
set -euo pipefail

PUB="$(cd "${1:?usage: backup-matrix.sh <publish-dir> <config.env> [runs]}" && pwd)"
CONF="$(cd "$(dirname "${2:?need a config.env}")" && pwd)/$(basename "$2")"
RUNS="${3:-2}"
BIN="$PUB/dmart"
W="$(mktemp -d)"
trap 'rm -rf "$W"' EXIT

get() { grep -E "^$1=" "$CONF" | tail -1 | cut -d= -f2- | tr -d '"'; }
PGHOST=$(get DATABASE_HOST); PGPORT=$(get DATABASE_PORT)
PGUSER=$(get DATABASE_USERNAME); PGDB=$(get DATABASE_NAME)
export PGPASSWORD; PGPASSWORD=$(get DATABASE_PASSWORD)
SPACE="${BENCH_SPACE:-personal}"
TABLES="users spaces roles permissions entries attachments histories"
J="$(nproc)"; [ "$J" -gt 8 ] && J=8

ms() { local b=999999 s e t; for _ in $(seq 1 "$RUNS"); do
    s=$(date +%s%N); eval "$1" >/dev/null 2>&1 || { echo "FAILED: $1" >&2; return 1; }
    e=$(date +%s%N); t=$(( (e-s)/1000000 )); [ "$t" -lt "$b" ] && b=$t; done; echo "$b"; }

# Same, but recreating the target database before every timed run — a restore
# into a populated database measures the wrong thing, or fails outright.
ms_restore() { local db="$1" prep="$2" cmd="$3" b=999999 s e t
  for _ in $(seq 1 "$RUNS"); do
    psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d postgres -q \
      -c "DROP DATABASE IF EXISTS $db WITH (FORCE);" -c "CREATE DATABASE $db;" >/dev/null 2>&1
    if [ "$prep" != "none" ]; then
      sed "s/^DATABASE_NAME=.*/DATABASE_NAME=\"$db\"/" "$CONF" > "$W/$db.env"; chmod 600 "$W/$db.env"
      BACKEND_ENV="$W/$db.env" "$BIN" migrate >/dev/null 2>&1 || true
      # Content rows carry owner_shortname, a foreign key into users; a target
      # with schema but no accounts fails every row.
      [ "$prep" = "users" ] && pg_dump -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" \
        -t users --data-only 2>/dev/null | psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$db" -q >/dev/null 2>&1
    fi
    s=$(date +%s%N); eval "$cmd" >/dev/null 2>&1 || { echo "FAILED: $cmd" >&2; return 1; }
    e=$(date +%s%N); t=$(( (e-s)/1000000 )); [ "$t" -lt "$b" ] && b=$t
  done; echo "$b"; }

sz() { local b; b=$(du -sb "$1" 2>/dev/null | cut -f1); numfmt --to=iec --format='%.1f' "$b"; }
row() { printf '| %-38s | %8s | %8s | %8s |\n' "$1" "$2" "$3" "$4"; }

printf '\n| %-38s | %8s | %8s | %8s |\n' "OPTION" "EXPORT" "IMPORT" "SIZE"
printf '|%s|%s|%s|%s|\n' "----------------------------------------" "----------" "----------" "----------"

# ---------------------------------------------------------------- dmart
mkdir -p "$W/z"
E=$(ms "BACKEND_ENV=$CONF $BIN export $SPACE --output $W/z/s.zip")
I=$(ms_restore bz users "BACKEND_ENV=$W/bz.env $BIN import $W/z/s.zip -r")
row "zip / JSON (1 space)" "$E" "$I" "$(sz "$W/z/s.zip")"

E=$(ms "rm -rf $W/pq; BACKEND_ENV=$CONF $BIN export $SPACE --parquet --output $W/pq")
I=$(ms_restore bp users "BACKEND_ENV=$W/bp.env $BIN import $W/pq --parquet -r --no-verify")
row "Parquet zstd-3 (1 space)" "$E" "$I" "$(sz "$W/pq")"

E=$(ms "rm -rf $W/all; BACKEND_ENV=$CONF $BIN export --all --parquet --no-verify --output $W/all")
I=$(ms_restore ba users "BACKEND_ENV=$W/ba.env $BIN import $W/all --parquet -r --no-verify")
row "Parquet zstd-3 (--all)" "$E" "$I" "$(sz "$W/all")"

# ---------------------------------------------------------------- pg_dump
E=$(ms "pg_dump -h $PGHOST -p $PGPORT -U $PGUSER -d $PGDB -Fc -f $W/d.pgc")
I=$(ms_restore bc none "pg_restore -h $PGHOST -p $PGPORT -U $PGUSER -d bc --no-owner $W/d.pgc")
row "pg_dump -Fc (zlib)" "$E" "$I" "$(sz "$W/d.pgc")"

I=$(ms_restore bcj none "pg_restore -h $PGHOST -p $PGPORT -U $PGUSER -d bcj --no-owner -j $J $W/d.pgc")
row "pg_dump -Fc  + pg_restore -j$J" "-" "$I" "$(sz "$W/d.pgc")"

E=$(ms "pg_dump -h $PGHOST -p $PGPORT -U $PGUSER -d $PGDB -Fc -Z zstd:3 -f $W/dz.pgc")
I=$(ms_restore bcz none "pg_restore -h $PGHOST -p $PGPORT -U $PGUSER -d bcz --no-owner -j $J $W/dz.pgc")
row "pg_dump -Fc -Z zstd:3 + -j$J" "$E" "$I" "$(sz "$W/dz.pgc")"

E=$(ms "rm -rf $W/dd; pg_dump -h $PGHOST -p $PGPORT -U $PGUSER -d $PGDB -Fd -j $J -Z zstd:3 -f $W/dd")
I=$(ms_restore bd none "pg_restore -h $PGHOST -p $PGPORT -U $PGUSER -d bd --no-owner -j $J $W/dd")
row "pg_dump -Fd -j$J -Z zstd:3 + -j$J" "$E" "$I" "$(sz "$W/dd")"

E=$(ms "pg_dump -h $PGHOST -p $PGPORT -U $PGUSER -d $PGDB -Fp -f $W/d.sql")
I=$(ms_restore bs none "psql -h $PGHOST -p $PGPORT -U $PGUSER -d bs -q -f $W/d.sql")
row "pg_dump -Fp (plain SQL)" "$E" "$I" "$(sz "$W/d.sql")"

E=$(ms "pg_dump -h $PGHOST -p $PGPORT -U $PGUSER -d $PGDB -Fp | zstd -3 -q -T0 > $W/d.sql.zst")
I=$(ms_restore bsz none "zstd -dc $W/d.sql.zst | psql -h $PGHOST -p $PGPORT -U $PGUSER -d bsz -q")
row "pg_dump -Fp | zstd -3" "$E" "$I" "$(sz "$W/d.sql.zst")"

E=$(ms "pg_dump -h $PGHOST -p $PGPORT -U $PGUSER -d $PGDB -Fp | gzip -6 > $W/d.sql.gz")
I=$(ms_restore bsg none "gzip -dc $W/d.sql.gz | psql -h $PGHOST -p $PGPORT -U $PGUSER -d bsg -q")
row "pg_dump -Fp | gzip -6" "$E" "$I" "$(sz "$W/d.sql.gz")"

# ---------------------------------------------------------------- COPY
cb_out() { rm -rf "$W/cb"; mkdir -p "$W/cb"; for t in $TABLES; do
  psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" -q \
    -c "COPY $t TO STDOUT WITH (FORMAT BINARY)" > "$W/cb/$t.bin"; done; }
cb_in() { for t in $TABLES; do psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d bcb -q \
    -c "COPY $t FROM STDIN WITH (FORMAT BINARY)" < "$W/cb/$t.bin"; done; }
E=$(ms cb_out); I=$(ms_restore bcb schema cb_in)
row "COPY BINARY (raw)" "$E" "$I" "$(sz "$W/cb")"

cbz_out() { rm -rf "$W/cbz"; mkdir -p "$W/cbz"; for t in $TABLES; do
  psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" -q \
    -c "COPY $t TO STDOUT WITH (FORMAT BINARY)" | zstd -3 -q -T0 > "$W/cbz/$t.zst"; done; }
cbz_in() { for t in $TABLES; do zstd -dc "$W/cbz/$t.zst" \
    | psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d bcz2 -q \
      -c "COPY $t FROM STDIN WITH (FORMAT BINARY)"; done; }
E=$(ms cbz_out); I=$(ms_restore bcz2 schema cbz_in)
row "COPY BINARY | zstd -3" "$E" "$I" "$(sz "$W/cbz")"

cbg_out() { rm -rf "$W/cbg"; mkdir -p "$W/cbg"; for t in $TABLES; do
  psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" -q \
    -c "COPY $t TO STDOUT WITH (FORMAT BINARY)" | gzip -6 > "$W/cbg/$t.gz"; done; }
cbg_in() { for t in $TABLES; do gzip -dc "$W/cbg/$t.gz" \
    | psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d bcg -q \
      -c "COPY $t FROM STDIN WITH (FORMAT BINARY)"; done; }
E=$(ms cbg_out); I=$(ms_restore bcg schema cbg_in)
row "COPY BINARY | gzip -6" "$E" "$I" "$(sz "$W/cbg")"

csv_out() { rm -rf "$W/csv"; mkdir -p "$W/csv"; for t in $TABLES; do
  psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDB" -q \
    -c "COPY $t TO STDOUT WITH (FORMAT CSV, HEADER)" | zstd -3 -q -T0 > "$W/csv/$t.zst"; done; }
csv_in() { for t in $TABLES; do zstd -dc "$W/csv/$t.zst" \
    | psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d bcsv -q \
      -c "COPY $t FROM STDIN WITH (FORMAT CSV, HEADER)"; done; }
E=$(ms csv_out); I=$(ms_restore bcsv schema csv_in)
row "COPY CSV | zstd -3" "$E" "$I" "$(sz "$W/csv")"

for db in bz bp ba bc bcj bcz bd bs bsz bsg bcb bcz2 bcg bcsv; do
  psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d postgres -q \
    -c "DROP DATABASE IF EXISTS $db WITH (FORCE);" >/dev/null 2>&1
done
printf '\nbest of %s runs; restores into a freshly created database each time\n' "$RUNS"
