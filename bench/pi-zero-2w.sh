#!/bin/sh
# Measure dmart on a Raspberry Pi Zero 2 W (or any small board), ON the board.
#
#   ./pi-zero-2w.sh <admin-password> [api-url]
#
# Runs entirely on the device: no network hop in any figure. Written for
# busybox — the Alpine image ships wget and jq and nothing else useful, so
# there is no curl, no python3, no ab, and no `date +%N`. That last one is why
# every latency here is a BATCH divided by its count rather than a per-request
# measurement: busybox `date` has one-second resolution, and the shell `time`
# builtin has 10 ms, which is too coarse for a 20 ms read.
#
# Each batch therefore carries one `wget` fork+exec per request. That is not
# free on a 1 GHz A53 — the /health/ready baseline below exists to size it, and
# it is ~5-6 ms. Subtract it before comparing these numbers to anything.
#
# The fixture is the one bench/sqlite-vs-postgresql.py builds, so the figures
# are comparable to REPORT-sqlite-vs-postgresql.md, with one difference: no
# users/ directory. The admin already exists on a provisioned board, and
# importing a meta.user.json over it would upsert a row that carries no
# password field.
set -e
PW="$1"; API="${2:-http://127.0.0.1:5099}"
[ -n "$PW" ] || { echo "usage: $0 <admin-password> [api-url]" >&2; exit 2; }
SRV=$(pgrep -f "/usr/bin/dmart serve" | head -1)

rss()  { awk '/^VmRSS:/{print $2}' /proc/$SRV/status 2>/dev/null; }
hwm()  { awk '/^VmHWM:/{print $2}' /proc/$SRV/status 2>/dev/null; }
avail(){ free -m | awk '/^Mem:/{print $7}'; }

echo "== host =="
tr -d '\0' < /proc/device-tree/model 2>/dev/null; echo
echo "cores=$(nproc)  mem=$(free -m | awk '/^Mem:/{print $2}')MB  freq=$(awk '{print $1/1000}' /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq 2>/dev/null)MHz"
/usr/bin/dmart version 2>/dev/null | head -3

echo
echo "== footprint, idle (PSS, not summed RSS -- RSS double-counts shared pages) =="
d=0; p=0
# NB: `if`, not `[ ... ] && continue`. Under `set -e` that idiom exits the
# script the moment the test is FALSE, which here is the common case.
for q in $(ls -1 /proc 2>/dev/null | grep -E '^[0-9]+$' || true); do
  if [ -r /proc/$q/smaps_rollup ]; then
    n=$(awk '/^Name:/{print $2}' /proc/$q/status 2>/dev/null || true)
    s=$(awk '/^Pss:/{print $2}' /proc/$q/smaps_rollup 2>/dev/null || true)
    if [ -n "$s" ]; then
      case "$n" in
        dmart)    d=$((d+s)) ;;
        postgres) p=$((p+s)) ;;
      esac
    fi
  fi
done
echo "  dmart=$((d/1024))MB  postgres=$((p/1024))MB  combined=$(((d+p)/1024))MB"

echo
echo "== baseline: /health/ready x30 (DB round-trip, no hashing) =="
echo "   this is also the wget fork+exec overhead carried by every batch below"
time sh -c "i=0; while [ \$i -lt 30 ]; do wget -qO /dev/null $API/health/ready 2>/dev/null; i=\$((i+1)); done"

echo
echo "== login: Argon2id verify (kept under the 10/min auth limit) =="
BODY='{"shortname":"dmart","password":"'"$PW"'"}'
echo "  idle RSS before: $(rss) KB"
i=0
while [ $i -lt 6 ]; do
  t0=$(cut -d' ' -f1 /proc/uptime)
  wget -qO /tmp/.bench.login --header='Content-Type: application/json' --post-data="$BODY" "$API/user/login" 2>/dev/null
  t1=$(cut -d' ' -f1 /proc/uptime)
  s=$(jq -r '.status' /tmp/.bench.login 2>/dev/null)
  printf "  login %d: %s ms  status=%s\n" $((i+1)) "$(awk "BEGIN{printf \"%.0f\",($t1-$t0)*1000}")" "${s:-ERR}"
  if [ "$s" != "success" ]; then echo "  !! not a real measurement unless status=success"; fi
  i=$((i+1)); sleep 1
done
echo "  peak HWM after: $(hwm) KB  (one Argon2 buffer is PasswordHashMemoryKb)"

TOK=$(jq -r '.records[0].attributes.access_token' /tmp/.bench.login 2>/dev/null)
[ -n "$TOK" ] && [ "$TOK" != "null" ] || { echo "no token; stopping before the query workloads"; exit 1; }

echo
echo "== query workloads (require the pibench fixture: 5000 entries, 10 subpaths) =="
Q() { wget -qO "$2" --header="Content-Type: application/json" \
        --header="Authorization: Bearer $TOK" --post-data="$1" "$API/managed/query" 2>/dev/null; }

# Correctness gate. A latency figure for a query that matched nothing is noise,
# so prove the fixture is really there and the filter really selects before
# timing anything.
Q '{"type":"search","space_name":"pibench","subpath":"/","search":"@payload.body.bucket:b7","limit":10,"retrieve_total":true}' /tmp/.bench.q
n=$(jq -r '.attributes.total // 0' /tmp/.bench.q)
echo "  filtered search selects $n rows (expect 100 of 5000)"
[ "$n" = "100" ] || { echo "  !! fixture missing or wrong; timings below would be meaningless"; exit 1; }

echo "  -- warm read x30 (one entry by shortname) --"
time sh -c "i=0; while [ \$i -lt 30 ]; do wget -qO /dev/null --header='Content-Type: application/json' --header='Authorization: Bearer $TOK' --post-data='{\"type\":\"search\",\"space_name\":\"pibench\",\"subpath\":\"/s0\",\"search\":\"@shortname:e10\",\"limit\":1,\"retrieve_total\":false}' $API/managed/query 2>/dev/null; i=\$((i+1)); done"

echo "  -- filtered search x30 (100 of 5000 rows) --"
time sh -c "i=0; while [ \$i -lt 30 ]; do wget -qO /dev/null --header='Content-Type: application/json' --header='Authorization: Bearer $TOK' --post-data='{\"type\":\"search\",\"space_name\":\"pibench\",\"subpath\":\"/\",\"search\":\"@payload.body.bucket:b7\",\"limit\":20,\"retrieve_total\":true}' $API/managed/query 2>/dev/null; i=\$((i+1)); done"

echo
echo "== final =="
echo "  server RSS=$(rss) KB  HWM=$(hwm) KB  board available=$(avail) MB"
