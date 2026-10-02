#!/usr/bin/env python3
"""Generate the per-pack space trees from each pack.json.

Deterministic by construction: UUIDv5 off a fixed namespace and a stable key,
and one fixed timestamp. Re-running must produce a byte-identical tree, because
these files are committed and a churning diff hides real change.
"""
import json, os, uuid, sys

# Fixed namespace — any constant works, this one is the RFC 4122 example UUID.
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

def space_meta(pack):
    # space_name / subpath / resource_type are stripped on export and
    # re-injected from the path on import (PLAN.md §1) — never authored here.
    return {
        "uuid": uid(pack["name"], "space"),
        "shortname": pack["name"],
        "is_active": True,
        "displayname": {"en": pack["title"]},
        "description": {"en": pack["summary"]},
        "tags": ["shanidar", "pack:" + pack["name"]],
        "created_at": WHEN,
        "updated_at": WHEN,
        "owner_shortname": OWNER,
        "languages": ["english"],
        "indexing_enabled": True,
        # `schema` and `workflows` are machinery, not content — hidden the way
        # the seeded management space hides its own.
        "hide_folders": [f for f in ("schema", "workflows") if f in pack["folders"]],
        "active_plugins": [],
    }

def folder_meta(pack, folder):
    return {
        "uuid": uid(pack["name"], "folder", folder),
        "shortname": folder,
        "is_active": True,
        "tags": [],
        "created_at": WHEN,
        "updated_at": WHEN,
        "owner_shortname": OWNER,
        "payload": {
            "content_type": "json",
            "schema_shortname": "folder_rendering",
            "body": f"{folder}.json",
        },
    }

def folder_body(folder):
    # folder_rendering requires index_attributes; everything else is optional.
    # allow_create/update stay false on `schema` so the admin UI does not invite
    # editing pack machinery by hand.
    machinery = folder in ("schema", "workflows")
    return {
        "shortname_title": "Shortname",
        "content_schema_shortnames": [],
        "index_attributes": [
            {"key": "shortname", "name": "Shortname"},
            {"key": "displayname", "name": "Name"},
        ],
        "allow_view": True,
        "allow_create": not machinery,
        "allow_update": not machinery,
        "allow_delete": False,
        "use_media": False,
        "filter": [],
    }

def main():
    # Pack root is this script's parent's parent — packs/lib/ -> packs/.
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    names = sorted(
        d for d in os.listdir(root)
        if os.path.isfile(os.path.join(root, d, "pack.json")))
    for name in names:
        pack = json.load(open(f"{root}/{name}/pack.json"))
        base = f"{root}/{name}/space"
        write_json(f"{base}/.dm/meta.space.json", space_meta(pack))
        for folder in pack["folders"]:
            write_json(f"{base}/{folder}/.dm/meta.folder.json", folder_meta(pack, folder))
            write_json(f"{base}/{folder}.json", folder_body(folder))
        print(f"  {name}: space + {len(pack['folders'])} folders")

if __name__ == "__main__":
    main()
