# Shanidar Telecom — a guided tour

Shanidar Telecom is a **fictional** Iraqi mobile operator. No real company,
brand, person or subscriber appears here: emails are `@example.com`, every
MSISDN follows a synthetic `+964 7X0 000 NNNN` pattern that is not assignable
on any live network, and the real operators are deliberately absent. The sites,
staff and customers are invented.

## Install it

```bash
export BACKEND_ENV=/path/to/config.env          # how the CLI finds the database
export DMART_URL=http://127.0.0.1:8282
export DMART_ADMIN_PASSWORD=...
export DMART_PACKS_DEMO_PASSWORD=...            # the personas' password

./packs/install.sh                              # 108 rows
# restart dmart here — see "Why the restart" below
./packs/demo.sh                                 # drive the storyline
```

`install.sh` lands the content with every case `open`. `demo.sh` then walks the
cases and requests through their workflows **over the real API as the real
users**, so the resulting state and history are produced by dmart's own engine
rather than written into the data.

## The three anchors

Everything in the demo hangs off three sites, and the same three recur across
every pack — that interlinking is the point.

| Site | Where | What makes it interesting |
| --- | --- | --- |
| `erb_0142` | Erbil, north | A macro site whose primary generator is faulty. Its September KPIs show 41 outage minutes, a customer case blames it, and a technician's visit found a dead starter. |
| `bsr_0031` | Basra, south | A healthy macro site. Its slow-router case is evening congestion, not a fault — the contrast that stops "slow" meaning "broken". |
| `krb_0007` | Karbala, central | A cell on wheels deployed for the Arbaeen pilgrimage. Its August is a partial month; its September carries the most data of any site. |

## Follow one thread

Start at the site and walk outwards. Every hop below is a real query.

**1 · The site**

```
@payload.body.region:north          in org/sites       → erb_0142, mos_0088
```

**2 · What is installed there**

```
@payload.body.site:erb_0142         in assets/equipment → gen_erb_0142_a (faulty), cab_erb_0142_1
```

**3 · What the numbers say**

```
@payload.body.site:erb_0142         in datamart/kpis    → 2026_09 availability 97.2%, 41 outage minutes
                                                        → 2026_08 availability 99.8%, 3
```

**4 · Who complained**

```
@payload.body.site:erb_0142         in servicedesk/cases → case_000101
```

**5 · What the agent should read**

```
@tags:coverage                      in kb/articles       → no_signal_triage
```

The case links to the site, the faulty generator, the product and the article —
as real `relationships`, so the graph is traversable — and separately carries
`site`, `product`, `equipment` and `article` as plain `payload.body` keys,
which is what the queries above actually filter on. **That duplication is not
style.** Relationship filtering does not work: `@relationships[].related_to.shortname:x`
is silently dropped from the WHERE clause and the query returns *unfiltered*
rows with no error. Try it and watch the total stay the same. The scalar key is
the only one a query can use.

## What `demo.sh` demonstrates

Each step is either a transition that succeeds or one that is refused, and the
refusals are the interesting half — they are dmart enforcing the workflow, not
the script pretending.

| Step | What it shows |
| --- | --- |
| agent takes, then escalates `case_000101` | the state machine, driven by the gating role |
| agent tries to close it once escalated → **refused** | `escalated` lists `resolve` for supervisors only |
| supervisor closes it `site_repaired` | the gate opens for the role that holds it |
| agent closes `case_000102` with no reason → **refused** | `resolution_required: true` on the transition |
| technician tries to approve his own request → **refused** | he holds no permission on `approvals` at all, so it is refused at the *read*, as "ticket not found" — dmart does not leak which tickets exist |
| officer approves one request, rejects the other | the same workflow, both outcomes |

`case_000104` is deliberately left **open**: a demo where every ticket is closed
has an empty worklist, which is the one view an agent lives in.

Afterwards, `case_000101` has three history rows — `open → in_progress` and
`in_progress → escalated` by `agent_baghdad`, `escalated → resolved` by
`sup_south`. Each was written by the engine at transition time and attributed to
the user who caused it.

