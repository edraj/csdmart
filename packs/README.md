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

## What is in it

The storyline is anchored on three sites that recur across every pack, which is
what makes the demo interlinked rather than eight unrelated folders:
`erb_0142` (Erbil — a faulty generator, a customer case, a bad KPI month),
`bsr_0031` (Basra — the healthy comparison) and `krb_0007` (Karbala — a cell on
wheels for the Arbaeen pilgrimage).

At `small` scale: 5 sites, 3 regions, 4 products, 5 tariffs, 7 equipment units,
3 maintenance visits, 4 markdown KB articles, 4 cases with comments, 2 access
requests, 8 KPI rows, 1 dual-shipped dataset, 2 notices, and 8 personas.

Two content styles on purpose: `kb` holds **markdown** entries with `tags` and
no schema (a schema validates `payload.body` as JSON, which markdown is not);
every other pack holds **JSON** against a real schema, with each folder
declaring the schema and resource type it accepts.

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

Nothing under `packs/<name>/space`, `packs/<name>/management` or
`packs/datasets/` is hand-written:

```bash
python3 packs/lib/gen_spaces.py        # space + folder metas, folder_rendering
python3 packs/lib/gen_management.py    # roles and permissions
python3 packs/lib/gen_schemas.py       # the content schemas
python3 packs/lib/gen_workflows.py     # the two state machines
python3 packs/lib/gen_dataset.py --scale small
```

`packs/lib/shanidar.py` holds the storyline itself — sites, cases, articles,
personas, and the resolution catalogues. It is the one file to edit to change
what the demo says.

All are deterministic — UUIDv5 from a fixed namespace and one fixed timestamp —
so re-running produces a byte-identical tree. That matters because the output is
committed, and a churning diff would hide real change.

They also check themselves: `gen_management.py` fails if a pack's `pack.json`
and its generated permissions drift apart, `gen_workflows.py` fails on a
transition naming a state that does not exist or a closing transition that
forgets `resolution_required`, and `gen_dataset.py` fails if it ever writes a
`history.jsonl`.

## Verified

Installed on both drivers against a clean, seeded instance: **108 rows, 0
failed** — 7 spaces, 20 folders, 12 schemas, 2 workflows, 77 entries, 8
attachments, 8 roles, 8 permissions, 3 groups and 8 personas. Every folder
returns a resolved `folder_rendering` payload, so CXB renders all 20. A second
install skips every existing row. `reset.sh --packs kb` removed exactly the `kb`
space, role and permission, left dmart's own `super_admin`, `logged_in` and
`world` intact, and a re-install restored it.

`demo.sh` then drove every workflow path: three cases resolved by the role that
was allowed to, four refusals that should have been refused (an agent closing an
escalated case, a close with no reason, a technician approving his own request),
and `case_000101` left with three history rows attributed to the two users who
caused them. Identical on both drivers: same transitions, same refusals, same
final states, same history.

`--scale medium` was generated and installed too — 342 rows, ×10 across every
entity, with generation N's equipment pointing at generation N's own site
rather than all of them piling onto the authored one.
