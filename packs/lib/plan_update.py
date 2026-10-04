#!/usr/bin/env python3
r"""Decide what installing a pack over an existing install should do.

The rule everything here serves: **an update never overwrites or deletes a row
the operator changed.** A pack is someone else's software landing in your
database; if you edited an entry it shipped, that edit wins and the update tells
you it skipped you rather than quietly reverting your work.

How a user edit is detected, with no hashing and no extra bookkeeping: dmart
sets `updated_at = TimeUtils.Now()` on every write through `EntryService`
(`Services/EntryService.cs:965`), while the IMPORTER preserves the `updated_at`
the archive carries (`ImportExportService.cs:2965`). A pack ships fixed
timestamps. So for any pack-shipped entry:

    db.updated_at == what the pack shipped   ->  untouched since install
    db.updated_at != what the pack shipped   ->  written through dmart since

That is dmart's own semantics doing the work, which is why it is trustworthy —
there is no second source of truth to drift.

Those are two different questions and they need two different signals:

    did the OPERATOR change it?   db.updated_at  vs  the shipped updated_at
    did the PACK change it?       a content hash vs  the hash in the receipt

The second cannot use the timestamp. The pack generators write a FIXED
`updated_at` so their output stays byte-identical between runs, so editing a
schema's content moves no timestamp at all — and a planner that asked the
timestamp would skip the change silently. That is not hypothetical: it is how
this was found, with a schema edit that never reached the database and a plugin
failing validation against the old copy.

So the receipt records both per entry. See `receipt_from_tree`.

Outputs a plan with six buckets:

    add        in the new version, not in the receipt           -> import
    update     pack changed it, operator did not                -> import -r
    conflict   pack changed it AND operator changed it          -> SKIP, report
    kept       pack did not change it, operator did             -> leave alone
    remove     dropped by the new version, operator untouched    -> delete
    orphan     dropped by the new version, operator changed it   -> leave, report
    unchanged  identical on both sides                           -> nothing
"""
import argparse, hashlib, json, os, sys, datetime

RECEIPT_SPACE = "management"
RECEIPT_SUBPATH = "packs"
RECEIPT_FORMAT = 2


# ── timestamps ────────────────────────────────────────────────────────────────

def norm_stamp(value):
    """One datetime from either spelling.

    A meta file writes `2026-10-01T00:00:00`; the database hands back
    `2026-10-01 00:00:00.0000000`. Comparing the strings says they differ when
    they do not, so both sides normalise here. Seven fractional digits is more
    than `fromisoformat` takes, hence the truncation to six."""
    if value is None:
        return None
    if isinstance(value, datetime.datetime):
        return value
    s = str(value).strip().replace(" ", "T")
    if s.endswith("Z"):
        s = s[:-1]
    if "+" in s[10:]:
        s = s[:10] + s[10:].split("+")[0]
    if "." in s:
        head, frac = s.split(".", 1)
        s = head + "." + frac[:6]
    try:
        return datetime.datetime.fromisoformat(s)
    except ValueError:
        return None


# ── reading a built tree ──────────────────────────────────────────────────────

META_PREFIX = "meta."

# Resource types whose `updated_at` is NOT preserved on import, so it tells us
# nothing about whether the operator changed the row. See `detectable`.
NO_TIMESTAMP_SIGNAL = {"space", "role", "permission", "group", "user"}

def decode_meta_path(rel):
    """(space, subpath, shortname, resource_type) from a source-relative meta path.

    Mirrors the layout pinned in PLAN.md §1 and its per-type table:

        {space}/.dm/meta.space.json                          the space itself
        {space}/{folder}/.dm/meta.folder.json                a folder
        {space}/{sub}/.dm/{sn}/meta.{rt}.json                an entry
        {space}/.dm/{sn}/meta.{rt}.json                      an entry at root
        {space}/{sub}/.dm/{sn}/attachments.{rt}/meta.{a}.json  an attachment

    Attachments return None: they are removed with their parent and are not
    independently addressable by the delete call this planner emits."""
    parts = rel.split("/")
    if len(parts) < 3 or ".dm" not in parts:
        return None
    if "attachments." in rel:
        return None
    space = parts[0]
    dm = parts.index(".dm")
    name = parts[-1]
    if not name.startswith(META_PREFIX) or not name.endswith(".json"):
        return None
    kind = name[len(META_PREFIX):-len(".json")]
    if kind == "space":
        return (space, "/", space, "space")
    folders = parts[1:dm]
    if kind == "folder":
        # The folder's own meta sits INSIDE it, so the folder is the last
        # segment before .dm and its parent is everything before that.
        if not folders:
            return None
        return (space, "/" + "/".join(folders[:-1]) if folders[:-1] else "/",
                folders[-1], "folder")
    # An entry: .dm/{shortname}/meta.{rt}.json
    if dm + 1 >= len(parts) - 1:
        return None
    shortname = parts[dm + 1]
    subpath = "/" + "/".join(folders) if folders else "/"
    return (space, subpath, shortname, kind)