## The personas

Each holds its gating role **directly**. A workflow gate reads `user.Roles` and
does not follow group membership, so a persona that drives a transition must
hold the role itself; the `org_region_*` groups carry regional ownership for the
`own` condition only.

| Persona | Role | Region group |
| --- | --- | --- |
| `agent_baghdad` | `servicedesk_agent` | central |
| `sup_south` | `servicedesk_supervisor` | south |
| `tech_north_erbil` | `assets_technician` | north |
| `tech_south_basra` | `assets_technician` | south |
| `security_officer` | `approvals_security` | central |
| `kb_author_najaf` | `kb_author` | central |
| `analyst_hq` | `datamart_analyst` | central |
| `editor_hq` | `catalogue_editor` | central |

All share `DMART_PACKS_DEMO_PASSWORD`, which is read from the environment at
install time and is never committed.

## Two content styles, on purpose

The `kb` pack holds **markdown** entries — real prose with tables and headings,
the body served as markdown. It ships **no schema**, because a schema validates
`payload.body` as JSON and a markdown body is not JSON. Its metadata rides as
`tags`, which `@tags:coverage` filters on.

Every other pack holds **JSON** entries against a real schema, and each folder
declares which schema and which resource type it accepts, so dmart rejects a
wrongly-shaped entry on write rather than storing it.

## The dataset ships twice

`datamart` publishes September's KPIs two ways, because one way is not enough:

**As a file you can download.** A real CSV riding as a `data_asset` attachment:

```bash
curl -H "Authorization: Bearer $TOKEN" \
  "$DMART_URL/managed/payload/dataasset/datamart/datasets/site_kpis_2026_09/site_kpis_2026_09.csv"
```

Note `dataasset`, not `data_asset`. The payload route parses the resource type
with `Enum.TryParse`, which matches the C# member name rather than the
`data_asset` wire value every other surface uses. It only bites the two-word
types, which is presumably why it has gone unnoticed.

**As rows you can query.** The same numbers as `kpi_row` entries under `/kpis`:

```
@payload.body.period:2026_09        in datamart/kpis    → 5 rows
```

Both, because **dmart serves the blob but does not run SQL inside it**. There is
no endpoint that queries within a data asset. The downloadable file is for
taking away; the entries are what answers a question — and only the entries
honour path-based ACL.

## Why the restart

`install.sh` ends by telling you to restart dmart, and it means it. The authz
cache is an in-process dictionary (`AuthzCacheRefresher`), not a materialized
view — the `mv_user_roles` / `mv_role_permissions` that the docs describe do not
exist. A CLI import writes the roles and permissions to the database but cannot
invalidate a running server's copy, so the personas will fail their permission
checks until the server restarts.

## What the demo does not claim

Three honest limits, each measured rather than assumed. They are written up with
citations in [PLAN.md](PLAN.md).

**The resolution catalogue is published, not enforced.** The workflow lists its
closing reasons in `resolutions[]` and the `case` schema constrains
`payload.body.resolution_code` to the same list. But the value dmart actually
stores — the top-level `resolution_reason` that `progress_ticket` writes — is
checked by nothing: `resolutions[]` is never read by the engine, and
`allowed_fields_values` is inert on the `progress_ticket` action. What *is*
enforced is presence, via `resolution_required: true`. Enforcing the value needs
a plugin.

**History is dated at install time, not at the story's dates.** The cases are
reported in September 2026 but their history rows carry the moment `demo.sh`
ran. Packs ship no `history.jsonl` on purpose: re-importing an archive *appends*
its history rather than upserting it, so a pack installed three times would
carry three copies. Generated history is real; authored history would have been
better dated and worse behaved.

**Scale beyond `small` is padding, not story.** `--scale medium` (×10) and
`--scale large` (×250) repeat the authored set with a `_gNN` suffix. The first
slice is always the real storyline, so the three anchors mean the same thing at
every scale, but the clones are volume for testing rather than narrative.
