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
# Which storyline this generator is building. Everything dataset-specific reads
# from here, so a second storyline (a school, a restaurant) is a new value plus
# a new data module — not a fork of the machinery.
#
# It feeds the uuid5 seed, so it is NOT cosmetic: changing the value for an
# existing dataset would re-identify every row in every install of it. Hence
# the default is the name of the dataset that already exists.
DATASET = os.environ.get("PACKS_DATASET", "shanidar")

OWNER = "dmart"

def uid(*parts):
    return str(uuid.uuid5(NS, DATASET + "/" + "/".join(parts)))

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
        "tags": [DATASET, "pack:" + pack["name"]],
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

# What each folder is allowed to hold. dmart enforces both of these on write
# (FolderContentValidator), so a folder that declares its schema rejects an
# entry of the wrong shape instead of silently storing it.
#
# A folder absent from this table accepts anything — `schema` and `workflows`
# hold pack machinery whose shapes vary, and kb's `articles` hold MARKDOWN,
# which a JSON Schema cannot validate.
FOLDER_CONTENT = {
    ("org", "regions"):            (["region"], ["content"]),
    ("org", "sites"):              (["site"], ["content"]),
    ("catalogue", "products"):     (["product"], ["content"]),
    ("catalogue", "tariffs"):      (["tariff"], ["content"]),
    ("assets", "equipment"):       (["equipment"], ["content"]),
    ("assets", "maintenance"):     (["maintenance_visit"], ["content"]),
    ("servicedesk", "cases"):      (["case"], ["ticket"]),
    ("servicedesk", "intake"):     (["intake_case"], ["ticket"]),
    ("approvals", "requests"):     (["access_request"], ["ticket"]),
    ("datamart", "datasets"):      (["dataset"], ["content"]),
    ("datamart", "kpis"):          (["kpi_row"], ["content"]),
    ("comms", "notices"):          (["notice"], ["content"]),
}

# Columns the admin UI lists for a folder, beyond shortname. Chosen per folder
# so a listing is readable rather than a wall of identical rows.
INDEX_EXTRA = {
    ("org", "sites"):          [("region", "Region"), ("city", "City"), ("status", "Status")],
    ("org", "regions"):        [("name", "Name"), ("hq_city", "HQ")],
    ("catalogue", "products"): [("family", "Family"), ("status", "Status")],
    ("catalogue", "tariffs"):  [("product", "Product"), ("monthly_iqd", "IQD/month")],
    ("assets", "equipment"):   [("kind", "Kind"), ("site", "Site"), ("status", "Status")],
    ("assets", "maintenance"): [("site", "Site"), ("performed_on", "Date"), ("outcome", "Outcome")],
    ("servicedesk", "cases"):  [("state", "State"), ("severity", "Severity"), ("region", "Region")],
    ("servicedesk", "intake"): [("state", "State"), ("category", "Category"), ("city", "City")],
    ("approvals", "requests"): [("state", "State"), ("site", "Site"), ("requested_for", "For")],
    ("datamart", "kpis"):      [("site", "Site"), ("period", "Period"), ("availability_pct", "Availability %")],
    ("datamart", "datasets"):  [("period", "Period"), ("format", "Format"), ("row_count", "Rows")],
    ("comms", "notices"):      [("channel", "Channel"), ("send_on", "Send on"), ("status", "Status")],
    ("kb", "articles"):        [("displayname", "Title")],
}

def folder_body(pack_name, folder):
    # folder_rendering requires index_attributes; everything else is optional.
    # allow_create/update stay false on `schema` and `workflows` so the admin UI
    # does not invite editing pack machinery by hand.
    machinery = folder in ("schema", "workflows")
    schemas, types = FOLDER_CONTENT.get((pack_name, folder), ([], []))
    index = [{"key": "shortname", "name": "Shortname"}]
    for key, label in INDEX_EXTRA.get((pack_name, folder), []):
        index.append({"key": key, "name": label})
    body = {
        "shortname_title": "Shortname",
        "content_schema_shortnames": schemas,
        "index_attributes": index,
        "allow_view": True,
        "allow_create": not machinery,
        "allow_update": not machinery,
        "allow_delete": False,
        "use_media": False,
        "filter": [],
    }
    if types:
        body["content_resource_types"] = types
    return body

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
            write_json(f"{base}/{folder}.json", folder_body(name, folder))
        print(f"  {name}: space + {len(pack['folders'])} folders")

if __name__ == "__main__":
    main()
