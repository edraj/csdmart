#!/usr/bin/env python3
"""Write the Shanidar storyline into packs/datasets/shanidar/<scale>/<pack>/.

Output is laid out exactly like a pack's own `space/` tree, because build.sh
copies it straight over the top.

Deterministic: UUIDv5 from a fixed namespace and one fixed timestamp, so
re-running produces a byte-identical tree. `small` is committed, so a churning
diff would hide real change.

NOTHING here writes a history.jsonl. Re-importing an archive APPENDS history
rather than upserting it (PLAN.md verification 5), so a pack installed three
times would carry three copies; build.sh refuses to build one it finds.

Every cross-entity edge is written twice — as a real `relationships` entry and
as a scalar `payload.body` key — because relationship filtering does not work.
See shanidar.py's module docstring.
"""
import argparse, hashlib, json, os, shutil, sys, uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shanidar as S

NS = uuid.UUID("6f9619ff-8b86-d011-b42d-00c04fc964ff")
WHEN = "2026-10-01T00:00:00"
OWNER = "dmart"

def uid(*parts):
    return str(uuid.uuid5(NS, "shanidar/" + "/".join(str(p) for p in parts)))

def write_json(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        json.dump(obj, f, indent=2, ensure_ascii=False)
        f.write("\n")

def write_text(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)

def rel(space, subpath, shortname, relation, rt="content"):
    """A real relationship edge. `attributes.relation` carries the vocabulary —
    verified to survive create, read and export verbatim (PLAN.md §4)."""
    return {
        "related_to": {"space_name": space, "subpath": subpath,
                        "shortname": shortname, "resource_type": rt},
        "attributes": {"relation": relation},
    }

def meta(kind, pack, folder, sn, body_ext="json", schema=None, extra=None,
         tags=None, displayname=None, description=None, relationships=None):
    """One entry meta. space_name / subpath / resource_type are stripped on
    export and re-injected from the path on import, so they are never authored
    (PLAN.md §1)."""
    m = {
        "uuid": uid(pack, folder, sn),
        "shortname": sn,
        "is_active": True,
        "tags": tags or [],
        "created_at": WHEN,
        "updated_at": WHEN,
        "owner_shortname": OWNER,
    }
    if displayname:
        m["displayname"] = {"en": displayname}
    if description:
        m["description"] = {"en": description}
    if relationships:
        m["relationships"] = relationships
    # The ContentType enum's wire value, NOT the file extension — they differ
    # for markdown, and `"content_type": "md"` is rejected at import with
    # `unknown ContentType value: md`.
    content_type = {"md": "markdown", "json": "json"}.get(body_ext, body_ext)
    payload = {
        "content_type": content_type,
        "body": f"{sn}.{body_ext}",
    }
    if schema:
        payload["schema_shortname"] = schema
    m["payload"] = payload
    if extra:
        m.update(extra)
    return m

def entry(root, pack, folder, sn, body, **kw):
    base = f"{root}/{pack}/{folder}"
    write_json(f"{base}/.dm/{sn}/meta.{kw.pop('rt', 'content')}.json",
               meta("content", pack, folder, sn, **kw))
    write_json(f"{base}/{sn}.json", body)

# ── per-pack writers ──────────────────────────────────────────────────────────

def gen_org(root, mult):
    for r in S.REGIONS:
        sites = [s for s in S.SITES if s["region"] == r["code"]]
        entry(root, "org", "regions", r["sn"],
              {"code": r["code"], "name": r["name"], "hq_city": r["hq"],
               "site_count": len(sites) * mult},
              schema="region", displayname=r["name"],
              description=f"{r['name']} region, headquartered in {r['hq']}.")
    for s in each(S.SITES, mult, "site"):
        entry(root, "org", "sites", s["sn"],
              {"name": s["name"], "region": s["region"], "city": s["city"],
               "kind": s["kind"], "status": s["status"],
               "commissioned_on": s["on"], "latitude": s["lat"],
               "longitude": s["lon"], "grid_hours_per_day": s["grid"]},
              schema="site", displayname=s["name"],
              tags=[s["region"], s["kind"], s["status"]],
              relationships=[rel("org", "regions", s["region"], "located_in")])

def gen_catalogue(root, mult):
    for p in S.PRODUCTS:
        entry(root, "catalogue", "products", p["sn"],
              {"name": p["name"], "family": p["family"], "status": p["status"],
               "launched_on": p["on"], "summary": p["summary"]},
              schema="product", displayname=p["name"],
              description=p["summary"], tags=[p["family"], p["status"]])
    for t in S.TARIFFS:
        entry(root, "catalogue", "tariffs", t["sn"],
              {"name": t["name"], "product": t["product"],
               "monthly_iqd": t["iqd"], "data_gb": t["gb"],
               "voice_minutes": t["mins"], "status": t["status"]},
              schema="tariff", displayname=t["name"], tags=[t["status"]],
              relationships=[rel("catalogue", "products", t["product"],
                                 "about_product")])

def gen_assets(root, mult):
    site_region = {s["sn"]: s["region"] for s in S.SITES}
    for e in each(S.EQUIPMENT, mult, "equipment"):
        body = {"name": e["name"], "kind": e["kind"], "site": e["site"],
                "region": site_region[e["site"]], "status": e["status"],
                "installed_on": e["on"], "last_service_on": e["serviced"]}
        if e["kva"]:
            body["rated_kva"] = e["kva"]
        entry(root, "assets", "equipment", e["sn"], body,
              schema="equipment", displayname=e["name"],
              tags=[e["kind"], e["status"], site_region[e["site"]]],
              relationships=[rel("org", "sites", e["site"], "installed_at")])
    for m in S.MAINTENANCE:
        entry(root, "assets", "maintenance", m["sn"],
              {"summary": m["summary"], "site": m["site"],
               "region": site_region[m["site"]], "equipment": m["equipment"],
               "kind": m["kind"], "performed_on": m["on"],
               "technician": m["tech"], "outcome": m["outcome"],
               "notes": m["notes"]},
              schema="maintenance_visit", displayname=m["summary"],
              tags=[m["kind"], m["outcome"]],
              relationships=[rel("org", "sites", m["site"], "located_in")]
                           + [rel("assets", "equipment", q, "affects")
                              for q in m["equipment"]])

def gen_kb(root, mult):
    # Markdown entries, not JSON — so no schema. Metadata rides as `tags`,
    # which `@tags:coverage` can filter.
    for a in S.ARTICLES:
        base = f"{root}/kb/articles"
        write_json(f"{base}/.dm/{a['sn']}/meta.content.json",
                   meta("content", "kb", "articles", a["sn"], body_ext="md",
                        displayname=a["title"], tags=a["tags"]))
        write_text(f"{base}/{a['sn']}.md", a["body"])

def gen_servicedesk(root, mult):
    for c in each(S.CASES, mult, "case"):
        body = {"title": c["title"], "category": c["category"],
                "severity": c["severity"], "region": c["region"],
                "site": c["site"], "customer_msisdn": c["msisdn"],
                "reported_on": c["on"]}
        for key in ("product", "equipment", "article"):
            if c.get(key):
                body[key] = c[key]
        rels = [rel("org", "sites", c["site"], "served_by")]
        if c.get("product"):
            rels.append(rel("catalogue", "products", c["product"], "about_product"))
        if c.get("equipment"):
            rels.append(rel("assets", "equipment", c["equipment"], "affects"))
        if c.get("article"):
            rels.append(rel("kb", "articles", c["article"], "uses_article"))
        # A ticket keeps its ticket fields on the meta — they are stripped from
        # NON-ticket metas only (PLAN.md §1). Every case starts `open`; the
        # walkthrough drives the transitions so the history is real.
        extra = {
            "workflow_shortname": "servicedesk_case",
            "state": "open",
            "is_open": True,
            # reporter must be an OBJECT; a bare string is silently discarded,
            # and distributor/governorate/channel_address are never parsed
            # (PLAN.md "Ticket reporter drops three of its seven fields").
            "reporter": {"type": "retail", "name": c["reporter"],
                         "channel": "call_center", "msisdn": c["msisdn"]},
        }
        base = f"{root}/servicedesk/cases"
        write_json(f"{base}/.dm/{c['sn']}/meta.ticket.json",
                   meta("ticket", "servicedesk", "cases", c["sn"],
                        schema="case", displayname=c["title"],
                        tags=[c["category"], c["severity"], c["region"]],
                        relationships=rels, extra=extra))
        write_json(f"{base}/{c['sn']}.json", body)
        # Comments are ATTACHMENTS — attachments.comment/meta.{sn}.json, flat
        # and prefixed, not .dm/{sn}/ (PLAN.md per-type table).
        for i, (author, text) in enumerate(c["comments"], start=1):
            csn = f"c{i:02d}"
            adir = f"{base}/.dm/{c['sn']}/attachments.comment"
            write_json(f"{adir}/meta.{csn}.json", {
                "uuid": uid("servicedesk", c["sn"], "comment", csn),
                "shortname": csn,
                "is_active": True,
                "tags": [],
                "created_at": WHEN,
                "updated_at": WHEN,
                "owner_shortname": author,
                "payload": {"content_type": "comment", "body": f"{csn}.json"},
            })
            write_json(f"{adir}/{csn}.json", {"body": text, "state": "commented"})

def gen_approvals(root, mult):
    for r in each(S.REQUESTS, mult, "request"):
        base = f"{root}/approvals/requests"
        write_json(f"{base}/.dm/{r['sn']}/meta.ticket.json",
                   meta("ticket", "approvals", "requests", r["sn"],
                        schema="access_request", displayname=r["summary"],
                        tags=[r["reason"], r["region"]],
                        relationships=[rel("org", "sites", r["site"], "located_in")],
                        extra={"workflow_shortname": "approvals_site_access",
                               "state": "submitted", "is_open": True,
                               "reporter": {"type": "staff", "name": r["by"],
                                            "channel": "staff_portal"}}))
        write_json(f"{base}/{r['sn']}.json",
                   {"summary": r["summary"], "site": r["site"],
                    "region": r["region"], "requested_by": r["by"],
                    "requested_for": r["on"], "reason": r["reason"],
                    "escort_required": r["escort"]})

def gen_datamart(root, mult):
    site_region = {s["sn"]: s["region"] for s in S.SITES}
    rows = list(each(S.KPI_ROWS, mult, "kpi"))
    for k in rows:
        sn = f"kpi_{k['site']}_{k['period']}"
        entry(root, "datamart", "kpis", sn,
              {"site": k["site"], "region": site_region[k["site"]],
               "period": k["period"], "availability_pct": k["avail"],
               "dropped_call_pct": k["drops"], "data_volume_tb": k["tb"],
               "outage_minutes": k["outage"]},
              schema="kpi_row",
              displayname=f"{k['site']} {k['period'].replace('_', '-')}",
              tags=[site_region[k["site"]], k["period"]],
              relationships=[rel("org", "sites", k["site"], "located_in")])
    # The dataset entry, plus the SAME numbers as a real downloadable CSV
    # riding as a data_asset attachment. Dual-shipping is deliberate: dmart
    # serves the blob but runs no SQL inside it (PLAN.md finding 7), so the
    # queryable copy is the /kpis entries above.
    for d in S.DATASETS:
        cols = ["site", "region", "period", "availability_pct",
                "dropped_call_pct", "data_volume_tb", "outage_minutes"]
        period_rows = [k for k in rows if k["period"] == d["period"]]
        csv_lines = [",".join(cols)]
        for k in sorted(period_rows, key=lambda r: r["site"]):
            csv_lines.append(",".join(str(x) for x in [
                k["site"], site_region[k["site"]], k["period"], k["avail"],
                k["drops"], k["tb"], k["outage"]]))
        csv = "\n".join(csv_lines) + "\n"
        base = f"{root}/datamart/datasets"
        write_json(f"{base}/.dm/{d['sn']}/meta.content.json",
                   meta("content", "datamart", "datasets", d["sn"],
                        schema="dataset", displayname=d["title"],
                        description=d["note"], tags=[d["period"], d["fmt"]]))
        write_json(f"{base}/{d['sn']}.json",
                   {"title": d["title"], "period": d["period"],
                    "format": d["fmt"], "row_count": len(period_rows),
                    "columns": cols, "note": d["note"]})
        # A data_asset's bytes are named after the ATTACHMENT shortname, not the
        # source filename, and the meta carries a sha256 of them — both pinned
        # by the round trip in PLAN.md's per-type table.
        asn = d["sn"]
        adir = f"{base}/.dm/{d['sn']}/attachments.data_asset"
        write_json(f"{adir}/meta.{asn}.json", {
            "uuid": uid("datamart", d["sn"], "data_asset", asn),
            "shortname": asn,
            "is_active": True,
            "tags": [d["fmt"]],
            "created_at": WHEN,
            "updated_at": WHEN,
            "owner_shortname": OWNER,
            "payload": {
                "content_type": d["fmt"],
                "checksum": hashlib.sha256(csv.encode()).hexdigest(),
                "body": f"{asn}.{d['fmt']}",
            },
        })
        write_text(f"{adir}/{asn}.{d['fmt']}", csv)

def gen_comms(root, mult):
    for n in S.NOTICES:
        body = {"title": n["title"], "channel": n["channel"],
                "audience": n["audience"], "region": n["region"],
                "site": n["site"], "send_on": n["on"], "status": n["status"]}
        rels = [rel("org", "sites", n["site"], "located_in")]
        if n.get("article"):
            body["article"] = n["article"]
            rels.append(rel("kb", "articles", n["article"], "uses_article"))
        entry(root, "comms", "notices", n["sn"], body,
              schema="notice", displayname=n["title"],
              tags=[n["channel"], n["status"], n["region"]],
              relationships=rels)

# ── scale ─────────────────────────────────────────────────────────────────────

def each(items, mult, kind):
    """Yield items `mult` times. The first pass is the authored set verbatim, so
    `small` and the first slice of `large` are identical and the anchors
    (erb_0142, bsr_0031, krb_0007) mean the same thing at every scale. Later
    passes get a `_gNN` suffix and are padding, not story."""
    for item in items:
        yield item
    for g in range(2, mult + 1):
        for item in items:
            clone = dict(item)
            clone["sn"] = f"{item['sn']}_g{g:02d}"
            if kind == "site":
                clone["name"] = f"{item['name']} #{g}"
            elif kind == "case":
                clone["title"] = f"{item['title']} (#{g})"
                clone["comments"] = []
            elif kind == "kpi":
                # KPI rows are keyed by site+period, so a clone must point at a
                # cloned site or it collides with the original's shortname.
                clone["site"] = f"{item['site']}_g{g:02d}"
            yield clone

SCALE_MULT = {"small": 1, "medium": 10, "large": 250}

WRITERS = {
    "org": gen_org, "catalogue": gen_catalogue, "assets": gen_assets,
    "kb": gen_kb, "servicedesk": gen_servicedesk, "approvals": gen_approvals,
    "datamart": gen_datamart, "comms": gen_comms,
}

def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--scale", default="small", choices=sorted(SCALE_MULT))
    ap.add_argument("--packs", help="comma-separated; default all")
    args = ap.parse_args()

    repo_packs = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    root = f"{repo_packs}/datasets/shanidar/{args.scale}"
    mult = SCALE_MULT[args.scale]
    names = (args.packs.split(",") if args.packs else sorted(WRITERS))

    for name in names:
        if name not in WRITERS:
            raise SystemExit(f"no dataset writer for pack '{name}'")
        shutil.rmtree(f"{root}/{name}", ignore_errors=True)
        WRITERS[name](root, mult)
        files = sum(len(f) for _, _, f in os.walk(f"{root}/{name}"))
        print(f"  {name}: {files} file(s)")

    # Personas are scale-independent and live beside the scales, because
    # install.sh creates them over the API: a user needs a password, demo
    # passwords come from DMART_PACKS_DEMO_PASSWORD at install time, and
    # nothing of the sort is ever committed.
    personas_path = f"{repo_packs}/datasets/shanidar/personas.json"
    write_json(personas_path, {
        "note": "Fictional staff of the fictional Shanidar Telecom. Created by "
                "install.sh over the API; passwords come from "
                "DMART_PACKS_DEMO_PASSWORD and are never stored here.",
        "personas": [
            {
                "shortname": p["sn"],
                "displayname": p["name"],
                # @example.com is reserved for documentation (RFC 2606), so
                # these can never reach a real mailbox.
                "email": f"{p['sn']}@example.com",
                # Synthetic MSISDN pattern, not assignable on a live network.
                "msisdn": p["msisdn"],
                "roles": p["roles"],
                "groups": p["groups"],
            }
            for p in S.PERSONAS
        ],
    })
    print(f"  personas: {len(S.PERSONAS)} -> {os.path.relpath(personas_path, repo_packs)}")

    stray = []
    for dirpath, _, files in os.walk(root):
        if "history.jsonl" in files:
            stray.append(os.path.join(dirpath, "history.jsonl"))
    if stray:
        raise SystemExit("generated a history.jsonl, which packs must not ship: "
                         + ", ".join(stray))
    print(f"  -> {root} (scale={args.scale}, x{mult})")

if __name__ == "__main__":
    main()