def overlay_paths(pack_dir):
    """The `management/...` meta paths this pack ships, from its source tree.

    build.sh copies `<pack>/management/.` over `dist/spaces/management/`, so a
    path under the pack's management directory appears in the build at
    `management/<same relative path>`."""
    root = os.path.join(pack_dir, "management")
    out = set()
    for dirpath, _, names in os.walk(root):
        for n in names:
            if n.startswith(META_PREFIX) and n.endswith(".json"):
                rel = os.path.relpath(os.path.join(dirpath, n), root)
                out.add("management/" + rel.replace(os.sep, "/"))
    return out


def attachment_paths(spaces_root, only_spaces=None):
    """Attachment meta paths -> the meta path of the entry that owns them.

    Attachments are not planned on their own: they have no independent identity
    the delete call can address, and they live and die with their parent. But
    they ARE separate meta files that the import walk would pick up, so an
    import driven by --from-list has to name them explicitly or a case's
    comments and a dataset's CSV simply never land.

    So each one is tied to its parent, and sync.py pulls an attachment in
    whenever the entry above it is being imported."""
    out = {}
    marker = "/attachments."
    for dirpath, _, names in os.walk(spaces_root):
        for n in names:
            if not (n.startswith(META_PREFIX) and n.endswith(".json")):
                continue
            rel = os.path.relpath(os.path.join(dirpath, n), spaces_root).replace(os.sep, "/")
            if marker not in rel:
                continue
            if only_spaces and rel.split("/")[0] not in only_spaces:
                continue
            # ".../.dm/{parent}/attachments.{rt}/meta.{sn}.json" — the parent's
            # own meta is the one meta.*.json directly under .dm/{parent}/.
            head = rel[:rel.index(marker)]            # ".../.dm/{parent}"
            parent_dir = os.path.join(spaces_root, head.replace("/", os.sep))
            parent_meta = None
            if os.path.isdir(parent_dir):
                for c in sorted(os.listdir(parent_dir)):
                    if c.startswith(META_PREFIX) and c.endswith(".json"):
                        parent_meta = head + "/" + c
                        break
            out[rel] = parent_meta
    return out


def content_hash(meta_abs_path, meta_obj):
    """sha256 over the meta AND its externalized body.

    The body is the half that usually changes — a schema's rules, a case's
    text — and it lives in a sibling file named by `payload.body`, so hashing
    the meta alone would miss most real edits. `updated_at` is excluded because
    it is fixed by the generators and would contribute nothing."""
    h = hashlib.sha256()
    stripped = {k: v for k, v in sorted(meta_obj.items())
                if k not in ("updated_at", "created_at")}
    h.update(json.dumps(stripped, sort_keys=True, ensure_ascii=False).encode())
    body = (meta_obj.get("payload") or {}).get("body")
    if isinstance(body, str) and body:
        # `{space}/{sub}/.dm/{sn}/meta.x.json` -> `{space}/{sub}/{body}`
        meta_dir = os.path.dirname(meta_abs_path)          # .../.dm/{sn}
        candidates = [
            os.path.join(meta_dir, "..", "..", body),      # entry body
            os.path.join(meta_dir, "..", "..", "..", body),  # folder body
            os.path.join(meta_dir, body),                  # attachment body
        ]
        for c in candidates:
            if os.path.isfile(c):
                with open(c, "rb") as f:
                    h.update(f.read())
                break
    return h.hexdigest()[:32]


