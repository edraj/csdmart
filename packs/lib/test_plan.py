#!/usr/bin/env python3
"""Self-test for the update planner.

    python3 packs/lib/test_plan.py

Worth having because the planner decides whether an operator's data gets
overwritten, and every case below is one I got wrong at least once while
building it. Plain asserts and no test framework, since the repo has no Python
harness and this has to stay runnable with nothing installed.

NOT run by CI today — there is no Python step in .github/workflows/ci.yml.
Run it after touching plan_update.py.
"""
import os, sys, tempfile, json, shutil

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import plan_update as P

FAILED = []

def check(name, got, want):
    if got != want:
        FAILED.append(f"{name}: expected {want}, got {got}")
        print(f"  FAIL  {name}: expected {want}, got {got}")
    else:
        print(f"  ok    {name}")

def counts(plan):
    return {k: len(v) for k, v in plan.items() if v}


# ── path decoding ─────────────────────────────────────────────────────────────

check("decode space", P.decode_meta_path("org/.dm/meta.space.json"),
      ("org", "/", "org", "space"))
check("decode folder", P.decode_meta_path("org/sites/.dm/meta.folder.json"),
      ("org", "/", "sites", "folder"))
check("decode nested folder",
      P.decode_meta_path("org/a/b/.dm/meta.folder.json"),
      ("org", "/a", "b", "folder"))
check("decode entry",
      P.decode_meta_path("org/sites/.dm/erb_0142/meta.content.json"),
      ("org", "/sites", "erb_0142", "content"))
check("decode ticket",
      P.decode_meta_path("servicedesk/cases/.dm/c1/meta.ticket.json"),
      ("servicedesk", "/cases", "c1", "ticket"))
check("decode root entry",
      P.decode_meta_path("mgmt/.dm/thing/meta.content.json"),
      ("mgmt", "/", "thing", "content"))
check("attachment is not planned",
      P.decode_meta_path("sd/cases/.dm/c1/attachments.comment/meta.c01.json"),
      None)

# ── timestamp normalisation ───────────────────────────────────────────────────

check("file vs db spelling agree",
      P.norm_stamp("2026-10-01T00:00:00") == P.norm_stamp("2026-10-01 00:00:00.0000000"),
      True)
check("seven fractional digits parse",
      P.norm_stamp("2026-10-01 00:00:00.1234567") is not None, True)
check("a different instant is different",
      P.norm_stamp("2026-10-01T00:00:00") == P.norm_stamp("2026-10-02T00:00:00"),
      False)

# ── version ordering ──────────────────────────────────────────────────────────

check("version orders", P.parse_version("2.0.0") > P.parse_version("1.9.9"), True)
try:
    P.parse_version("1.0.0-rc1")
    check("non-numeric version is refused", False, True)
except ValueError:
    check("non-numeric version is refused", True, True)

# ── the plan ──────────────────────────────────────────────────────────────────

SHIPPED_V1 = "2026-10-01T00:00:00"
SHIPPED_V2 = "2026-11-20T09:00:00"
EDITED     = "2026-10-04T07:00:00"

def entry(space, sub, sn, stamp, rt="content"):
    return {"space": space, "subpath": sub, "shortname": sn,
            "rt": rt, "updated_at": stamp}

def run(shipped, receipt, db):
    return P.build_plan(shipped, receipt, db)

# A fresh install: nothing in the receipt, nothing in the database.
plan = run({"org/sites/.dm/a/meta.content.json": entry("org", "/sites", "a", SHIPPED_V1)},
           None, {})
check("fresh install adds", counts(plan), {"add": 1})

# Adopting an install that predates receipts: the row is there, untouched.
plan = run({"org/sites/.dm/a/meta.content.json": entry("org", "/sites", "a", SHIPPED_V1)},
           None, {("org", "/sites", "a"): SHIPPED_V1})
check("adopting an untouched row is a no-op", counts(plan), {"unchanged": 1})

