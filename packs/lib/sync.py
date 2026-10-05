#!/usr/bin/env python3
"""Plan an install-or-update for every selected pack, in one pass.

Reads each pack's receipt from dmart, reads the current `updated_at` of
everything the pack owns, and asks plan_update to decide what may change.
Writes out the three artefacts install.sh needs:

    import-list.txt     meta paths to import (adds + safe updates)
    deletes.json        rows to delete (dropped and operator-untouched)
    receipts/<pack>.json  the record to store after a successful install

Nothing here writes to dmart. Planning and applying are separate so that
`--dry-run` is the same code path minus the apply, rather than a second
implementation that can drift from the first.
"""
import argparse, json, os, sys, datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import plan_update as P
import db_state as D

RECEIPT_SPACE = "management"
RECEIPT_SUBPATH = "packs"


def fetch_receipt(url, token, pack):
    """The stored receipt for a pack, or None if it has never been installed.

    A 404 and a never-installed pack are the same thing here, so a missing
    entry is not an error."""
    # retrieve_json_payload=true is NOT optional here. Without it the entry
    # comes back with `payload.body` absent, every pack reads as a fresh
    # install, and the planner silently loses its baseline — the one failure
    # that would make an update overwrite the operator's edits.
    got = D.get(
        f"{url}/managed/entry/content/{RECEIPT_SPACE}/{RECEIPT_SUBPATH}/{pack}"
        "?retrieve_json_payload=true", token)
    if not got:
        return None
    payload = got.get("payload") or (got.get("attributes") or {}).get("payload") or {}
    body = payload.get("body")
    if isinstance(body, str):
        # An externalized body comes back as a filename; the receipt is small
        # and always inline, so this means something else wrote it.
        return None
    return body or None


def db_state_for(url, token, spaces):
    rows = {}
    for space in spaces:
        out = {}
        d = D.post(f"{url}/managed/query", token, {
            "type": "search", "space_name": space, "subpath": "/",
            "search": "", "limit": 20000, "retrieve_json_payload": False})
        if d.get("status") == "success":
            for r in d.get("records", []):
                sub = r.get("subpath") or "/"
                if not sub.startswith("/"):
                    sub = "/" + sub
                a = r.get("attributes") or {}
                rows[(space, sub, r.get("shortname"))] = a.get("updated_at")
        sp = D.get(f"{url}/managed/entry/space/{space}/{space}", token)
        if sp and sp.get("shortname") == space:
            rows[(space, "/", space)] = sp.get("updated_at") \
                or (sp.get("attributes") or {}).get("updated_at")
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", required=True)
    ap.add_argument("--token", required=True)
    ap.add_argument("--spaces", required=True, help="the built tree")
    ap.add_argument("--packs-root", required=True)
    ap.add_argument("--packs", required=True, help="space-separated pack names")
    ap.add_argument("--scale", default="small")
    ap.add_argument("--out", required=True, help="directory for the artefacts")
    ap.add_argument("--force", action="store_true",
                    help="proceed even when the installed version is newer")
    args = ap.parse_args()

    os.makedirs(f"{args.out}/receipts", exist_ok=True)
    names = [n for n in args.packs.split() if n]
    now = datetime.datetime.now().replace(microsecond=0).isoformat()

    import_list, deletes, summary = [], [], []
    exit_code = 0

    for name in names:
        manifest_path = os.path.join(args.packs_root, name, "pack.json")
        pack = json.load(open(manifest_path))
        if "version" not in pack:
            print(f"{name}: pack.json has no `version`", file=sys.stderr)
            return 2
        new_v = pack["version"]
        try:
            P.parse_version(new_v)
        except ValueError as exc:
            print(f"{name}: {exc}", file=sys.stderr)
            return 2

        receipt = fetch_receipt(args.url, args.token, name)
        old_v = (receipt or {}).get("version")
        if old_v:
            try:
                older = P.parse_version(new_v) < P.parse_version(old_v)
            except ValueError as exc:
                print(f"{name}: {exc}", file=sys.stderr)
                return 2
            if older and not args.force:
                print(f"{name}: installed {old_v} is NEWER than {new_v}. "
                      "Refusing to downgrade — reset the pack first, or pass "
                      "--force if you know what you are doing.", file=sys.stderr)
                exit_code = 2
                continue

        overlay = P.overlay_paths(os.path.join(args.packs_root, name))
        shipped = P.scan_tree(args.spaces, only_spaces={pack["space"]})
        shipped.update(P.scan_tree(args.spaces, only_spaces={RECEIPT_SPACE},
                                   restrict_to=overlay))
        db = db_state_for(args.url, args.token, [pack["space"], RECEIPT_SPACE])
        plan = P.build_plan(shipped, receipt, db)

        if old_v == new_v and not any(plan[k] for k in ("add", "update", "remove")):
            summary.append(f"{name}: already at {new_v}, nothing to do")
            continue

        refreshing = bool(receipt) and ((receipt.get("format") or 1)
                                        < P.RECEIPT_FORMAT)
        print(P.render(plan, name, old_v, new_v, refreshing=refreshing))
        chosen = [r["path"] for r in plan["add"] + plan["update"]]
        # An attachment rides with its parent. It has no independent identity to
        # plan against, but it IS a separate meta file, so an import driven by
        # --from-list has to name it or a case's comments and the datamart CSV
        # never land at all.
        atts = P.attachment_paths(args.spaces, only_spaces={pack["space"]})
        parents = set(chosen)
        chosen += [rel for rel, parent in sorted(atts.items()) if parent in parents]
        import_list += chosen
        deletes += plan["remove"]

        rec = P.receipt_from_tree(pack, args.scale, shipped, now)
        if receipt and receipt.get("installed_at"):
            rec["installed_at"] = receipt["installed_at"]
            rec["updated_at"] = now
        json.dump(rec, open(f"{args.out}/receipts/{name}.json", "w"), indent=2)

        counts = {k: len(v) for k, v in plan.items() if v}
        summary.append(f"{name}: {old_v or 'fresh'} -> {new_v}  " +
                       ", ".join(f"{k}={n}" for k, n in sorted(counts.items())))

    # De-duplicate while keeping order: two packs share the management space and
    # could both name the same overlay path only by mistake, but importing a
    # path twice in one run is wasted work either way.
    seen, uniq = set(), []
    for p in import_list:
        if p not in seen:
            seen.add(p)
            uniq.append(p)
    with open(f"{args.out}/import-list.txt", "w") as f:
        f.write("\n".join(uniq) + ("\n" if uniq else ""))
    json.dump(deletes, open(f"{args.out}/deletes.json", "w"), indent=2)

    print()
    for line in summary:
        print("  " + line)
    print(f"  -> {len(uniq)} path(s) to import, {len(deletes)} row(s) to delete")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