def scan_tree(spaces_root, only_spaces=None, restrict_to=None):
    """Every meta path in a built tree -> its identity, timestamp and hash."""
    found = {}
    for dirpath, _, names in os.walk(spaces_root):
        for n in names:
            if not (n.startswith(META_PREFIX) and n.endswith(".json")):
                continue
            abs_path = os.path.join(dirpath, n)
            rel = os.path.relpath(abs_path, spaces_root).replace(os.sep, "/")
            ident = decode_meta_path(rel)
            if ident is None:
                continue
            if only_spaces and ident[0] not in only_spaces:
                continue
            if restrict_to is not None and rel not in restrict_to:
                continue
            try:
                meta = json.load(open(abs_path))
            except Exception:
                continue
            found[rel] = {
                "space": ident[0], "subpath": ident[1],
                "shortname": ident[2], "rt": ident[3],
                "updated_at": meta.get("updated_at"),
                "hash": content_hash(abs_path, meta),
            }
    return found


def receipt_from_tree(pack, scale, shipped, now):
    """The record install.sh writes back after a successful install.

    `entries` is what lets the NEXT update tell a pack change from an operator
    change, so it is the part that must not be dropped to save space."""
    return {
        "format": RECEIPT_FORMAT,
        "name": pack["name"],
        "version": pack["version"],
        "space": pack["space"],
        "scale": scale,
        "installed_at": now,
        "provides": pack.get("provides", {}),
        # format 2: {path: [updated_at, hash]}. format 1 stored the timestamp
        # alone, which could not tell a pack edit from no edit at all.
        "entries": {rel: [info["updated_at"], info["hash"]]
                    for rel, info in sorted(shipped.items())},
    }


# ── version comparison ────────────────────────────────────────────────────────

def parse_version(v):
    """Dotted numeric version -> a comparable tuple.

    Deliberately strict: a pack version is machinery, and silently accepting
    `1.0.0-rc1` would make the ordering a guess. Pre-release suffixes can be
    added when something actually needs them."""
    parts = str(v).strip().split(".")
    if not parts or not all(p.isdigit() for p in parts):
        raise ValueError(
            f"version '{v}' is not dotted-numeric (e.g. 1.0.0) — refusing to "
            "guess its ordering")
    return tuple(int(p) for p in parts)


# ── the plan ──────────────────────────────────────────────────────────────────

