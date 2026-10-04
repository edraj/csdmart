#!/usr/bin/env python3
"""Maintain a region-month rollup whenever a KPI row changes.

A hook on create/update/delete of a `kpi_row` in datamart/kpis. It recomputes
the whole region-month from the rows that currently exist and writes
`rollup_<region>_<period>` into datamart/rollups.

## Recomputed, never incremented

The obvious implementation adds the new row's numbers to the existing rollup.
This one re-reads every row in the region-month and recomputes from scratch,
because an incremental rollup is wrong in three ordinary situations: an `update`
would double-count unless it knew the old value, a `delete` cannot be
subtracted from a figure it was never added to, and a hook that fires twice
(dmart dispatches after the response, so a retry is possible) would count
twice. Recomputing is idempotent, which matters far more here than saving a
query.

It also means the rollup is self-checking: it records the shortnames it was
computed from, so a reader can verify the arithmetic instead of trusting it.

## Why REST and not the save_entry callback

Same reasoning as shanidar_case_assign: `save_entry` writes straight through
EntryRepository, so the rollup would skip its own schema validation, and the
history would name whoever happened to edit a KPI row rather than the
automation. A rollup that claims a human wrote it is worse than no rollup.

Credentials come from `credentials.json` beside this file, mode 0600, written
by install.sh. The account it uses can read and write datamart/kpis and
datamart/rollups, and nothing else anywhere.
"""
import datetime
import json
import os
import sys
import urllib.error
import urllib.request

__version__ = "1.0.0"

HERE = os.path.dirname(os.path.abspath(__file__))
HOST_CALLBACKS = False
_creds = None
_next_id = 0


def callback(op, args):
    global _next_id
    _next_id += 1
    print(json.dumps({"type": "callback", "id": _next_id, "op": op, "args": args}),
          flush=True)
    line = sys.stdin.readline()
    if not line:
        return None
    try:
        reply = json.loads(line)
    except Exception:
        return None
    return reply.get("result") if reply.get("ok") else None


def log(msg, level=2):
    print(f"[shanidar_kpi_rollup] {msg}", file=sys.stderr)
    if HOST_CALLBACKS:
        callback("log", {"level": level, "message": msg})


def creds():
    global _creds
    if _creds is not None:
        return _creds or None
    try:
        with open(os.path.join(HERE, "credentials.json")) as f:
            _creds = json.load(f)
    except Exception as exc:
        log(f"no usable credentials.json ({exc}) — not rolling up", level=4)
        _creds = {}
        return None
    return _creds


def api(method, path, token=None, body=None):
    c = creds()
    if not c:
        return None
    req = urllib.request.Request(
        c["url"].rstrip("/") + path,
        data=json.dumps(body).encode() if body is not None else None,
        headers={"Content-Type": "application/json",
                 **({"Authorization": "Bearer " + token} if token else {})},
        method=method)
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        try:
            return json.loads(e.read().decode())
        except Exception:
            return None
    except Exception as exc:
        log(f"{method} {path} failed: {exc}", level=4)
        return None


def login():
    c = creds()
    if not c:
        return None
    d = api("POST", "/user/login",
            body={"shortname": c["shortname"], "password": c["password"]})
    if not d or d.get("status") != "success":
        msg = (d or {}).get("error", {}).get("message", "no response")
        log(f"login as {c.get('shortname')} failed: {msg}", level=4)
        return None
    return d["records"][0]["attributes"]["access_token"]


def rows_for(token, region, period):
    """Every kpi_row in one region-month.

    Filtered on payload.body keys, which is the only filtering that works —
    the packs duplicate every link into payload.body precisely because
    `@relationships[]...` is silently dropped from the WHERE clause."""
    d = api("POST", "/managed/query", token, {
        "type": "search", "space_name": "datamart", "subpath": "kpis",
        "search": f"@payload.body.region:{region} @payload.body.period:{period}",
        "limit": 5000, "retrieve_json_payload": True})
    if not d or d.get("status") != "success":
        return None
    out = []
    for r in d.get("records", []):
        body = ((r.get("attributes") or {}).get("payload") or {}).get("body") or {}
        if isinstance(body, dict) and body.get("region") == region \
           and body.get("period") == period:
            out.append((r.get("shortname"), body))
    return out


def compute(region, period, rows, actor):
    """The rollup body. Averages are weighted by nothing — one row is one site
    for one month, so a plain mean over sites is what 'regional availability'
    means here. Rounded to the precision the source data carries, so the
    rollup does not imply more accuracy than it has."""
    n = len(rows)
    avail = sum(float(b.get("availability_pct") or 0) for _, b in rows) / n
    outage = sum(int(b.get("outage_minutes") or 0) for _, b in rows)
    volume = sum(float(b.get("data_volume_tb") or 0) for _, b in rows)
    return {
        "region": region,
        "period": period,
        "site_count": n,
        "availability_pct": round(avail, 2),
        "outage_minutes": outage,
        "data_volume_tb": round(volume, 2),
        "sources": sorted(sn for sn, _ in rows),
        "computed_by": actor,
        "computed_at": datetime.datetime.now().replace(microsecond=0).isoformat(),
    }


