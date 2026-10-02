#!/usr/bin/env python3
"""Generate each pack's content schemas into packs/<name>/space/schema/.

A schema entry is `schema/.dm/<sn>/meta.schema.json` whose payload names
`meta_schema` as its own schema, plus the JSON Schema itself at
`schema/<sn>.json` — the shape the seeded management space uses
(seed/spaces/management/schema/.dm/workflow/meta.schema.json).

The `checksum`, `last_validated` and `validation_status` fields the seeded
schemas carry are written by the server when it validates; they are not
authored here.

Scalar link fields (`site`, `region`, `product`, `article`) are deliberately
part of these schemas even though the same edges are carried as real
`relationships`. Relationship filtering does not work — `@relationships[]...`
is silently dropped from the WHERE clause and returns UNFILTERED rows (PLAN.md
verification 2) — so anything a UI needs to filter on has to exist as a plain
payload.body key. These are those keys.
"""
import json, os, sys, uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shanidar as SH

NS = uuid.UUID("6f9619ff-8b86-d011-b42d-00c04fc964ff")
WHEN = "2026-10-01T00:00:00"
OWNER = "dmart"

REGIONS = ["north", "central", "south"]

def uid(*parts):
    return str(uuid.uuid5(NS, "shanidar/" + "/".join(parts)))