def build_plan(shipped, receipt, db_state):
    """shipped: rel -> info (the new version)
       receipt: the previous install's record, or None for a fresh install
       db_state: (space, subpath, shortname) -> updated_at, as the DB has it"""
    plan = {k: [] for k in
            ("add", "update", "conflict", "kept", "remove", "orphan", "unchanged")}
    was = (receipt or {}).get("entries") or {}
    receipt_format = (receipt or {}).get("format") or 1

    def recorded(rel):
        """(updated_at, hash) from the receipt. A format-1 receipt stored only
        the timestamp, so the hash comes back None and the pack-side comparison
        falls back to the timestamp — which is all a format-1 receipt can
        support. The first format-2 install records hashes and the fallback
        stops being used."""
        v = was.get(rel)
        if isinstance(v, list):
            return (v[0] if v else None), (v[1] if len(v) > 1 else None)
        return v, None

    def db_stamp(info):
        return db_state.get((info["space"], info["subpath"], info["shortname"]))

    def detectable(info):
        """Whether an operator edit to this row can be detected at all.

        Only for ENTRIES. The importer binds an entry's shipped `updated_at`
        explicitly (ImportExportService.cs:2965), so a difference against what
        the pack shipped is real signal.

        Every other table discards it. The pattern is consistent across dmart:
        `created_at` is preserved when supplied, `updated_at` is always
        TimeUtils.Now() — spaces (SpaceRepository.cs:113-114), groups, roles and
        permissions (AccessRepository.cs:113-114, 236-237, 340-341) and users.
        So their timestamps are the write moment and comparing one would report
        "the operator edited this" after every single install.

        These are all pack machinery — a space meta, a role, a permission —
        rather than content anyone curates, and re-importing them is harmless.
        So they are treated as always-refreshed-from-the-pack, which is also
        what you want: a pack's authz should follow the pack."""
        return info["rt"] not in NO_TIMESTAMP_SIGNAL

    for rel, info in sorted(shipped.items()):
        row = dict(info, path=rel)
        if rel not in was:
            # No receipt entry for this path. Either the pack is genuinely new
            # here, or it was installed before receipts existed and there is no
            # baseline to compare against.
            #
            # The timestamp still carries the signal, so use it rather than
            # assuming: a row already present whose updated_at differs from what
            # the pack ships was written through dmart after it landed, and an
            # adopting install must not overwrite that. The cost is that a pack
            # change to such a row is also skipped — but without a baseline the
            # two are indistinguishable, and protecting the operator is the
            # right way to be wrong.
            existing = norm_stamp(db_stamp(info))
            if existing is None:
                plan["add"].append(row)
            elif not detectable(info) or existing == norm_stamp(info["updated_at"]):
                plan["unchanged"].append(row)
            else:
                plan["kept"].append(dict(row, db=str(existing),
                                         note="no receipt; adopting as-is"))
            continue
        stamp_before, hash_before = recorded(rel)
        pack_before = norm_stamp(stamp_before)
        in_db = norm_stamp(db_stamp(info))
        # Absent from the DB though the receipt claims it: someone deleted it.
        # Re-adding is the least surprising thing an update can do.
        if in_db is None:
            plan["add"].append(dict(row, note="was deleted"))
            continue
        operator_touched = in_db != pack_before
        # The CONTENT decides whether the pack changed it. The generators write
        # a fixed updated_at, so a timestamp comparison here would miss every
        # real edit; the hash sees them. A format-1 receipt has no hash, so it
        # falls back to the timestamp and will under-report — which is why the
        # first format-2 install re-records everything.
        if hash_before is not None:
            pack_changed = info["hash"] != hash_before
        elif receipt_format < RECEIPT_FORMAT:
            # A receipt older than this planner has no hashes, so nothing can
            # be compared and a timestamp fallback would under-report every
            # content edit made since. Treat the whole pack as changed ONCE, so
            # the format-2 receipt that follows is a real baseline.
            #
            # Safe because an operator-touched row still goes to `conflict`
            # below rather than being overwritten: the refresh is a re-import
            # of rows nobody has edited, which is a no-op for the unchanged
            # ones and a correction for the rest.
            pack_changed = True
        else:
            pack_changed = norm_stamp(info["updated_at"]) != pack_before
        if not detectable(info):
            # Not comparable, so classify by what the PACK did and leave the
            # operator out of it.
            plan["update" if pack_changed else "unchanged"].append(row)
            continue
        if pack_changed and operator_touched:
            plan["conflict"].append(dict(row, db=str(in_db),
                                            shipped=str(info["updated_at"])))
        elif pack_changed:
            plan["update"].append(row)
        elif operator_touched:
            plan["kept"].append(dict(row, db=str(in_db)))
        else:
            plan["unchanged"].append(row)

    for rel in sorted(was):
        if rel in shipped:
            continue
        stamp, _ = recorded(rel)
        ident = decode_meta_path(rel)
        if ident is None:
            continue
        space, subpath, shortname, rt = ident
        in_db = norm_stamp(db_state.get((space, subpath, shortname)))
        row = {"path": rel, "space": space, "subpath": subpath,
               "shortname": shortname, "rt": rt}
        if in_db is None:
            continue           # already gone; nothing to do
        if rt == "space":
            # Never auto-delete a space: dropping one takes every entry inside
            # with it, including anything the operator added. `reset.sh` is the
            # deliberate way to do that.
            plan["orphan"].append(dict(row, db=str(in_db),
                                       note="a space is never auto-deleted"))
            continue
        if rt in NO_TIMESTAMP_SIGNAL:
            # A role or permission the new version dropped. Safe to remove —
            # it is machinery, and leaving a stale grant behind is worse than
            # removing one the operator happened to tweak.
            plan["remove"].append(row)
            continue
        if in_db != norm_stamp(stamp):
            plan["orphan"].append(dict(row, db=str(in_db)))
        else:
            plan["remove"].append(row)
    return plan


