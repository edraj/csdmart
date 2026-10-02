#!/usr/bin/env bash
# Drive the Shanidar storyline through its workflows, as the personas.
#
# install.sh lands every case `open`. This walks them through the state machine
# over the real API as the real users, so the resulting history is genuine —
# authored by the engine, gated by the roles, refused where it should be.
#
# Why not ship the history instead? Because re-importing an archive APPENDS its
# history.jsonl rather than upserting it (PLAN.md verification 5), so a pack
# installed twice would carry its history twice. The cost of generating it here
# is that the timestamps are install-time rather than the story's dates.
#
# Idempotent in the sense that matters: re-running it on an already-walked
# instance reports the transitions as already-applied rather than failing.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
URL="${DMART_URL:-}"
ADMIN="${DMART_ADMIN:-dmart}"

usage() {
    cat <<EOF
Usage: $(basename "$0") [--url http://127.0.0.1:8282] [--admin dmart]

Environment:
  DMART_ADMIN_PASSWORD        to read the summary at the end
  DMART_PACKS_DEMO_PASSWORD   to act as the personas (same value install.sh used)
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --url)     URL="$2"; shift 2 ;;
        --url=*)   URL="${1#*=}"; shift ;;
        --admin)   ADMIN="$2"; shift 2 ;;
        --admin=*) ADMIN="${1#*=}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

[ -n "$URL" ] || { echo "--url or \$DMART_URL is required" >&2; exit 2; }
[ -n "${DMART_PACKS_DEMO_PASSWORD:-}" ] || {
    echo "DMART_PACKS_DEMO_PASSWORD is required — the same value install.sh used" >&2
    exit 2
}

login() {
    local who="$1" pw="$2" out
    out="$(curl -sS -m 15 -X POST "$URL/user/login" \
        -H 'Content-Type: application/json' \
        -d "$(python3 -c 'import json,sys; print(json.dumps({"shortname":sys.argv[1],"password":sys.argv[2]}))' "$who" "$pw")")"
    python3 -c '
import json, sys
d = json.load(sys.stdin)
if d.get("status") != "success":
    sys.exit("login failed for " + sys.argv[1] + ": "
             + str((d.get("error") or {}).get("message")))
print(d["records"][0]["attributes"]["access_token"])' "$who" <<<"$out"
}

# PUT /managed/progress-ticket/{space}/{subpath}/{shortname}/{action}
progress() {
    local token="$1" space="$2" subpath="$3" sn="$4" action="$5" reason="${6:-}"
    local body='{}'
    [ -n "$reason" ] && body="$(python3 -c 'import json,sys; print(json.dumps({"resolution_reason":sys.argv[1]}))' "$reason")"
    curl -sS -m 20 -X PUT "$URL/managed/progress-ticket/$space/$subpath/$sn/$action" \
        -H 'Content-Type: application/json' \
        -H "Authorization: Bearer $token" \
        -d "$body"
}

# Prints `ok`, `already`, or `refused: <reason>`. A transition that is not
# available because the ticket already moved on is not a failure when
# re-running, so it is reported separately from a real refusal.
step() {
    local label="$1" token="$2" space="$3" subpath="$4" sn="$5" action="$6" reason="${7:-}"
    printf '  %-46s ' "$label"
    progress "$token" "$space" "$subpath" "$sn" "$action" "$reason" | python3 -c '
import json, sys
raw = sys.stdin.read()
try:
    d = json.loads(raw)
except Exception:
    print("unreadable: " + raw[:80]); raise SystemExit
if d.get("status") == "success":
    print("ok"); raise SystemExit
msg = str((d.get("error") or {}).get("message") or "")
for block in (d.get("error") or {}).get("info") or []:
    if isinstance(block, dict):
        for f in block.get("failed") or []:
            msg = str(f.get("error") or msg)
if "not in workflow" in msg or "no transitions" in msg or "transition not allowed" in msg:
    print("already (" + msg[:48] + ")")
else:
    print("refused: " + msg[:60])'
}

# Same, but a refusal is the POINT — used for the negative demonstrations.
step_expect_refusal() {
    local label="$1"; shift
    printf '  %-46s ' "$label"
    progress "$@" | python3 -c '
import json, sys
d = json.load(sys.stdin)
if d.get("status") == "success":
    print("UNEXPECTEDLY ALLOWED — the role gate did not hold"); raise SystemExit(1)
msg = str((d.get("error") or {}).get("message") or "")
print("refused as expected: " + msg[:60])'
}

PW="$DMART_PACKS_DEMO_PASSWORD"
echo "== logging in as the personas"
AGENT="$(login agent_baghdad "$PW")";   echo "  agent_baghdad    ok"
SUP="$(login sup_south "$PW")";         echo "  sup_south        ok"
SEC="$(login security_officer "$PW")";  echo "  security_officer ok"

cat <<'EOF'

== 1. erb_0142: a power fault becomes a resolved case

   The customer on case_000101 has no service at a fixed address. The site's
   own KPIs show 41 outage minutes and its generator is marked faulty, so this
   is not a handset problem. The agent takes it, cannot close it alone once
   escalated, and the supervisor resolves it.