# Adopting one the operator has since edited: protect it, do not overwrite.
plan = run({"org/sites/.dm/a/meta.content.json": entry("org", "/sites", "a", SHIPPED_V1)},
           None, {("org", "/sites", "a"): EDITED})
check("adopting an edited row keeps it", counts(plan), {"kept": 1})

receipt = {"version": "1.0.0",
           "entries": {"org/sites/.dm/a/meta.content.json": SHIPPED_V1}}

# The pack changed it, the operator did not: safe to update.
plan = run({"org/sites/.dm/a/meta.content.json": entry("org", "/sites", "a", SHIPPED_V2)},
           receipt, {("org", "/sites", "a"): SHIPPED_V1})
check("pack-only change updates", counts(plan), {"update": 1})

# Both changed it: the operator wins and the update reports it.
plan = run({"org/sites/.dm/a/meta.content.json": entry("org", "/sites", "a", SHIPPED_V2)},
           receipt, {("org", "/sites", "a"): EDITED})
check("both changed -> conflict, never overwritten", counts(plan), {"conflict": 1})

# Only the operator changed it: left alone, not reverted.
plan = run({"org/sites/.dm/a/meta.content.json": entry("org", "/sites", "a", SHIPPED_V1)},
           receipt, {("org", "/sites", "a"): EDITED})
check("operator-only change is kept", counts(plan), {"kept": 1})

# Dropped by the new version, untouched: remove it.
plan = run({}, receipt, {("org", "/sites", "a"): SHIPPED_V1})
check("dropped and untouched -> removed", counts(plan), {"remove": 1})

# Dropped by the new version but edited: keep it and say so.
plan = run({}, receipt, {("org", "/sites", "a"): EDITED})
check("dropped but edited -> orphan, kept", counts(plan), {"orphan": 1})

# Deleted out from under us: re-add rather than silently skip.
plan = run({"org/sites/.dm/a/meta.content.json": entry("org", "/sites", "a", SHIPPED_V1)},
           receipt, {})
check("deleted by hand -> re-added", counts(plan), {"add": 1})

# A SPACE row: its updated_at is always the write moment, so it must never be
# read as an operator edit, and never auto-deleted.
plan = run({"org/.dm/meta.space.json": entry("org", "/", "org", SHIPPED_V1, "space")},
           None, {("org", "/", "org"): EDITED})
check("a space is not judged by its timestamp", counts(plan), {"unchanged": 1})
space_receipt = {"version": "1.0.0", "entries": {"org/.dm/meta.space.json": SHIPPED_V1}}
plan = run({}, space_receipt, {("org", "/", "org"): SHIPPED_V1})
check("a space is never auto-deleted", counts(plan), {"orphan": 1})

# A role: same non-signal, but dropping one IS safe — a stale grant is worse.
role_receipt = {"version": "1.0.0",
                "entries": {"management/roles/.dm/org_viewer/meta.role.json": SHIPPED_V1}}
plan = run({}, role_receipt, {("management", "/roles", "org_viewer"): EDITED})
check("a dropped role is removed", counts(plan), {"remove": 1})

# ── attachments follow their parent ───────────────────────────────────────────

tmp = tempfile.mkdtemp()
try:
    d = f"{tmp}/sd/cases/.dm/c1"
    os.makedirs(f"{d}/attachments.comment")
    json.dump({"shortname": "c1", "updated_at": SHIPPED_V1},
              open(f"{d}/meta.ticket.json", "w"))
    json.dump({"shortname": "c01"},
              open(f"{d}/attachments.comment/meta.c01.json", "w"))
    atts = P.attachment_paths(tmp)
    check("attachment maps to its parent",
          atts.get("sd/cases/.dm/c1/attachments.comment/meta.c01.json"),
          "sd/cases/.dm/c1/meta.ticket.json")
    check("the parent itself is not an attachment", len(atts), 1)
finally:
    shutil.rmtree(tmp, ignore_errors=True)

print()
if FAILED:
    print(f"{len(FAILED)} check(s) FAILED")
    sys.exit(1)
print("all checks passed")
