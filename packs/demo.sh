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

== 7. what is left open, on purpose

   case_000104 (the Arbaeen capacity reports) and the two customer-raised
   cases stay `open`. A demo where every ticket is closed has an empty
   worklist, which is the one view an agent actually lives in — and the
   customer cases are there to be found by their owner, not resolved by this
   script.

   Note the public form adds one intake case per run, which is what a public
   form does. Everything else here is idempotent.
EOF


# ── public surface ────────────────────────────────────────────────────────────
# Only meaningful after `install.sh --public`. Without it the anonymous user
# holds no pack role and every call below is correctly refused, so the section
# announces that rather than looking broken.
pub_get() { curl -sS -m 15 "$URL$1"; }
pub_query() {
    curl -sS -m 15 -X POST "$URL/public/query" -H 'Content-Type: application/json' \
      -d "$(python3 -c '
import json,sys
print(json.dumps({"type":"search","space_name":sys.argv[1],"subpath":sys.argv[2],
                  "search":"","retrieve_total":True,"limit":30}))' "$1" "$2")"
}
count_or_block() {
    python3 -c '
import json, sys
raw = sys.stdin.read()
try:
    d = json.loads(raw)
except Exception:
    print("unreadable: " + raw[:60]); raise SystemExit
if d.get("status") != "success":
    print("blocked (" + str((d.get("error") or {}).get("message"))[:48] + ")")
    raise SystemExit
rs = sorted(r["shortname"] for r in d.get("records", []))
print(("%d record(s): %s" % (len(rs), ",".join(rs[:4]))) if rs else "0 records")'
}

anon_articles="$(pub_query kb articles | count_or_block)"
case "$anon_articles" in
    0*|blocked*|unreadable*)
        cat <<EOF

== 5. public surface: not enabled

   The anonymous user holds no pack role, so the help centre and the intake
   form are both closed. Re-run with: install.sh --public
EOF
        ;;
    *)
        cat <<'EOF'

== 5. the public help centre

   No token at all. The kb pack's articles are readable by anyone once
   --public has granted `kb_public` to the anonymous user; nothing else is.
EOF
        printf '  %-46s %s\n' "anonymous reads kb/articles" "$anon_articles"
        printf '  %-46s %s\n' "anonymous reads one article directly" \
            "$(pub_get /public/entry/content/kb/articles/no_signal_triage \
               | python3 -c 'import sys; t=sys.stdin.read(); print("ok" if "\"shortname\"" in t else "blocked")')"
        for pair in "servicedesk cases" "servicedesk intake" "org sites" "datamart kpis"; do
            set -- $pair
            printf '  %-46s %s\n' "anonymous reads $1/$2" "$(pub_query "$1" "$2" | count_or_block)"
        done

        cat <<'EOF'

   Those four are the point: the public role opens exactly kb/articles and
   nothing else. `intake` is closed too — a public caller may POST a case and
   may not read one back, not even the one they just filed.

== 6. anonymous intake, then customer self-service

   Two doors, because they differ in who ends up OWNING the case.
EOF
        printf '  %-46s ' "anonymous posts through the public form"
        curl -sS -m 20 -X POST \
            "$URL/public/submit/servicedesk/ticket/servicedesk_case/intake_case/intake" \
            -H 'Content-Type: application/json' \
            -d '{"title":"No service on the Karbala ring road","category":"no_signal","description":"Out since this morning across the whole street.","contact_msisdn":"+9647700000233","contact_name":"Nasreen","city":"Karbala"}' \
          | python3 -c '
import json, sys
d = json.load(sys.stdin)
if d.get("status") != "success":
    e = d.get("error") or {}
    msg = str(e.get("message"))[:60]
    if "not allowed" in msg.lower() or "location" in msg.lower():
        msg += "  (set ALLOWED_SUBMIT_MODELS=servicedesk.intake_case)"
    print("refused: " + msg); raise SystemExit
r = d["records"][0]
print("accepted as %s, owned by %s"
      % (r.get("shortname"), r["attributes"].get("owner_shortname")))'

        for who in customer_erbil customer_basra; do
            t="$(login "$who" "$PW" 2>/dev/null || echo)"
            if [ -z "$t" ]; then
                printf '  %-46s %s\n' "$who signs in" "no such user (install the servicedesk pack)"
                continue
            fi
            printf '  %-46s ' "$who sees, of all 6+ cases"
            curl -sS -m 20 -X POST "$URL/managed/query" \
                -H 'Content-Type: application/json' -H "Authorization: Bearer $t" \
                -d '{"type":"search","space_name":"servicedesk","subpath":"cases","search":"","retrieve_total":true,"limit":30}' \
              | count_or_block
        done

        cat <<'EOF'

   A customer's own cases and no one else's. `own` is doing that, though not
   the way it reads: conditions are exempt on `query`, so the filtering comes
   from BuildUserQueryPoliciesAsync emitting an owner-segment policy pattern
   that the SQL ACL filter matches per row. `view` takes the other path, where
   the condition IS enforced — so a cross-customer read is refused too.

   And the anonymous submission above is owned by `anonymous`, not by the
   person who filed it. That is why the approved design has both doors: the
   public form for someone with no account, an authenticated create for
   someone who wants to track what they raised.
EOF
        ;;
esac

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
    cat <<'EOF'

== 8. the archive: twelve months that nothing replayed

   Six cases shipped WITH their history, dated across 2025-11 to 2026-08 and
   attributed to the agent or supervisor who did the work. No script walked
   them: the rows arrived as data and the importer kept the authored uuid and
   timestamp.
EOF
    curl -sS -m 20 -X POST "$URL/managed/query" \
        -H 'Content-Type: application/json' -H "Authorization: Bearer $ADMIN_TOKEN" \
        -d '{"type":"history","space_name":"servicedesk","subpath":"cases","limit":200}' \
      | python3 -c '
import json, sys
d = json.load(sys.stdin)
if d.get("status") != "success":
    print("  (history query unavailable: %s)"
          % str((d.get("error") or {}).get("message"))[:60]); raise SystemExit
rows = []
for r in d.get("records", []):
    a = r["attributes"]
    diff = a.get("diff") or {}
    st = diff.get("state") or {}
    # Sort on the FULL timestamp, print only the date: two transitions on the
    # same day would otherwise come out in arbitrary order and read as a
    # ticket resolving before it was taken.
    rows.append((str(a.get("timestamp")), r["shortname"],
                 a.get("owner_shortname"),
                 "%s->%s" % (st.get("old"), st.get("new")),
                 (diff.get("resolution_reason") or {}).get("new") or ""))
rows.sort()
# Only the authored archive: everything dated before today was shipped, not
# replayed. The live cases demo.sh just drove are stamped now.
import datetime
today = datetime.date.today().isoformat()
old = [r for r in rows if r[0][:10] < today]
for ts, sn, who, move, why in old:
    print("  %-11s %-14s %-16s %-26s %s" % (ts[:10], sn, who, move, why))
print()
print("  %d authored row(s) spanning %s to %s"
      % (len(old), old[0][0][:10], old[-1][0][:10]) if old else "  (none)")
new = len(rows) - len(old)
print("  %d row(s) written by this run, stamped today" % new)'
fi

cat <<'EOF'

== done

Two kinds of history sit side by side above. Sections 1-4 were driven live:
every state, every refusal and every row came from dmart's own workflow engine
and permission walk, stamped now. Section 8 was shipped as data, dated across
twelve months and attributed to the people who did the work — which only works
because the importer preserves an authored uuid and timestamp.
EOF
