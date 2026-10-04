#!/usr/bin/env python3
"""Assign a new case to the technician covering its site's region.

A hook on `create` of a ticket in servicedesk/cases or /intake. It reads the
case's own `region`, looks that up in the routing table at
servicedesk/routing/assignment, and writes `payload.body.assignee`.

## Why a payload field rather than `collaborators`

`collaborators.assignee` is the field that looks right, and it is a trap here.
It is only settable through `request_type: "assign"`, which also transfers
`owner_shortname` to the assignee (Api/Managed/RequestHandler.cs:1505-1530) —
and a customer's view of their own case depends on OWNING it, so routing a
technician would hide the case from the person who raised it. A plain `update`
carrying `collaborators` is accepted and silently ignored, which is how this
was found: the plugin reported success, nothing changed, and no history row
appeared.

`payload.body.assignee` routes the work without moving the ownership, and
`@payload.body.assignee:tech_north_erbil` filters — which `collaborators`
would not, being outside payload.

## Everything it reads is in its own pack, deliberately

The first version looked the region up by querying org/sites, and the service
account could not read it — a pack may only grant access to its own space, and
build.sh enforces that. Rather than widen the grant, the data moved:

  * the region is already ON the case. The packs duplicate every link into
    payload.body because relationship filtering does not work, and that
    duplication pays off here — no traversal needed.
  * who covers a region is routing POLICY, so it lives as data at
    servicedesk/routing/assignment, generated from the personas so it cannot
    name a technician who does not exist. An operator re-routes by editing an
    entry rather than an executable.

So the plugin needs no cross-pack grant at all, which is what makes it safe to
install something like this from a repo you do not control.

## Why this writes over REST instead of using the save_entry callback

dmart offers a `save_entry` callback that would be less code. The pack brief
assumed a plugin had no choice but REST; it does have a choice, and REST is
still the right one, for two reasons found in dmart's source rather than
guessed:

  1. `save_entry` goes straight to `EntryRepository.UpsertWithPriorAsync`
     (Plugins/Native/NativePluginCallbacks.cs), bypassing EntryService
     entirely — so no schema validation, no relationship integrity, no
     permission check and no folder content policy. This plugin writes a
     LINKED field, which is exactly what referential integrity protects.
  2. History would be attributed to the user who triggered the hook, not to
     the plugin: PluginInvocationContext.CurrentActor is the triggering
     request's actor, and EmitSaveEntry passes it to history.AppendAsync. The
     brief's requirement is that automation appears as its own actor, and
     save_entry gives the opposite — a customer's case would show the customer
     silently assigning their own ticket.

So this authenticates as a scoped service account and goes through the API like
any other client. The cost is a login and two round trips; the gain is that the
write is validated, permission-checked, and honestly attributed.

## Credentials

`credentials.json` beside this file, mode 0600, written by install.sh from
$DMART_PACKS_PLUGIN_PASSWORD. Nothing secret is committed, and the account it
logs in as can only update tickets in this one space.
"""
import json
import os
import sys
import urllib.error
import urllib.request

__version__ = "1.0.0"

HERE = os.path.dirname(os.path.abspath(__file__))
HOST_CALLBACKS = False
_creds = None


def log(msg, level=2):
    """stderr always — dmart forwards it — and through dmart's own log pipeline
    when the host answers callbacks, so it lands in the operator's sinks."""
    print(f"[shanidar_case_assign] {msg}", file=sys.stderr)
    if HOST_CALLBACKS:
        callback("log", {"level": level, "message": msg})


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


def creds():
    """Read once. A missing file is not a crash: the plugin degrades to doing
    nothing and says so, because a broken automation must not break the write
    that triggered it."""
    global _creds
    if _creds is not None:
        return _creds or None
    path = os.path.join(HERE, "credentials.json")
    try:
        with open(path) as f:
            _creds = json.load(f)
    except Exception as exc:
        log(f"no usable credentials.json ({exc}) — not assigning", level=4)
        _creds = {}
        return None
    return _creds


def api(method, path, token=None, body=None):
    c = creds()
    if not c:
        return None
    url = c["url"].rstrip("/") + path
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
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


