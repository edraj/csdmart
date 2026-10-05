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
# Which storyline this generator is building. Everything dataset-specific reads
# from here, so a second storyline (a school, a restaurant) is a new value plus
# a new data module — not a fork of the machinery.
#
# It feeds the uuid5 seed, so it is NOT cosmetic: changing the value for an
# existing dataset would re-identify every row in every install of it. Hence
# the default is the name of the dataset that already exists.
DATASET = os.environ.get("PACKS_DATASET", "shanidar")

OWNER = "dmart"

# The resource types a pack's content actually uses. `comment`, `reaction` and
# `relationship` are attachments but are still named here: the permission gate
# reads resource_type per record, not per table.
CONTENT_TYPES = ["content", "folder", "json", "media", "comment", "reaction",
                 "relationship", "data_asset"]
TICKET_TYPES = CONTENT_TYPES + ["ticket"]

def uid(*parts):
    return str(uuid.uuid5(NS, DATASET + "/" + "/".join(parts)))

def write_json(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        json.dump(obj, f, indent=2, ensure_ascii=False)
        f.write("\n")

def permission(shortname, space, subpaths, actions, types, conditions=None,
               filter_fields_values=None):
    out = {
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
    if filter_fields_values:
        # Row-level narrowing on top of the subpath grant. The string is merged
        # into the caller's search clause (QueryService's "filter fields
        # values" block), so it filters by a payload VALUE — which is how you
        # scope by region, since region is a field rather than a subpath.
        #
        # Admin-managed input by design: the trust note in QueryService is
        # explicit that these strings are concatenated verbatim, so nothing
        # outside a pack's own generator should ever compose one.
        out["filter_fields_values"] = filter_fields_values
    return out

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
            "servicedesk_agent_case", space, ["cases", "intake"],
            WRITE + ["progress_ticket"], TICKET_TYPES)
        out["servicedesk_supervisor_case"] = permission(
            "servicedesk_supervisor_case", space, all_sub,
            WRITE + ["progress_ticket", "assign", "delete"], TICKET_TYPES)
        # A signed-in customer creates their OWN case and sees only their own.
        #
        # `own` does the row filtering, but not the way it first looks:
        # CheckConditions is exempt for create and query, so `own` is NOT what
        # gates the query. BuildUserQueryPoliciesAsync emits a policy pattern
        # with the actor's shortname (and their groups) in the owner segment —
        # `<space>:<subpath>:<rt>:<is_active>:<owner>` — and the SQL ACL filter
        # matches it against each row's own query_policies. That is what makes
        # a customer's query return their cases and nobody else's.
        #
        # `view` is a different path: conditions ARE enforced there, and `own`
        # is achieved only when the row's owner is the actor. So the two agree.
        out["servicedesk_customer_own"] = permission(
            "servicedesk_customer_own", space, ["cases"],
            ["query", "view", "create", "update"],
            ["ticket", "comment", "json", "media"], conditions=["own"])
        # Anonymous intake, scoped to `intake` and `create` ONLY — a public
        # caller may post a case and may not read anything back, not even the
        # one they just posted. /public/submit owns the entry as `anonymous`
        # (PLAN.md finding 5), so there is no "their own" to read.
        out["servicedesk_public_intake"] = permission(
            "servicedesk_public_intake", space, ["intake"],
            ["create"], ["ticket"])
        # The assignment plugin's service account. Reads the routing table and
        # the case, writes the assignee: no create, no delete, no
        # progress_ticket, and nothing outside this space. A plugin that can
        # only do the one thing it exists for is the argument for REST over the
        # save_entry callback, which has no scope at all.
        #
        # `routing` is read-only in practice — the actions are shared across the
        # subpaths, and dmart has no per-subpath action list, so `update` on
        # routing is granted as a side effect. Worth knowing rather than
        # claiming a tightness the permission model cannot express.
        out["servicedesk_automation_assign"] = permission(
            "servicedesk_automation_assign", space,
            ["cases", "intake", "routing"],
            ["query", "view", "update"], ["ticket", "content"])
    elif name == "approvals":
        out["approvals_security_review"] = permission(
            "approvals_security_review", space, ["requests"],
            READ + ["update", "progress_ticket"], TICKET_TYPES)
    elif name == "kb":
        out["kb_author_write"] = permission(
            "kb_author_write", space, ["articles"], WRITE, CONTENT_TYPES)
        # The public help centre: anonymous read of the articles.
        #
        # Deliberately NO conditions. `is_active` would be the obvious choice,
        # but conditions are enforced on `view` and exempt on `query`
        # (PermissionService.CheckConditions), so an `is_active` world
        # permission passes /public/query and then FAILS the direct
        # /public/entry view of the same article — a split that looks like a
        # bug to whoever hits it. Everything under kb/articles is intended to
        # be public, so the permission says exactly that.
        out["kb_public_read"] = permission(
            "kb_public_read", space, ["articles"],
            ["query", "view"], ["content", "media", "json"])
    elif name == "datamart":
        out["datamart_analyst_read"] = permission(
            "datamart_analyst_read", space, all_sub, READ, CONTENT_TYPES)
        # The rollup plugin's service account. Scoped to exactly what it
        # writes: it reads the KPI rows and maintains the rollups, and can do
        # nothing else anywhere. That narrowness is the point of using REST
        # with an account rather than the save_entry callback, which has no
        # scope at all — it writes straight through EntryRepository, past
        # validation, permissions and referential integrity.
        # The MCP persona from §8: an ordinary dmart user with a read-only,
        # South-scoped role. MCP has no authorization surface of its own — an
        # MCP client authenticates as a dmart user and every tool runs the same
        # permission walk as any other caller — so "scoping an AI assistant" is
        # just this role.
        #
        # Region is a payload FIELD, not a subpath, so the subpath grant cannot
        # express "south only". filter_fields_values can: it is merged into the
        # caller's search clause, narrowing which rows come back.
        #
        # The subpaths are listed EXPLICITLY rather than __all_subpaths__, and
        # that is load-bearing. filter_fields_values is applied by matching the
        # permission's own key, `space:subpath:resource_type`, as a PREFIX of
        # the request's resolved query policy (QueryService's
        # MergeFilterFieldsValues). With __all_subpaths__ the key reads
        # `datamart:__all_subpaths__:content`, the policy for a query on /kpis
        # reads `datamart:kpis:content:…`, the prefix test fails, and the
        # filter is dropped — silently, so the caller sees EVERY region and
        # nothing reports a problem. Measured: 8 rows across all three regions
        # instead of the 2 southern ones.
        out["datamart_ai_ops_south_read"] = permission(
            "datamart_ai_ops_south_read", space,
            ["kpis", "rollups", "datasets"],
            READ, CONTENT_TYPES,
            filter_fields_values="@payload.body.region:south")
        out["datamart_automation_rollup"] = permission(
            "datamart_automation_rollup", space, ["kpis", "rollups"],
            ["query", "view", "create", "update"], ["content"])
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
    "servicedesk_customer": ["servicedesk_customer_own"],
    "servicedesk_public": ["servicedesk_public_intake"],
    "servicedesk_automation": ["servicedesk_automation_assign"],
    "approvals_security": ["approvals_security_review"],
    "kb_author": ["kb_author_write"],
    "kb_public": ["kb_public_read"],
    "datamart_analyst": ["datamart_analyst_read"],
    "datamart_automation": ["datamart_automation_rollup"],
    "datamart_ai_ops_south": ["datamart_ai_ops_south_read"],
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