EOF
step "agent takes case_000101"              "$AGENT" servicedesk cases case_000101 take
step "agent escalates it"                   "$AGENT" servicedesk cases case_000101 escalate
step_expect_refusal "agent tries to close an escalated case" \
     "$AGENT" servicedesk cases case_000101 resolve site_repaired
step "supervisor resolves it (site_repaired)" "$SUP" servicedesk cases case_000101 resolve site_repaired

cat <<'EOF'

== 2. bsr_0031: congestion, not a fault

   case_000103 is a slow router in the evening peak. The site's figures match,
   so the right close is `customer_educated` — not a repair that never happened.
EOF
step "agent takes case_000103"                "$AGENT" servicedesk cases case_000103 take
step "agent resolves it (customer_educated)"  "$AGENT" servicedesk cases case_000103 resolve customer_educated

cat <<'EOF'

== 3. a close with no reason is refused

   The workflow marks both closing transitions `resolution_required`, so the
   engine rejects a close that carries no resolution_reason. This is the one
   part of the resolution catalogue dmart DOES enforce — presence, not value.
EOF
step "agent takes case_000102"                "$AGENT" servicedesk cases case_000102 take
step_expect_refusal "agent resolves it with no reason" \
     "$AGENT" servicedesk cases case_000102 resolve
step "agent resolves it (billing_corrected)"  "$AGENT" servicedesk cases case_000102 resolve billing_corrected

cat <<'EOF'

== 4. site access, approved and rejected

   Both requests are gated on approvals_security, which only the security
   officer holds. The technician who raised them holds assets_technician and no
   permission on the approvals space at all — so his attempt is refused at the
   READ, with "ticket not found" rather than "not allowed". That is deliberate
   on dmart's part: a permission walk that distinguished the two would leak
   which tickets exist.
EOF
step_expect_refusal "technician cannot even see his own request" \
     "$(login tech_north_erbil "$PW")" approvals requests req_erb_0142_starter approve
step "officer approves the starter replacement" "$SEC" approvals requests req_erb_0142_starter approve
step "officer completes it"                     "$SEC" approvals requests req_erb_0142_starter complete
step "officer rejects the COW recovery (window_clashes)" \
     "$SEC" approvals requests req_krb_0007_recovery reject window_clashes

cat <<'EOF'

== 5. one case stays open

   case_000104 (the Arbaeen capacity reports) is deliberately left `open`. A
   demo where every ticket is closed has an empty worklist, which is the one
   view an agent actually lives in.
EOF

# ── summary ───────────────────────────────────────────────────────────────────
if [ -n "${DMART_ADMIN_PASSWORD:-}" ]; then
    ADMIN_TOKEN="$(login "$ADMIN" "$DMART_ADMIN_PASSWORD")"
    echo
    echo "== where everything ended up"
    for space in servicedesk approvals; do
        sub=$([ "$space" = servicedesk ] && echo cases || echo requests)
        curl -sS -m 20 -X POST "$URL/managed/query" \
            -H 'Content-Type: application/json' -H "Authorization: Bearer $ADMIN_TOKEN" \
            -d "{\"type\":\"search\",\"space_name\":\"$space\",\"subpath\":\"$sub\",\"search\":\"\",\"limit\":50}" \
          | python3 -c '
import json, sys
d = json.load(sys.stdin)
for r in sorted(d.get("records", []), key=lambda x: x["shortname"]):
    a = r["attributes"]
    print("  %-24s %-12s open=%-5s %s" % (
        r["shortname"], a.get("state"), a.get("is_open"),
        a.get("resolution_reason") or ""))'
    done
    echo
    # A history query scopes by `filter_shortnames`, NOT by a `search`
    # selector — HistoryRepository.QueryHistoryAsync builds its WHERE from
    # space/subpath/filter_shortnames/from/to and never looks at Search, so
    # `@shortname:x` is accepted and silently ignored.
    echo "== history the walkthrough wrote"
    curl -sS -m 20 -X POST "$URL/managed/query" \
        -H 'Content-Type: application/json' -H "Authorization: Bearer $ADMIN_TOKEN" \
        -d '{"type":"history","space_name":"servicedesk","subpath":"cases","filter_shortnames":["case_000101"],"limit":20}' \
      | python3 -c '
import json, sys
d = json.load(sys.stdin)
rows = d.get("records", [])
if d.get("status") != "success":
    print("  (history query unavailable: %s)" % str((d.get("error") or {}).get("message"))[:60])
else:
    print("  case_000101 has %d history row(s):" % len(rows))
    for r in rows:
        a = r["attributes"]
        print("    %-20s %s" % (a.get("request_headers", {}).get("actor")
              or a.get("owner_shortname", "?"), json.dumps(a.get("diff", {}))[:90]))'
fi

cat <<'EOF'

== done

Nothing above was scripted into the data: every state, every refusal and every
history row came from dmart's own workflow engine and permission walk.
EOF