def query(token, space, subpath, search, limit=50):
    d = api("POST", "/managed/query", token, {
        "type": "search", "space_name": space, "subpath": subpath,
        "search": search, "limit": limit, "retrieve_json_payload": True})
    if not d or d.get("status") != "success":
        return []
    return d.get("records", [])


def routing_table(token):
    """region -> assignee, from servicedesk/routing/assignment.

    In the plugin's own space, so its scoped service account can read it. An
    absent entry is not an error: the plugin then assigns nothing and says so,
    which is better than guessing an assignee."""
    for r in query(token, "servicedesk", "routing", "@shortname:assignment", limit=2):
        body = ((r.get("attributes") or {}).get("payload") or {}).get("body") or {}
        if isinstance(body, dict) and isinstance(body.get("by_region"), dict):
            return body["by_region"]
    return None


def handle_info(msg):
    global HOST_CALLBACKS
    HOST_CALLBACKS = bool((msg.get("host") or {}).get("callbacks"))
    return {"shortname": "shanidar_case_assign", "version": __version__,
            "type": "hook"}


def handle_hook(event):
    space = event.get("space_name")
    subpath = (event.get("subpath") or "").strip("/")
    shortname = event.get("shortname")
    if space != "servicedesk" or subpath not in ("cases", "intake"):
        return {"status": "ok"}

    token = login()
    if not token:
        return {"status": "ok"}      # degraded, already logged

    rows = query(token, space, subpath, f"@shortname:{shortname}", limit=2)
    if not rows:
        log(f"{shortname}: not readable by the service account", level=3)
        return {"status": "ok"}
    attrs = rows[0].get("attributes") or {}
    body = (attrs.get("payload") or {}).get("body") or {}

    if not isinstance(body, dict):
        log(f"{shortname}: payload body is not an object, cannot route it",
            level=3)
        return {"status": "ok"}

    # Never overwrite an assignee a human already set. The hook fires on
    # `create`, so this should not happen — but a create that already carries
    # one is a deliberate act and the automation has no business undoing it.
    if body.get("assignee"):
        log(f"{shortname}: already assigned to {body['assignee']}, leaving it")
        return {"status": "ok"}

    # The case carries its own region — no cross-space read needed.
    region = body.get("region") if isinstance(body, dict) else None
    if not region:
        log(f"{shortname}: no region on the case, cannot route it")
        return {"status": "ok"}
    table = routing_table(token)
    if table is None:
        log(f"{shortname}: no routing table at servicedesk/routing/assignment",
            level=3)
        return {"status": "ok"}
    tech = table.get(region)
    if not tech:
        # A real automation degrades. Central ships no technician in this
        # storyline, so an unassigned Baghdad case is the expected path.
        log(f"{shortname}: nobody covers {region}, leaving it unassigned")
        return {"status": "ok"}

    # The WHOLE body goes back with `assignee` added, rather than a fragment:
    # an update replaces payload rather than merging into it, so sending only
    # the one key would drop the rest of the case.
    merged = dict(body)
    merged["assignee"] = tech
    d = api("POST", "/managed/request", token, {
        "space_name": space, "request_type": "update", "records": [{
            "resource_type": "ticket", "shortname": shortname,
            "subpath": subpath,
            "attributes": {"payload": {
                "content_type": "json",
                "schema_shortname": (attrs.get("payload") or {}).get(
                    "schema_shortname") or "case",
                "body": merged}}}]})
    if d and d.get("status") == "success":
        log(f"{shortname}: {region} -> assigned {tech}")
    else:
        detail = json.dumps((d or {}).get("error") or {})[:120]
        log(f"{shortname}: assignment to {tech} refused: {detail}", level=4)
    return {"status": "ok"}


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
            # A hook that throws must not look like a dmart failure: the write
            # it fired on has already been answered to the client.
            out = {"status": "error", "message": str(exc)}
        print(json.dumps(out), flush=True)


if __name__ == "__main__":
    import signal
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    try:
        main()
    except KeyboardInterrupt:
        pass
