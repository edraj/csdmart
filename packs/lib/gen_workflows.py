#!/usr/bin/env python3
"""Generate each pack's workflows into packs/<name>/space/workflows/.

A workflow is an ordinary `content` entry whose payload body is the state
machine — dmart reads it with WorkflowEngine at transition time, so this is
"workflows as data" in the literal sense.

Three things about the format, each established from source rather than assumed
(PLAN.md §6 and verification 3):

  * A state is CLOSED when it has no `next`. There is no closed_states list.
  * `resolution_required: true` sits on a `next[]` TRANSITION, not on a state,
    and enforces that a top-level `resolution_reason` is PRESENT. The engine
    reads exactly this key.
  * `resolutions[]` sits on a STATE and is never read by the engine — zero C#
    references. It is the admin UI's picker. Listing a code here does NOT
    constrain what a caller may send.

The workflow entries carry no `schema_shortname`: dmart's own `workflow` schema
lives in the `management` space and schema lookup is per-space, so pointing at
it from here would not resolve. An approximate local copy would be worse than
none — it could reject a valid workflow.
"""
import json, os, sys, uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shanidar as S

NS = uuid.UUID("6f9619ff-8b86-d011-b42d-00c04fc964ff")
WHEN = "2026-10-01T00:00:00"
OWNER = "dmart"

def uid(*parts):
    return str(uuid.uuid5(NS, "shanidar/" + "/".join(parts)))

def write_json(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        json.dump(obj, f, indent=2, ensure_ascii=False)
        f.write("\n")

def resolutions(codes):
    return [{"key": key, "en": label} for key, label in codes]

WORKFLOWS = {
    "servicedesk": {
        "servicedesk_case": {
            "name": "Service desk case",
            "initial_state": "open",
            "states": [
                {
                    "name": "Open",
                    "state": "open",
                    "next": [
                        {"action": "take", "state": "in_progress",
                         "roles": ["servicedesk_agent", "servicedesk_supervisor"]},
                        {"action": "escalate", "state": "escalated",
                         "roles": ["servicedesk_agent"]},
                    ],
                },
                {
                    "name": "In progress",
                    "state": "in_progress",
                    "next": [
                        {"action": "resolve", "state": "resolved",
                         "roles": ["servicedesk_agent", "servicedesk_supervisor"],
                         "resolution_required": True},
                        {"action": "escalate", "state": "escalated",
                         "roles": ["servicedesk_agent"]},
                    ],
                },
                {
                    "name": "Escalated",
                    "state": "escalated",
                    "next": [
                        # Only a supervisor closes an escalated case, and only
                        # a supervisor can hand it back.
                        {"action": "resolve", "state": "resolved",
                         "roles": ["servicedesk_supervisor"],
                         "resolution_required": True},
                        {"action": "return", "state": "in_progress",
                         "roles": ["servicedesk_supervisor"]},
                    ],
                },
                {
                    # No `next` — so dmart derives this as closed.
                    "name": "Resolved",
                    "state": "resolved",
                    "resolutions": resolutions(S.RESOLUTION_CODES),
                },
            ],
        },
    },
    "approvals": {
        "approvals_site_access": {
            "name": "Site access request",
            "initial_state": "submitted",
            "states": [
                {
                    "name": "Submitted",
                    "state": "submitted",
                    "next": [
                        {"action": "approve", "state": "approved",
                         "roles": ["approvals_security"]},
                        {"action": "reject", "state": "rejected",
                         "roles": ["approvals_security"],
                         "resolution_required": True},
                    ],
                },
                {
                    "name": "Approved",
                    "state": "approved",
                    "next": [
                        {"action": "complete", "state": "completed",
                         "roles": ["approvals_security"]},
                    ],
                },
                {"name": "Rejected", "state": "rejected",
                 "resolutions": resolutions(S.REJECTION_CODES)},
                {"name": "Completed", "state": "completed"},
            ],
        },
    },
}

def check(pack, sn, wf):
    """Catch the mistakes that only surface at transition time."""
    states = {s["state"] for s in wf["states"]}
    initial = wf["initial_state"]
    if initial not in states:
        raise SystemExit(f"{sn}: initial_state '{initial}' is not a state")
    for st in wf["states"]:
        for t in st.get("next", []):
            if t["state"] not in states:
                raise SystemExit(
                    f"{sn}: {st['state']} --{t['action']}--> '{t['state']}' "
                    "names no such state")
            # A transition into a CLOSED state without resolution_required
            # closes a ticket with no reason recorded. Allowed by dmart;
            # flagged here because for these two workflows it is a mistake.
            target = next(s for s in wf["states"] if s["state"] == t["state"])
            if "next" not in target and not t.get("resolution_required") \
               and t["action"] != "complete":
                raise SystemExit(
                    f"{sn}: {t['action']} closes the ticket but does not set "
                    "resolution_required")
        if "next" not in st and not st.get("resolutions") \
           and st["state"] != "completed":
            raise SystemExit(
                f"{sn}: closed state '{st['state']}' publishes no resolutions")
    closed = [s["state"] for s in wf["states"] if "next" not in s]
    return closed

def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    for pack, flows in sorted(WORKFLOWS.items()):
        manifest = json.load(open(f"{root}/{pack}/pack.json"))
        if "workflows" not in manifest["folders"]:
            raise SystemExit(f"pack '{pack}' declares no workflows folder")
        base = f"{root}/{pack}/space/workflows"
        for sn, wf in sorted(flows.items()):
            closed = check(pack, sn, wf)
            write_json(f"{base}/.dm/{sn}/meta.content.json", {
                "uuid": uid(pack, "workflow", sn),
                "shortname": sn,
                "is_active": True,
                "tags": ["workflow"],
                "created_at": WHEN,
                "updated_at": WHEN,
                "owner_shortname": OWNER,
                "displayname": {"en": wf["name"]},
                "payload": {"content_type": "json", "body": f"{sn}.json"},
            })
            write_json(f"{base}/{sn}.json", wf)
            gates = sorted({r for s in wf["states"] for t in s.get("next", [])
                            for r in t.get("roles", [])})
            print(f"  {pack}/{sn}: {len(wf['states'])} states, "
                  f"closed={closed}, gates={gates}")

if __name__ == "__main__":
    main()