def write_json(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        json.dump(obj, f, indent=2, ensure_ascii=False)
        f.write("\n")

def schema_meta(pack, sn):
    return {
        "uuid": uid(pack, "schema", sn),
        "shortname": sn,
        "is_active": True,
        "tags": [],
        "created_at": WHEN,
        "updated_at": WHEN,
        "owner_shortname": OWNER,
        "payload": {
            "content_type": "json",
            "schema_shortname": "meta_schema",
            "body": f"{sn}.json",
        },
    }

def obj(title, description, props, required):
    return {
        "title": title,
        "description": description,
        "type": "object",
        "additionalProperties": False,
        "properties": props,
        "required": required,
    }

S = lambda **kw: dict(type="string", **kw)
I = lambda **kw: dict(type="integer", **kw)
N = lambda **kw: dict(type="number", **kw)
B = lambda **kw: dict(type="boolean", **kw)
ARR = lambda items: dict(type="array", items=items)
ENUM = lambda *vals: dict(type="string", enum=list(vals))

# `kb` is deliberately absent: its articles are MARKDOWN entries, and a schema
# validates `payload.body` as JSON. Articles carry their metadata as `tags`
# instead, which `@tags:coverage` can filter (tags is a real array column).
SCHEMAS = {
    "org": {
        "region": obj(
            "Region", "An operating region of the network.",
            {
                "code": ENUM(*REGIONS),
                "name": S(),
                "hq_city": S(),
                "site_count": I(minimum=0),
            },
            ["code", "name"]),
        "site": obj(
            "Site", "A physical network site: a tower, rooftop or shelter.",
            {
                "name": S(),
                "region": ENUM(*REGIONS),
                "city": S(),
                "kind": ENUM("macro", "rooftop", "shelter", "cow"),
                "status": ENUM("live", "planned", "maintenance", "decommissioned"),
                "commissioned_on": S(format="date"),
                "latitude": N(minimum=28.5, maximum=37.5),
                "longitude": N(minimum=38.5, maximum=48.5),
                "grid_hours_per_day": I(minimum=0, maximum=24),
            },
            ["name", "region", "city", "kind", "status"]),
    },
    "catalogue": {
        "product": obj(
            "Product", "A sellable mobile or fixed-wireless product.",
            {
                "name": S(),
                "family": ENUM("prepaid", "postpaid", "fixed_wireless", "iot"),
                "status": ENUM("active", "retired"),
                "launched_on": S(format="date"),
                "summary": S(),
            },
            ["name", "family", "status"]),
        "tariff": obj(
            "Tariff", "A priced plan attached to a product.",
            {
                "name": S(),
                # Link field: `@payload.body.product:prepaid_basic` filters,
                # `@relationships[]...` does not.
                "product": S(),
                "monthly_iqd": I(minimum=0),
                "data_gb": N(minimum=0),
                "voice_minutes": I(minimum=0),
                "status": ENUM("active", "retired"),
            },
            ["name", "product", "monthly_iqd", "status"]),
    },
    "assets": {
        "equipment": obj(
            "Equipment", "A serviceable unit installed at a site.",
            {
                "name": S(),
                "kind": ENUM("generator", "cabinet", "cow", "battery_bank", "aircon"),
                "site": S(),
                "region": ENUM(*REGIONS),
                "status": ENUM("in_service", "faulty", "standby", "retired"),
                "installed_on": S(format="date"),
                "rated_kva": N(minimum=0),
                "last_service_on": S(format="date"),
            },
            ["name", "kind", "site", "region", "status"]),
        "maintenance_visit": obj(
            "Maintenance visit", "A scheduled or reactive visit to a site.",
            {
                "summary": S(),
                "site": S(),
                "region": ENUM(*REGIONS),
                "equipment": ARR(S()),
                "kind": ENUM("scheduled", "reactive", "upgrade"),
                "performed_on": S(format="date"),
                "technician": S(),
                "outcome": ENUM("completed", "partial", "deferred"),
                "notes": S(),
            },
            ["summary", "site", "region", "kind", "performed_on", "outcome"]),
    },
    "servicedesk": {
        "case": obj(
            "Case", "A customer-reported case worked by the service desk.",
            {
                "title": S(),
                "category": ENUM("no_signal", "slow_data", "billing", "device", "capacity"),
                "severity": ENUM("low", "normal", "high", "critical"),
                "region": ENUM(*REGIONS),
                # Link fields, duplicated from `relationships` so they filter.
                "site": S(),
                "product": S(),
                "equipment": S(),
                "article": S(),
                # Digits-only: dmart validates msisdn as ^\+?[0-9]{6,15}$,
                # and the demo keeps one spelling everywhere rather than two.
                "customer_msisdn": S(pattern=r"^\+9647[0-9]0000[0-9]{4}$"),
                "reported_on": S(format="date"),
                # The catalogue of closing reasons. Presence of the TOP-LEVEL
                # resolution_reason is enforced by the workflow's
                # resolution_required; its VALUE is enforced by nothing in dmart
                # (PLAN.md verification 3), so the vocabulary is pinned here and
                # a closing agent is expected to copy it into both places.
                "resolution_code": ENUM(*[c for c, _ in SH.RESOLUTION_CODES]),
                "resolution_note": S(),
            },
            ["title", "category", "severity", "region", "reported_on"]),
    },
    "approvals": {
        "access_request": obj(
            "Access request", "A request to enter or work on a site.",
            {
                "summary": S(),
                "site": S(),
                "region": ENUM(*REGIONS),
                "requested_by": S(),
                "requested_for": S(format="date"),
                "reason": ENUM("maintenance", "installation", "audit", "emergency"),
                "escort_required": B(),
                "rejection_code": ENUM(*[c for c, _ in SH.REJECTION_CODES]),
                "decision_note": S(),
            },
            ["summary", "site", "region", "requested_by", "requested_for", "reason"]),
    },
    "datamart": {
        "kpi_row": obj(
            "KPI row", "One site-month of network KPIs, queryable as an entry.",
            {
                "site": S(),
                "region": ENUM(*REGIONS),
                "period": S(pattern=r"^[0-9]{4}_[0-9]{2}$"),
                "availability_pct": N(minimum=0, maximum=100),
                "dropped_call_pct": N(minimum=0, maximum=100),
                "data_volume_tb": N(minimum=0),
                "outage_minutes": I(minimum=0),
            },
            ["site", "region", "period", "availability_pct"]),
        "dataset": obj(
            "Dataset", "A published dataset; the file rides as a data_asset.",
            {
                "title": S(),
                "period": S(pattern=r"^[0-9]{4}_[0-9]{2}$"),
                "format": ENUM("csv", "jsonl", "parquet", "sqlite"),
                "row_count": I(minimum=0),
                "columns": ARR(S()),
                "note": S(),
            },
            ["title", "period", "format", "row_count"]),
    },
    "comms": {
        "notice": obj(
            "Notice", "An outbound customer or staff notice.",
            {
                "title": S(),
                "channel": ENUM("sms", "email", "app_push", "staff_portal"),
                "audience": ENUM("all", "region", "product", "staff"),
                "region": ENUM(*REGIONS),
                "site": S(),
                "article": S(),
                "send_on": S(format="date"),
                "status": ENUM("draft", "scheduled", "sent", "cancelled"),
            },
            ["title", "channel", "audience", "status"]),
    },
}

def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    for pack, schemas in sorted(SCHEMAS.items()):
        manifest = os.path.join(root, pack, "pack.json")
        if not os.path.isfile(manifest):
            raise SystemExit(f"no pack.json for '{pack}'")
        if "schema" not in json.load(open(manifest))["folders"]:
            raise SystemExit(f"pack '{pack}' has no schema folder declared")
        base = f"{root}/{pack}/space/schema"
        for sn, body in sorted(schemas.items()):
            write_json(f"{base}/.dm/{sn}/meta.schema.json", schema_meta(pack, sn))
            write_json(f"{base}/{sn}.json", body)
        print(f"  {pack}: {len(schemas)} schema(s) — {', '.join(sorted(schemas))}")

if __name__ == "__main__":
    main()
