#!/usr/bin/env python3
"""Fetch `updated_at` for every entry in a space, for the update planner.

Reads over the HTTP API rather than the database directly, so it works against
a remote instance and needs no driver-specific code. One query per space with a
high limit; `retrieve_json_payload` is off because only the timestamp matters
and the payloads would dwarf the response.
"""
import argparse, json, sys, urllib.request, urllib.error

def post(url, token, body):
    req = urllib.request.Request(
        url, data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json",
                 "Authorization": f"Bearer {token}"},
        method="POST")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        return json.loads(e.read().decode() or "{}")

def get(url, token):
    req = urllib.request.Request(
        url, headers={"Authorization": f"Bearer {token}"}, method="GET")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.load(r)
    except Exception:
        return None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", required=True)
    ap.add_argument("--token", required=True)
    ap.add_argument("--space", required=True)
    ap.add_argument("--limit", type=int, default=20000)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    rows = []
    # Subpath "/" with no exact flag walks the whole space.
    d = post(f"{args.url}/managed/query", args.token, {
        "type": "search", "space_name": args.space, "subpath": "/",
        "search": "", "limit": args.limit, "retrieve_json_payload": False,
    })
    if d.get("status") != "success":
        err = (d.get("error") or {}).get("message")
        # A space that does not exist yet is a FRESH install, not a failure.
        print(f"db_state: {args.space}: {err}", file=sys.stderr)
        json.dump([], open(args.out, "w"))
        return
    for r in d.get("records", []):
        a = r.get("attributes") or {}
        sub = r.get("subpath") or "/"
        if not sub.startswith("/"):
            sub = "/" + sub
        rows.append([args.space, sub, r.get("shortname"), a.get("updated_at")])
    # The space row itself is NOT returned by a subpath search, and the
    # `spaces` query type refuses anything but space_name="management" — it
    # lists every space rather than describing one. Read it as an entry
    # instead, which returns the row directly (flat, not wrapped in `records`).
    sp = get(f"{args.url}/managed/entry/space/{args.space}/{args.space}", args.token)
    if sp and sp.get("shortname") == args.space:
        rows.append([args.space, "/", args.space,
                     sp.get("updated_at") or (sp.get("attributes") or {}).get("updated_at")])
    json.dump(rows, open(args.out, "w"))
    print(f"db_state: {args.space}: {len(rows)} row(s)", file=sys.stderr)

if __name__ == "__main__":
    main()
