#!/usr/bin/env python3
"""Generate each pack's management overlay: its roles and permissions.

One overlay per pack under packs/<name>/management/, laid out exactly as the
management space is on disk so build.sh can merge them into a single
`management/` tree for one import.

Groups are NOT here. They do not round-trip through import/export (PLAN.md
finding 3, measured 2026-10-02: a group present in the `groups` table was
simply absent from the archive), so install.sh creates them over the API.
"""
import json, os, uuid

NS = uuid.UUID("6f9619ff-8b86-d011-b42d-00c04fc964ff")
WHEN = "2026-10-01T00:00:00"
OWNER = "dmart"

# The resource types a pack's content actually uses. `comment`, `reaction` and
# `relationship` are attachments but are still named here: the permission gate
# reads resource_type per record, not per table.
CONTENT_TYPES = ["content", "folder", "json", "media", "comment", "reaction",
                 "relationship", "data_asset"]
TICKET_TYPES = CONTENT_TYPES + ["ticket"]

def uid(*parts):
    return str(uuid.uuid5(NS, "shanidar/" + "/".join(parts)))

def write_json(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        json.dump(obj, f, indent=2, ensure_ascii=False)
        f.write("\n")

def permission(shortname, space, subpaths, actions, types, conditions=None):
    return {
        "uuid": uid("permission", shortname),
        "shortname": shortname,
        "is_active": True,
        "tags": [],
        "created_at": WHEN,
        "updated_at": WHEN,
        "owner_shortname": OWNER,
        "subpaths": {space: subpaths},
        "resource_types": types,
        "actions": actions,
        "conditions": conditions or [],
    }

def role(shortname, permissions):
    return {
        "uuid": uid("role", shortname),
        "shortname": shortname,
        "is_active": True,
        "tags": [],
        "created_at": WHEN,
        "updated_at": WHEN,
        "owner_shortname": OWNER,
        "permissions": permissions,
    }

# Per-pack permission bodies. Kept explicit rather than derived: what each role
# may do is a design decision per pack, not a pattern to infer.
READ = ["query", "view"]
WRITE = READ + ["create", "update"]

def perms_for(name, pack):
    space = pack["space"]
    all_sub = ["__all_subpaths__"]
    out = {}
    if name == "org":
        out["org_viewer_read"] = permission(
            "org_viewer_read", space, all_sub, READ, CONTENT_TYPES)
    elif name == "catalogue":
        out["catalogue_editor_write"] = permission(
            "catalogue_editor_write", space, ["products", "tariffs"], WRITE, CONTENT_TYPES)
    elif name == "assets":
        # `own` scopes a technician to the equipment they own — the regional
        # groups carry the ownership (PLAN.md "Personas").
        out["assets_technician_write"] = permission(
            "assets_technician_write", space, ["equipment", "maintenance"],
            WRITE, CONTENT_TYPES, conditions=["own"])
    elif name == "servicedesk":
        out["servicedesk_agent_case"] = permission(
            "servicedesk_agent_case", space, ["cases"],
            WRITE + ["progress_ticket"], TICKET_TYPES)
        out["servicedesk_supervisor_case"] = permission(
            "servicedesk_supervisor_case", space, all_sub,
            WRITE + ["progress_ticket", "assign", "delete"], TICKET_TYPES)
    elif name == "approvals":
        out["approvals_security_review"] = permission(
            "approvals_security_review", space, ["requests"],
            READ + ["update", "progress_ticket"], TICKET_TYPES)
    elif name == "kb":
        out["kb_author_write"] = permission(
            "kb_author_write", space, ["articles"], WRITE, CONTENT_TYPES)
    elif name == "datamart":
        out["datamart_analyst_read"] = permission(
            "datamart_analyst_read", space, all_sub, READ, CONTENT_TYPES)
    elif name == "comms":
        out["comms_editor_write"] = permission(
            "comms_editor_write", space, ["notices"], WRITE, CONTENT_TYPES)
    return out

# Which permissions each role holds. A role may hold several; a permission may
# be held by several roles.
ROLE_PERMISSIONS = {
    "org_viewer": ["org_viewer_read"],
    "catalogue_editor": ["catalogue_editor_write"],
    "assets_technician": ["assets_technician_write"],
    "servicedesk_agent": ["servicedesk_agent_case"],
    "servicedesk_supervisor": ["servicedesk_supervisor_case"],
    "approvals_security": ["approvals_security_review"],
    "kb_author": ["kb_author_write"],
    "datamart_analyst": ["datamart_analyst_read"],
    "comms_editor": ["comms_editor_write"],
}

def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    names = sorted(
        d for d in os.listdir(root)
        if os.path.isfile(os.path.join(root, d, "pack.json")))
    for name in names:
        pack = json.load(open(f"{root}/{name}/pack.json"))
        base = f"{root}/{name}/management"
        perms = perms_for(name, pack)
        declared = set(pack["provides"]["permissions"])
        if set(perms) != declared:
            raise SystemExit(
                f"{name}: pack.json declares permissions {sorted(declared)} "
                f"but the generator builds {sorted(perms)} — keep them in step")
        for sn, body in perms.items():
            write_json(f"{base}/permissions/.dm/{sn}/meta.permission.json", body)
        for sn in pack["provides"]["roles"]:
            if sn not in ROLE_PERMISSIONS:
                raise SystemExit(f"{name}: role {sn} has no permission mapping")
            write_json(f"{base}/roles/.dm/{sn}/meta.role.json",
                       role(sn, ROLE_PERMISSIONS[sn]))
        print(f"  {name}: {len(perms)} permission(s), "
              f"{len(pack['provides']['roles'])} role(s), "
              f"{len(pack['provides']['groups'])} group(s) via API")

if __name__ == "__main__":
    main()
