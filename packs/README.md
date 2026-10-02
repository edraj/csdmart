# Solution packs

Ready-made dmart applications defined as data. A pack is a space, the folders
and schemas inside it, and the roles and permissions that make it usable —
nothing compiled, nothing in core. Install one with `install.sh`, remove it
with `reset.sh`.

The storyline is **Shanidar Telecom**, a fictional Iraqi mobile operator. No
real company, person, brand or subscriber appears anywhere: emails are
`@example.com` and MSISDNs follow a synthetic `+964 7X0 000 NNNN` pattern.

## Layout

```
packs/
  <name>/
    pack.json          the manifest — what the pack is, needs and provides
    space/             its space tree, in dmart's on-disk .dm layout
    management/        its roles and permissions, as a management overlay
  datasets/shanidar/   demo content by scale; only `small` is committed
  lib/                 generators for the space trees and overlays
  dist/                build output (gitignored)
  build.sh install.sh reset.sh
```

A directory is a pack if and only if it holds a `pack.json`, so `lib/`,
`datasets/` and `dist/` are not packs and the scripts need no hardcoded list.

## The packs

| Pack | Depends | Links | Space |
|---|---|---|---|
| `org` | — | — | sites, regions — the spine the others hang off |
| `catalogue` | org | org | products, tariffs |
| `assets` | org | org | field equipment and maintenance |
| `servicedesk` | org | catalogue, assets, kb | customer cases on a workflow |
| `approvals` | org | assets, org | role-gated approval requests |
| `kb` | — | catalogue, servicedesk | markdown articles |
| `datamart` | org | assets, catalogue, servicedesk | KPI datasets |
| `comms` *(optional)* | org | assets, kb | outbound notices |

`depends` is a hard requirement and is resolved transitively — asking for
`servicedesk` installs `org` too. `links` is narrative only: it records which
packs point at each other through relationships, and installs nothing.

`comms` is marked `optional`, so it is excluded from the default set and has to
be named explicitly.

## Usage

```bash
export BACKEND_ENV=/path/to/config.env      # how the CLI finds the database
export DMART_URL=http://127.0.0.1:8282      # for the API phase
export DMART_ADMIN_PASSWORD=...             # for the API phase

./packs/install.sh                          # every non-optional pack
./packs/install.sh --packs servicedesk      # that pack plus its dependencies
./packs/install.sh --packs kb,comms --scale small
./packs/reset.sh --packs kb                 # drop one pack
```

`build.sh` alone assembles `dist/spaces/` without touching any database, which
is the thing to run when inspecting what *would* be imported.

## Three constraints the code imposes

These are not design preferences. Each was measured against a running dmart and
is written up with citations in [PLAN.md](PLAN.md).

**Groups cannot be imported.** A `group` does not round-trip through
export/import — one sitting in the `groups` table is simply absent from the
archive. So `pack.json` lists groups under `provides.groups` and `install.sh`
creates them over the HTTP API in a second phase. That is the only reason
`install.sh` needs a URL and a password at all.

**The server must restart after installing.** dmart's authz cache is an
in-process dictionary (`AuthzCacheRefresher`), not a materialized view — the
documented `mv_user_roles` / `mv_role_permissions` do not exist. A CLI import
writes the roles to the database but cannot invalidate a running server's copy,
so new roles and permissions stay invisible until it restarts. `install.sh` says
so when it finishes.

**Packs ship no history.** Re-importing an archive *appends* its
`history.jsonl` rows every time rather than upserting them, so a pack installed
three times would carry three copies of its history. Synthetic history has no
demo value, so nothing authors one and `build.sh` refuses to build if it finds
one.

Re-running `install.sh` is otherwise idempotent: entries, folders, spaces, roles
and permissions are keyed and skipped (`skipped 43 existing, 0 failed`).

## Regenerating

The space trees and management overlays are generated, not hand-written:

```bash
python3 packs/lib/gen_spaces.py
python3 packs/lib/gen_management.py
```

Both are deterministic — UUIDv5 from a fixed namespace and one fixed timestamp
— so re-running produces a byte-identical tree. That matters because the output
is committed, and a churning diff would hide real change. `gen_management.py`
also fails if a pack's `pack.json` and its generated permissions drift apart.

## Verified

Installed on both drivers against a clean, seeded instance: **43 rows, 0
failed** on SQLite and on PostgreSQL, 7 spaces, 20 folders, 8 roles, 8
permissions and 3 groups. Every folder returns a resolved `folder_rendering`
payload, so CXB renders all 20. A second install reports `skipped 43 existing,
0 failed`. `reset.sh --packs kb` removed exactly the `kb` space, role and
permission, leaving dmart's own `super_admin`, `logged_in` and `world` intact,
and a re-install restored it.