def render(plan, pack_name, old_version, new_version, refreshing=False):
    lines = []
    head = (f"{pack_name}: {old_version} -> {new_version}"
            if old_version else f"{pack_name}: fresh install of {new_version}")
    lines.append(head)
    if refreshing:
        lines.append("  the stored receipt predates content hashing — "
                     "refreshing every untouched row once to rebuild the "
                     "baseline; your edits are still protected")
    order = [
        ("add", "will be added"),
        ("update", "will be updated (pack changed it, you did not)"),
        ("conflict", "SKIPPED — both the pack and you changed it"),
        ("kept", "left as you edited it (the pack did not change it)"),
        ("remove", "will be deleted (dropped by the new version)"),
        ("orphan", "KEPT — dropped by the new version but you edited it"),
        ("unchanged", "unchanged"),
    ]
    for key, label in order:
        rows = plan[key]
        if not rows:
            continue
        lines.append(f"  {len(rows):4d}  {label}")
        if key in ("conflict", "orphan", "remove", "kept"):
            for r in rows[:12]:
                lines.append(f"          {r['space']}{r['subpath']}/{r['shortname']}"
                             f" ({r['rt']})")
            if len(rows) > 12:
                lines.append(f"          ... and {len(rows) - 12} more")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--spaces", required=True, help="built tree (dist/spaces)")
    ap.add_argument("--manifest", required=True, help="the pack's pack.json")
    ap.add_argument("--pack-dir", help="the pack's source dir, for its management overlay")
    ap.add_argument("--receipt", help="previous receipt JSON; omit for a fresh install")
    ap.add_argument("--db-state", help="JSON of [[space,subpath,shortname,updated_at],...]")
    ap.add_argument("--scale", default="small")
    ap.add_argument("--emit-receipt", help="write the new receipt here")
    ap.add_argument("--emit-import-list", help="write the meta paths to import here")
    ap.add_argument("--emit-deletes", help="write the rows to delete here, as JSON")
    ap.add_argument("--json", action="store_true", help="print the plan as JSON")
    args = ap.parse_args()

    pack = json.load(open(args.manifest))
    if "version" not in pack:
        raise SystemExit(f"{args.manifest} has no `version` — cannot plan an update")
    new_version = pack["version"]
    parse_version(new_version)

    receipt = json.load(open(args.receipt)) if args.receipt else None
    old_version = receipt.get("version") if receipt else None
    if old_version:
        if parse_version(new_version) < parse_version(old_version):
            raise SystemExit(
                f"{pack['name']}: installed {old_version} is NEWER than {new_version}. "
                "Downgrading is not supported — reset the pack first if you mean it.")

    db_state = {}
    if args.db_state:
        for space, subpath, shortname, stamp in json.load(open(args.db_state)):
            db_state[(space, subpath, shortname)] = stamp

    # A pack owns two regions of the built tree: its own space, and its slice
    # of the shared `management` space. The second has to be attributed per
    # pack or an update would either skip every role and permission (so a
    # fresh install lands no authz at all) or treat another pack's overlay as
    # its own. The pack's source `management/` directory IS the list of paths
    # it owns, so that is what decides.
    overlay = overlay_paths(args.pack_dir) if args.pack_dir else set()
    shipped = scan_tree(args.spaces, only_spaces={pack["space"]})
    shipped.update(scan_tree(args.spaces, only_spaces={"management"},
                             restrict_to=overlay))
    plan = build_plan(shipped, receipt, db_state)

    if args.emit_receipt:
        now = datetime.datetime.now().replace(microsecond=0).isoformat()
        rec = receipt_from_tree(pack, args.scale, shipped, now)
        if receipt and receipt.get("installed_at"):
            rec["installed_at"] = receipt["installed_at"]
            rec["updated_at"] = now
        json.dump(rec, open(args.emit_receipt, "w"), indent=2)
    if args.emit_import_list:
        with open(args.emit_import_list, "w") as f:
            for r in plan["add"] + plan["update"]:
                f.write(r["path"] + "\n")
    if args.emit_deletes:
        json.dump(plan["remove"], open(args.emit_deletes, "w"), indent=2)

    if args.json:
        print(json.dumps({k: len(v) for k, v in plan.items()}))
    else:
        print(render(plan, pack["name"], old_version, new_version))
    # A conflict is not a failure — it is the planner doing its job — so the
    # exit code stays 0 and the caller decides.


if __name__ == "__main__":
    main()