def write_rollup(token, actor, region, period, body):
    sn = f"rollup_{region}_{period}"
    record = {
        "resource_type": "content", "shortname": sn, "subpath": "rollups",
        "attributes": {
            "is_active": True,
            "displayname": {"en": f"{region} {period.replace('_', '-')}"},
            "tags": [region, period, "rollup"],
            "payload": {"content_type": "json",
                        "schema_shortname": "kpi_rollup", "body": body},
        }}
    # No upsert in RequestType (create/update/patch/update_acl/assign/delete/
    # move), so create first and fall back to update. Doing it in that order
    # means the common case — a rollup that already exists — costs one extra
    # call, which is cheaper than reading it first to decide.
    d = api("POST", "/managed/request", token,
            {"space_name": "datamart", "request_type": "create",
             "records": [record]})
    if d and d.get("status") == "success":
        return "created"
    d = api("POST", "/managed/request", token,
            {"space_name": "datamart", "request_type": "update",
             "records": [record]})
    if d and d.get("status") == "success":
        return "updated"
    detail = json.dumps((d or {}).get("error") or {})[:140]
    log(f"{sn}: refused: {detail}", level=4)
    return None


def handle_info(msg):
    global HOST_CALLBACKS
    HOST_CALLBACKS = bool((msg.get("host") or {}).get("callbacks"))
    return {"shortname": "shanidar_kpi_rollup", "version": __version__,
            "type": "hook"}


def handle_hook(event):
    if event.get("space_name") != "datamart":
        return {"status": "ok"}
    if (event.get("subpath") or "").strip("/") != "kpis":
        return {"status": "ok"}

    token = login()
    if not token:
        return {"status": "ok"}
    actor = (creds() or {}).get("shortname") or "unknown"
    shortname = event.get("shortname")

    # Which region-month changed? The event does not carry the payload, and on
    # a DELETE the row is already gone — so derive it from the shortname, which
    # gen_dataset builds as kpi_<site>_<YYYY>_<MM>. Falling back to a read
    # would work for create and update and fail for delete, which is the one
    # case most in need of a recompute.
    region = period = None
    rows = query_row(token, shortname)
    if rows:
        region, period = rows
    else:
        # The row is gone (a delete), so ask the rollups which one owned it.
        region, period = owning_rollup(token, shortname)
    if not region or not period:
        log(f"{shortname}: cannot tell which region-month changed", level=3)
        return {"status": "ok"}

    present = rows_for(token, region, period)
    if present is None:
        log(f"{region} {period}: could not read the rows", level=4)
        return {"status": "ok"}
    if not present:
        # Every row in the region-month is gone. Leaving a rollup describing
        # nothing would be worse than removing it.
        d = api("POST", "/managed/request", token, {
            "space_name": "datamart", "request_type": "delete", "records": [{
                "resource_type": "content",
                "shortname": f"rollup_{region}_{period}",
                "subpath": "rollups", "attributes": {}}]})
        state = "removed" if d and d.get("status") == "success" else "already absent"
        log(f"{region} {period}: no rows left, rollup {state}")
        return {"status": "ok"}

    what = write_rollup(token, actor, region, period,
                        compute(region, period, present, actor))
    if what:
        log(f"{region} {period}: rollup {what} from {len(present)} site(s)")
    return {"status": "ok"}


def query_row(token, shortname):
    """(region, period) read off the row itself, when it still exists."""
    d = api("POST", "/managed/query", token, {
        "type": "search", "space_name": "datamart", "subpath": "kpis",
        "search": f"@shortname:{shortname}", "limit": 2,
        "retrieve_json_payload": True})
    if not d or d.get("status") != "success":
        return None
    for r in d.get("records", []):
        body = ((r.get("attributes") or {}).get("payload") or {}).get("body") or {}
        if isinstance(body, dict) and body.get("region") and body.get("period"):
            return body["region"], body["period"]
    return None


def owning_rollup(token, shortname):
    """(region, period) of the rollup that LISTS this row in its sources.

    The delete path. The row is gone, so its region and period cannot be read
    from it — and the first version resolved them by querying org/sites, which
    the scoped service account cannot read. A pack's plugin may only reach its
    own pack, and widening that grant to make a convenience work would defeat
    the point of scoping it.

    So the answer comes from inside datamart instead: every rollup records the
    shortnames it was computed from, which makes it exactly the index needed
    here. The `sources` field earns its keep twice — a reader can check the
    arithmetic, and a deletion can find its way home."""
    d = api("POST", "/managed/query", token, {
        "type": "search", "space_name": "datamart", "subpath": "rollups",
        "search": "", "limit": 5000, "retrieve_json_payload": True})
    if not d or d.get("status") != "success":
        return None, None
    for r in d.get("records", []):
        body = ((r.get("attributes") or {}).get("payload") or {}).get("body") or {}
        if not isinstance(body, dict):
            continue
        if shortname in (body.get("sources") or []):
            return body.get("region"), body.get("period")
    return None, None


def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            break
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
            kind = msg.get("type", "")
            if kind == "info":
                out = handle_info(msg)
            elif kind == "hook":
                out = handle_hook(msg.get("event", {}))
            else:
                out = {"status": "error", "message": f"unknown type: {kind}"}
        except Exception as exc:
            out = {"status": "error", "message": str(exc)}
        print(json.dumps(out), flush=True)


if __name__ == "__main__":
    import signal
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    try:
        main()
    except KeyboardInterrupt:
        pass
