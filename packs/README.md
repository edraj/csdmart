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
export DMART_URL=http://127.0.0.1:8282      # for the API phases
export DMART_ADMIN_PASSWORD=...             # for the API phases
export DMART_PACKS_DEMO_PASSWORD=...        # the demo personas' password

./packs/install.sh                          # every non-optional pack
./packs/install.sh --packs servicedesk      # that pack plus its dependencies
./packs/install.sh --packs kb,comms --scale small
./packs/install.sh --public                 # also open the public surface
./packs/install.sh --dry-run                # print the plan, change nothing
./packs/demo.sh                             # drive the storyline's workflows
./packs/reset.sh --packs kb                 # drop one pack
```

`demo.sh` walks the cases and access requests through their workflows over the
real API as the real personas, so the resulting state and history come from
dmart's engine rather than from the data. [DEMO.md](DEMO.md) is the guided tour
— start there.

`build.sh` alone assembles `dist/spaces/` without touching any database, which
is the thing to run when inspecting what *would* be imported.

## What is in it

The storyline is anchored on three sites that recur across every pack, which is
what makes the demo interlinked rather than eight unrelated folders:
`erb_0142` (Erbil — a faulty generator, a customer case, a bad KPI month),
`bsr_0031` (Basra — the healthy comparison) and `krb_0007` (Karbala — a cell on
wheels for the Arbaeen pilgrimage).

At `small` scale: 5 sites, 3 regions, 4 products, 5 tariffs, 7 equipment units,
3 maintenance visits, 4 markdown KB articles, 4 agent-worked cases with
comments, 2 customer-raised cases, 1 queued public-intake case, 2 access
requests, 8 KPI rows, 1 dual-shipped dataset, 2 notices, and 10 personas —
eight staff and two customers.

Two content styles on purpose: `kb` holds **markdown** entries with `tags` and
no schema (a schema validates `payload.body` as JSON, which markdown is not);
every other pack holds **JSON** against a real schema, with each folder
declaring the schema and resource type it accepts.

## The public surface is opt-in

Two packs ship a public face, and **neither opens by default**. Installing a
pack must not quietly make anything world-readable, so each lists its public
roles under `provides.public_roles` and `install.sh --public` is what grants
them to dmart's `anonymous` user.

| Pack | Public role | Opens |
| --- | --- | --- |
| `kb` | `kb_public` | anonymous read of `kb/articles` — a help centre |
| `servicedesk` | `servicedesk_public` | anonymous **create** in `servicedesk/intake` — a contact form |

Granting is additive and reversible. `AdminBootstrap` already creates
`anonymous` holding the `world` role, and permission resolution walks every role
a user holds — so the pack's own permission does the scoping, the seeded `world`
permission stays inert and untouched, and `reset.sh` takes the role back off.
The install unions rather than overwrites, because dropping `world` would stop
the world permission resolving at all.

Three things worth knowing:

**`/public/submit` is gated by config as well as permissions.** An empty
`ALLOWED_SUBMIT_MODELS` closes it no matter what the anonymous user may create.
Anonymous intake needs this in `config.env`, and a restart:

```
ALLOWED_SUBMIT_MODELS="servicedesk.intake_case"
```

`install.sh --public` checks for it and tells you if it is missing, rather than
rewriting a file that holds secrets.

**Intake is create-only.** A public caller may post a case and may not read one
back — not even the one they just filed. `/public/submit` owns the entry as
`anonymous`, so there is no "their own" to read.

**The intake schema is separate from `case` on purpose.** `case` requires
region, severity and `reported_on`; none of those can be asked of a member of
the public, and severity is an agent's judgement. `intake_case` has its own
minimal required set, and an agent promotes an intake entry to a full case on
triage. Keeping them separate also means enabling public submit cannot
accidentally open `servicedesk.case`.

## A signed-in customer sees only their own cases

`servicedesk_customer` is the other half of the approved design: a customer with
an account creates cases **owned by themselves** and queries only those. Two
personas hold it, `customer_erbil` and `customer_basra`, and each sees exactly
one case out of the six-plus in the space.

The filtering is worth understanding, because it is not where you would look.
`conditions: ["own"]` does it — but `CheckConditions` is *exempt* for `create`
and `query`, so `own` is not what gates the query.
`BuildUserQueryPoliciesAsync` emits a policy pattern carrying the actor's
shortname (and each of their groups) in the owner segment, and the SQL ACL
filter matches that against every row's own `query_policies`. `view` takes the
other path, where the condition *is* enforced, so a cross-customer read is
refused as well.

Customer personas are deliberately in **no group**: `org_region_*` carries
internal ownership, and a customer inheriting one would widen what their
`own`-scoped query can reach, since a pattern is emitted per group too.

## A pack cannot reach outside itself

`build.sh` refuses to build a pack that grants itself anything it does not own.
This is the check that makes a pack from someone else's repo safe to install —
without it, installing one means handing it your whole instance.

Three ways out, all refused:

| the pack tries to | refused because |
| --- | --- |
| claim `__all_spaces__` | a pack may only grant access to its own space |
| name another pack's space | same rule, with the owning space named in the error |
| ship a role or permission not `<pack>_` prefixed | it could **overwrite** a dmart-seeded row — a pack shipping `super_admin` would redefine it |
| hold a permission it does not provide | it would borrow a grant belonging to something else |

`__all_subpaths__` *inside* the pack's own space is fine, and several packs use
it. The rule is about which space, not how much of it.

**One consequence worth knowing.** A pack cannot grant read access to a space it
merely `links` to, so a servicedesk agent cannot read the kb article a case
cites unless the kb pack grants it or an operator adds a permission by hand.
Relaxing the rule to cover a pack's *declared* `links` would fix that and would
still be reviewable from the manifest — but it is a widening, so it is a
decision rather than something to slip in.

## A second storyline

Nothing in the machinery is telecom-specific. A dataset is one module in
`packs/lib/` plus a name:

```bash
PACKS_DATASET=school python3 packs/lib/gen_dataset.py --scale small
./packs/install.sh --dataset school
```

The name also seeds the UUIDs, so two storylines never collide — and renaming
an existing one would re-identify every row in every install of it, so it is
chosen once.

[`packs/lib/example_dataset.py`](lib/example_dataset.py) is the contract: every
attribute the generators read, with a one-region, one-site storyline that
builds. Copy it to start a school, a restaurant or an ecommerce pack. An empty
list is a fine answer for a pack you are not populating — the structures still
install.

## Versions, and what an update may touch

Each pack carries a `version` in its manifest, separate from `format` (the
manifest's own shape). `install.sh` stores a **receipt** at
`management/packs/<name>` after a successful install, recording the version and
the `updated_at` of every row the pack landed. That receipt is what lets the
next install tell a pack change from one of yours.

```bash
./packs/install.sh --packs org --dry-run    # print the plan, change nothing
./packs/install.sh --packs org              # apply it
```

The rule the planner enforces, and the reason any of this exists:

> **An update never overwrites or deletes a row you changed.**

A pack is someone else's software landing in your database. If you edited an
entry it shipped, your edit wins and the update tells you it skipped you.

| the pack | you | what happens |
| --- | --- | --- |
| added it | — | **added** |
| changed it | left it alone | **updated** |
| changed it | changed it too | **skipped**, and named in the output |
| left it alone | changed it | **kept** as you have it |
| dropped it | left it alone | **deleted** |
| dropped it | changed it | **kept**, and named in the output |

A downgrade is refused outright — `reset.sh` first if you mean it, or
`--force`.

### How an edit is detected

No checksums and no extra bookkeeping: dmart's own timestamps carry it. The
importer binds an entry's shipped `updated_at`
(`ImportExportService.cs:2965`), while any write through `EntryService` replaces
it with `Now()` (`EntryService.cs:965`). A pack ships fixed timestamps, so for
any row it owns:

```
db.updated_at == what the pack shipped   ->  untouched since install
db.updated_at != what the pack shipped   ->  written through dmart since
```

**One carve-out worth knowing.** Only *entries* preserve that timestamp. Spaces,
roles, permissions, groups and users always get `Now()` on write
(`SpaceRepository.cs:113-114`, `AccessRepository.cs:113-114, 236-237, 340-341`),
so their timestamps carry no signal at all. They are pack machinery rather than
content anyone curates, so they are always refreshed from the pack — which is
also what you want, since a pack's authz should follow the pack. A space is
never auto-deleted, because dropping one takes everything inside it.

### Installing without a receipt

An install that predates receipts has no baseline. Rather than assume, the
planner still reads the timestamps: a row already present whose `updated_at`
differs from what the pack ships is adopted as-is, not overwritten. The cost is
that a genuine pack change to such a row is also skipped — but with no baseline
the two are indistinguishable, and protecting you is the right way to be wrong.

### Checking the planner

```bash
python3 packs/lib/test_plan.py
```

26 assertions over the table above, including every case I got wrong while
building it. It is **not** wired into CI — there is no Python step in
`.github/workflows/ci.yml` — so run it after touching `plan_update.py`.

## Three constraints the code imposes

These are not design preferences. Each was measured against a running dmart and
is written up with citations in [PLAN.md](PLAN.md).

**Groups cannot be imported.** A `group` does not round-trip through
export/import — one sitting in the `groups` table is simply absent from the
archive. So `pack.json` lists groups under `provides.groups` and `install.sh`
creates them over the HTTP API in a second phase. That is the only reason
`install.sh` needs a URL and a password at all.

**A running server cannot see the new roles until its authz cache is cleared.**
That cache is a process-local dictionary (`AuthzCacheRefresher`), not a
materialized view — the `mv_user_roles` / `mv_role_permissions` the docs used to
describe never existed. Only a write made *through* the server clears it, so a
CLI import leaves it stale.

`install.sh` calls `GET /managed/reload-security-data` for you when it has a URL
and a token, which fixes it without dropping connections. Restarting works too.
Run the install without `--url` and it tells you to do one or the other.

**Packs ship a twelve-month archive, and it survives a re-install.** Six cases
arrive with their `history.jsonl` — dated from 2025-11 to 2026-08 and attributed
to the agent or supervisor who did the work — alongside history on a non-ticket
entry, the `erb_0142` generator going faulty.

That was impossible until #329. The importer called `AppendAsync`, the path for
*new* events, so it stamped every imported row with the import moment and
appended a duplicate on each re-run; an earlier revision of these packs shipped
no history for exactly that reason. It now restores an authored `uuid` and
`timestamp` and dedupes on the uuid. Measured: 17 history rows after one
install, and still 17 after three.

So `build.sh`'s check inverted. It no longer refuses history — it refuses
history that would *silently lose its dates*, failing on any line missing
`uuid`, `timestamp` or `owner_shortname`. Such a line falls back to
`AppendAsync` and gets `now()` with no error, which is the quiet failure worth
catching.

Re-running `install.sh` is idempotent throughout: entries, folders, spaces,
roles, permissions and now history are all keyed and skipped.

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

`packs/lib/` also holds the install machinery, which is not generated:
`plan_update.py` (what an update may touch), `sync.py` (plans every selected
pack in one pass), `db_state.py` (reads the current timestamps) and
`test_plan.py` (the planner's self-test).

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

Installed on both drivers against a clean, seeded instance: **142 rows, 0
failed** — 7 spaces, 21 folders, 11 schemas, 2 workflows, 88 entries, 8
attachments, 17 authored history rows, 11 roles, 11 permissions, 3 groups and
10 personas. The archive lands dated 2025-11-04 to 2026-09-18, and three
successive installs leave it at 17 rows. Every folder
returns a resolved `folder_rendering` payload, so CXB renders all of them. A
second install skips every existing row. `reset.sh --packs kb` removed exactly
the `kb` space, role and permission, revoked `kb_public` from the anonymous user
while leaving `world` and `servicedesk_public` intact, left dmart's own
`super_admin`/`logged_in`/`world` alone, and a re-install restored it.

The public surface, verified on both drivers with **no restart** after the
install:

| check | result |
| --- | --- |
| anonymous query of `kb/articles` | 4 records |
| anonymous direct view of one article | ok |
| anonymous query of `servicedesk/cases`, `intake`, `org/sites`, `datamart/kpis` | 0 records each |
| anonymous `POST /public/submit` | accepted, owned by `anonymous` |
| `customer_erbil` / `customer_basra` query `cases` | 1 record each, their own |
| `agent_baghdad` queries the same folder | 6 records |
| `customer_erbil` views `customer_basra`'s case | refused |
| a customer creates a case | owned by them, visible to them immediately |

`demo.sh` then drove every workflow path: three cases resolved by the role that
was allowed to, four refusals that should have been refused (an agent closing an
escalated case, a close with no reason, a technician approving his own request),
and `case_000101` left with three history rows attributed to the two users who
caused them. Identical on both drivers: same transitions, same refusals, same
final states, same history.

`--scale medium` was generated and installed too — 342 rows, ×10 across every
entity, with generation N's equipment pointing at generation N's own site
rather than all of them piling onto the authored one.
