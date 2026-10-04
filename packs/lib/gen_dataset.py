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
import argparse, hashlib, json, os, re, shutil, sys, uuid

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

def write_jsonl(path, rows):
    """One JSON object per line, which is what Pass 5 reads."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        for r in rows:
            json.dump(r, f, ensure_ascii=False)
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
    region_of = lambda sn: site_region[base_sn(sn)]
    for e in each(S.EQUIPMENT, mult, "equipment"):
        body = {"name": e["name"], "kind": e["kind"], "site": e["site"],
                "region": region_of(e["site"]), "status": e["status"],
                "installed_on": e["on"], "last_service_on": e["serviced"]}
        if e["kva"]:
            body["rated_kva"] = e["kva"]
        entry(root, "assets", "equipment", e["sn"], body,
              schema="equipment", displayname=e["name"],
              tags=[e["kind"], e["status"], region_of(e["site"])],
              relationships=[rel("org", "sites", e["site"], "installed_at")])
    for m in S.MAINTENANCE:
        entry(root, "assets", "maintenance", m["sn"],
              {"summary": m["summary"], "site": m["site"],
               "region": region_of(m["site"]), "equipment": m["equipment"],
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

def gen_servicedesk_public(root, mult):
    """The two customer-owned cases and the one queued intake case.

    Split out from gen_servicedesk because these differ in the one way that
    matters: who OWNS them. A customer-raised case is owned by the customer, so
    their `own`-scoped query finds it; an intake case is owned by `anonymous`,
    because that is what /public/submit does and pretending otherwise would
    make the demo lie about finding 5."""
    site_region = {x["sn"]: x["region"] for x in S.SITES}
    base = f"{root}/servicedesk/cases"
    for c in S.CUSTOMER_CASES:
        body = {"title": c["title"], "category": c["category"],
                "severity": c["severity"], "region": c["region"],
                "site": c["site"], "product": c["product"],
                "customer_msisdn": c["msisdn"], "reported_on": c["on"]}
        rels = [rel("org", "sites", c["site"], "served_by"),
                rel("catalogue", "products", c["product"], "about_product")]
        m = meta("ticket", "servicedesk", "cases", c["sn"],
                 schema="case", displayname=c["title"],
                 tags=[c["category"], c["severity"], c["region"], "self_service"],
                 relationships=rels,
                 extra={"workflow_shortname": "servicedesk_case",
                        "state": "open", "is_open": True,
                        "reporter": {"type": "retail", "name": c["reporter"],
                                     "channel": "self_service",
                                     "msisdn": c["msisdn"]}})
        # The whole point: owner_shortname is the CUSTOMER.
        m["owner_shortname"] = c["owner"]
        write_json(f"{base}/.dm/{c['sn']}/meta.ticket.json", m)
        write_json(f"{base}/{c['sn']}.json", body)

    ibase = f"{root}/servicedesk/intake"
    for c in S.INTAKE_CASES:
        m = meta("ticket", "servicedesk", "intake", c["sn"],
                 schema="intake_case", displayname=c["title"],
                 tags=[c["category"], "intake"],
                 extra={"workflow_shortname": "servicedesk_case",
                        "state": "open", "is_open": True,
                        "reporter": {"type": "retail", "name": c["contact_name"],
                                     "channel": "public_form",
                                     "msisdn": c["msisdn"]}})
        # Owned by `anonymous` — the user AdminBootstrap always creates, so the
        # owner_shortname foreign key resolves on any instance.
        m["owner_shortname"] = "anonymous"
        write_json(f"{ibase}/.dm/{c['sn']}/meta.ticket.json", m)
        write_json(f"{ibase}/{c['sn']}.json", {
            "title": c["title"], "category": c["category"],
            "description": c["description"], "contact_msisdn": c["msisdn"],
            "contact_name": c["contact_name"], "city": c["city"]})

# The workflow's own transition table, mirrored here so the authored history
# cannot drift from the state machine. gen_workflows.py owns the real thing;
# this is the subset the archive drives, and the generator fails if an authored
# event names a transition the workflow does not have.
CASE_TRANSITIONS = {
    ("open", "take"):           ("in_progress", None),
    ("open", "escalate"):       ("escalated", None),
    ("in_progress", "escalate"): ("escalated", None),
    ("in_progress", "resolve"): ("resolved", True),
    ("escalated", "resolve"):   ("resolved", True),
    ("escalated", "return"):    ("in_progress", None),
}

def history_line(uuid_key, when, actor, diff):
    """One history.jsonl row, in the shape the EXPORTER writes — which is also
    the shape the importer now restores verbatim (uuid and timestamp included).

    The timestamp format is round-trippable ("o"-style, 7 fractional digits):
    DateTime.TryParse with RoundtripKind reads it back, and the column is
    `timestamp without time zone`, so no offset is written."""
    return {
        "uuid": uid("history", uuid_key),
        "shortname": "history",
        "owner_shortname": actor,
        "timestamp": when,
        "request_headers": {},
        "diff": diff,
    }

def stamp(day, hour, minute):
    """A story date as a local-naive timestamp. Fixed clock times so the output
    is byte-identical between runs."""
    return f"{day}T{hour:02d}:{minute:02d}:00.0000000"

def gen_servicedesk_archive(root, mult):
    """The twelve-month archive: closed cases shipped WITH their history.

    Each case's final state is DERIVED from its last event rather than written
    twice, so the meta and the history cannot disagree."""
    import datetime
    site_region = {x["sn"]: x["region"] for x in S.SITES}
    base = f"{root}/servicedesk/cases"
    for c in S.ARCHIVE_CASES:
        opened = datetime.date.fromisoformat(c["opened"])
        state, is_open, reason = "open", True, None
        rows = []
        for i, ev in enumerate(c["events"]):
            offset, actor, action = ev[0], ev[1], ev[2]
            new_reason = ev[3] if len(ev) > 3 else None
            key = (state, action)
            if key not in CASE_TRANSITIONS:
                raise SystemExit(
                    f"{c['sn']}: '{action}' is not a transition from '{state}' "
                    "— the archive would not match servicedesk_case")
            target, closes = CASE_TRANSITIONS[key]
            if closes and not new_reason:
                raise SystemExit(
                    f"{c['sn']}: '{action}' closes the ticket and needs a "
                    "resolution reason, exactly as resolution_required demands")
            if new_reason and new_reason not in dict(S.RESOLUTION_CODES):
                raise SystemExit(
                    f"{c['sn']}: '{new_reason}' is not in RESOLUTION_CODES")
            diff = {"state": {"old": state, "new": target}}
            if closes:
                diff["is_open"] = {"old": True, "new": False}
                diff["resolution_reason"] = {"old": reason, "new": new_reason}
            when = stamp((opened + datetime.timedelta(days=offset)).isoformat(),
                         9 + (i % 7), (i * 17) % 60)
            rows.append(history_line(f"{c['sn']}/{i}", when, actor, diff))
            state = target
            if closes:
                is_open, reason = False, new_reason

        body = {"title": c["title"], "category": c["category"],
                "severity": c["severity"], "region": c["region"],
                "site": c["site"], "customer_msisdn": c["msisdn"],
                "reported_on": c["opened"]}
        if c.get("product"):
            body["product"] = c["product"]
        if c.get("equipment"):
            body["equipment"] = c["equipment"]
        if reason:
            body["resolution_code"] = reason
        rels = [rel("org", "sites", c["site"], "served_by")]
        if c.get("product"):
            rels.append(rel("catalogue", "products", c["product"], "about_product"))
        if c.get("equipment"):
            rels.append(rel("assets", "equipment", c["equipment"], "affects"))
        extra = {
            "workflow_shortname": "servicedesk_case",
            "state": state, "is_open": is_open,
            "reporter": {"type": "retail", "name": c["reporter"],
                         "channel": "call_center", "msisdn": c["msisdn"]},
        }
        if reason:
            extra["resolution_reason"] = reason
        m = meta("ticket", "servicedesk", "cases", c["sn"],
                 schema="case", displayname=c["title"],
                 tags=[c["category"], c["severity"], c["region"], "archive"],
                 relationships=rels, extra=extra)
        # Dated by the story, not by the generator's fixed WHEN: an archived
        # case created after its own history would read as nonsense.
        m["created_at"] = rows[0]["timestamp"]
        m["updated_at"] = rows[-1]["timestamp"]
        write_json(f"{base}/.dm/{c['sn']}/meta.ticket.json", m)
        write_json(f"{base}/{c['sn']}.json", body)
        write_jsonl(f"{base}/.dm/{c['sn']}/history.jsonl", rows)

def gen_equipment_history(root, mult):
    """History on a non-ticket entry — history is not a ticket feature."""
    base = f"{root}/assets/equipment"
    for sn, events in sorted(S.EQUIPMENT_HISTORY.items()):
        meta_path = f"{base}/.dm/{sn}/meta.content.json"
        if not os.path.isfile(meta_path):
            raise SystemExit(f"EQUIPMENT_HISTORY names {sn}, which has no entry")
        rows = [history_line(f"{sn}/{i}", stamp(day, 10 + i, (i * 23) % 60), actor, diff)
                for i, (day, actor, diff) in enumerate(events)]
        write_jsonl(f"{base}/.dm/{sn}/history.jsonl", rows)
        # The entry's updated_at should not predate its own last revision.
        m = json.load(open(meta_path))
        m["updated_at"] = rows[-1]["timestamp"]
        write_json(meta_path, m)

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
    region_of = lambda sn: site_region[base_sn(sn)]
    rows = list(each(S.KPI_ROWS, mult, "kpi"))
    for k in rows:
        sn = f"kpi_{k['site']}_{k['period']}"
        entry(root, "datamart", "kpis", sn,
              {"site": k["site"], "region": region_of(k["site"]),
               "period": k["period"], "availability_pct": k["avail"],
               "dropped_call_pct": k["drops"], "data_volume_tb": k["tb"],
               "outage_minutes": k["outage"]},
              schema="kpi_row",
              displayname=f"{k['site']} {k['period'].replace('_', '-')}",
              tags=[region_of(k["site"]), k["period"]],
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
                k["site"], region_of(k["site"]), k["period"], k["avail"],
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

# `_gNN` marks a generated clone. Stripping it recovers the authored shortname,
# which is how a clone still resolves its region: the region tables are keyed by
# the authored site names and are not themselves cloned.
GEN_SUFFIX = re.compile(r"_g\d{2}$")

def base_sn(shortname):
    return GEN_SUFFIX.sub("", shortname)

def each(items, mult, kind):
    """Yield items `mult` times. The first pass is the authored set VERBATIM, so
    `small` and the first slice of `large` are identical and the three anchors
    (erb_0142, bsr_0031, krb_0007) mean the same thing at every scale. Later
    passes carry a `_gNN` suffix and are padding, not story.

    A clone's links are suffixed too, so generation N points at generation N's
    own site rather than all of them piling onto the authored one. The graph
    stays internally consistent at every scale instead of growing one
    absurdly-busy site."""
    for item in items:
        yield item
    for g in range(2, mult + 1):
        suffix = f"_g{g:02d}"
        for item in items:
            clone = dict(item)
            # KPI rows derive their shortname from site+period and carry no
            # `sn` of their own — suffixing the site is what makes them unique.
            if "sn" in item:
                clone["sn"] = f"{item['sn']}{suffix}"
            if item.get("site"):
                clone["site"] = f"{item['site']}{suffix}"
            if item.get("equipment"):
                eq = item["equipment"]
                clone["equipment"] = ([f"{q}{suffix}" for q in eq]
                                      if isinstance(eq, list) else f"{eq}{suffix}")
            if kind == "site":
                clone["name"] = f"{item['name']} #{g}"
            elif kind == "case":
                clone["title"] = f"{item['title']} (#{g})"
                # Comments are owned by personas, which are NOT cloned; a clone
                # carrying them would just repeat the same two notes 250 times.
                clone["comments"] = []
            yield clone

SCALE_MULT = {"small": 1, "medium": 10, "large": 250}

WRITERS = {
    "org": gen_org, "catalogue": gen_catalogue,
    "assets": lambda root, mult: (gen_assets(root, mult),
                                   gen_equipment_history(root, mult)),
    "kb": gen_kb,
    "servicedesk": lambda root, mult: (gen_servicedesk(root, mult),
                                        gen_servicedesk_public(root, mult),
                                        gen_servicedesk_archive(root, mult)),
    "approvals": gen_approvals,
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

    # Packs DO ship history now (#329 made the importer preserve an authored
    # uuid and timestamp, and dedupe on the uuid). What still has to hold is
    # that every line is readable and carries both fields — a line missing them
    # silently falls back to "now", which would quietly undo the whole point.
    bad = []
    for dirpath, _, files in os.walk(root):
        if "history.jsonl" not in files:
            continue
        hp = os.path.join(dirpath, "history.jsonl")
        for n, line in enumerate(open(hp), start=1):
            if not line.strip():
                continue
            try:
                row = json.loads(line)
            except Exception as exc:
                bad.append(f"{hp}:{n} unreadable ({exc})")
                continue
            for field in ("uuid", "timestamp", "owner_shortname"):
                if not row.get(field):
                    bad.append(f"{hp}:{n} missing {field}")
    if bad:
        raise SystemExit("authored history is malformed:\n  " + "\n  ".join(bad))
    print(f"  -> {root} (scale={args.scale}, x{mult})")

if __name__ == "__main__":
    main()
