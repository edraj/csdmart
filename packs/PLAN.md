# Solution Packs — Phase 0 plan

Recon output: the ten Phase-0 questions answered with citations, with every
core-change request kept separate at the end.

**Status: APPROVED 2026-10-02.** The storyline is **Shanidar Telecom** and all
four remaining proposals were accepted as recommended. The decisions are
recorded below as settled rather than open. Phase 1 starts with the
verification list at the end of this document, because three of its five items
change what the structures must contain.

Read against `8ded171` (master).

Citations are `path:line`. Where a claim could not be settled from source it is
marked **VERIFY** with the experiment that settles it — those are deliberately
*not* presented as facts.

---

## Headline: eight findings that change the brief

The brief makes assumptions that the code contradicts. These drive the design,
so they are up front rather than buried in the answers.

| # | Brief assumes | Reality | Consequence |
|---|---|---|---|
| 1 | History may not be importable; propose API replay | **History imports** as `history.jsonl`, with author and timestamp per row | No replay. Author history directly, at real dates. |
| 2 | — | **Re-import duplicates history** (append, never upsert) | `install.sh` must pass `--skip-history` on re-runs, or reset first. |
| 3 | Packs ship groups in `management_overlay` | **`group` does not round-trip** through import/export | Groups must be created over the API by `install.sh`. |
| 4 | Workflow gates may match roles inherited through groups | Gates read **`user.Roles` only** | Every transition-driving persona needs the role *directly*. |
| 5 | `/public/submit` + `own` gives customers their own cases | Submitted entries are owned by **`anonymous`** | Customer-owned cases need an authenticated create, or a re-owning step. |
| 6 | Plugins must call REST as a service account | A `save_entry` **callback exists** — but it bypasses validation/permissions and attributes history to the **triggering user** | Use REST + service account anyway, now for a *demonstrated* reason. |
| 7 | `shop_daily_sales` SQLite is "queryable through the API" | **No endpoint queries inside a data asset** | Ship the blob *and* mirror rows as entries, or drop the claim. |
| 8 | Resolution comes "from a catalogue" | Workflow `resolutions` is **not enforced**; only presence of `resolution_reason` is | Enforce the catalogue with `allowed_fields_values`, or accept free text. |

---

## 1. Serialization — the `.dm` layout

Authoritative layout: `Services/ImportExportService.cs:21-38` (file header),
with the writers at `:352, :371, :387, :403, :425, :430, :570`.

```
{space}/
  .dm/meta.space.json                                  space
  {folder}/.dm/meta.folder.json                        folder meta (inside the folder)
  {folder}.json                                        folder payload body (folder_rendering)
  {subpath}/.dm/{shortname}/meta.{resource_type}.json  entry meta
  {subpath}/{shortname}.json                           externalized payload body
  {subpath}/.dm/{shortname}/history.jsonl              per-entry history
  {subpath}/.dm/{shortname}/attachments.{rt}/
      meta.{att_shortname}.json                        attachment meta
      {att_shortname}.json                             JSON attachment body
      {media_filename}                                 raw bytes (name from payload.body)
management/users/.dm/{sn}/meta.user.json
management/roles/.dm/{sn}/meta.role.json
management/permissions/.dm/{sn}/meta.permission.json
```

Rules that bite:

- **The meta filename encodes the resource type** — `meta.{rt}.json` using the
  `[EnumMember]` wire name (`:430`). Folders are the exception: always
  `meta.folder.json`, placed *inside* the folder's own `.dm` (`:425`).
- **Attachments do not use the `.dm/{sn}/` shape.** They are
  `attachments.{rt}/meta.{att_sn}.json` — flat, prefixed (`:570`, and the
  comment at `:568-569` calls this out as a Python convention divergence).
- `space_name`, `subpath`, `resource_type` and `query_policies` are **stripped**
  from every meta on export and re-injected from the path on import
  (`:3137-3147`). Do not author them.
- Ticket fields are stripped from **non-ticket** metas only (`:3149-3158`);
  tickets keep `state`, `is_open`, `reporter`, `workflow_shortname`,
  `collaborators`, `resolution_reason`.
- The legacy flat layout (no `{space}/` root) is **rejected** (`:46-48`).

Real examples in-tree, one per type:

| Resource | Example path |
|---|---|
| space | `seed/spaces/management/.dm/meta.space.json` |
| folder | `seed/spaces/management/permissions/.dm/meta.folder.json` + `permissions.json` |
| schema | `seed/spaces/management/schema/.dm/workflow/meta.schema.json` + `schema/workflow.json` |
| content | `seed/spaces/management/workflows/.dm/channel/meta.content.json` + `workflows/channel.json` |
| permission | `seed/spaces/management/permissions/.dm/access_public/meta.permission.json` |
| role | `seed/spaces/management/roles/.dm/logged_in/meta.role.json` |
| media attachment | `seed/spaces/management/schema/.dm/admin_notification_request/attachments.media/meta.ui_schema.json` (+ `ui_schema.json` body) |

**No in-tree example exists** for `ticket`, `user`, `group`, `comment`,
`reaction`, `relationship`, or the `data_asset`/`csv`/`jsonl`/`parquet`/`sqlite`
family. Their meta filenames follow the `meta.{EnumMember}.json` rule
(`Dmart.Models/Enums/ResourceType.cs:21,38-41`), and `data_asset`/`csv`/… are
**attachment** types (`docs/data-model.md:161-174`), so they take the
`attachments.{rt}/` shape, not `.dm/{sn}/`.
**VERIFY (Phase 1):** create one of each over the API, `dmart export`, and read
the layout back. That is the only honest way to pin these, and it is cheap.

---

## 2. What import preserves

**Preserved:**

- `owner_shortname` — taken from each meta; the `actor` argument is explicitly
  *not* threaded through (`ImportExportService.cs:680-684`). `EnsureOwner`
  backstops a malformed export with the literal `dmart`.
- `created_at` / `updated_at` — written verbatim unless the meta omits them, in
  which case `now` is substituted: `e.CreatedAt == default ? now : e.CreatedAt`
  (`:2910-2911` entries, `:3003-3004` attachments).
- Ticket `state`, `is_open`, `workflow_shortname`, `resolution_reason`,
  `reporter`, `collaborators` — carried on the ticket meta (`:3149-3158`).
- Attachments, including media bytes (`:507-573`).
- **History** — `history.jsonl` is a first-class member of the format and is
  imported in Pass 5 (`:1761-1800`).

The history line format (`:611-630`):

```json
{"uuid":"…","shortname":"history","owner_shortname":"tech_north_erbil",
 "timestamp":"2026-02-11T09:04:17.0000000","request_headers":{…},"diff":{…}}
```

**So the brief's fallback is unnecessary.** We author history directly, with the
right persona in `owner_shortname` and the right date in `timestamp`. No replay,
no "timestamps would be now" problem, and ticket state transitions can be told
as a story that happened over twelve months.

**Not preserved / caveats:**

- **History import is not idempotent.** "History append is NOT idempotent (every
  line inserts a fresh row)" (`:1773-1774`). A second `install.sh` run duplicates
  every history row. Mitigation in the installer: `--skip-history` unless the
  target is empty (see §3).
- **Referential integrity is not enforced on import.** The RI gate lives in
  `EntryService.CreateAsync` (`Services/EntryService.cs:124-133`); bulk import
  writes through repository COPY. A dangling `related_to` will land silently.
  → `packs/verify/` must check relationship resolution itself; that is now a
  requirement, not a nicety.
- **Schema validation differs by source.** The FS path validates by default
  (`--no-validate` opts out, `Program.cs:1601`); the **zip path does not validate
  at all** (`ImportExportService.cs:717-720`). → install from the **directory**,
  not the zip, so payload errors surface at install time.

---

## 3. Import scope, merging, idempotency

- **One archive, many spaces — yes.** The parallel path is explicitly keyed on
  "the zip carries more than one space" (`:666-676`), and `--spaces=A,B,C`
  filters which ones load (`Program.cs:1601` → `includeSpaces`).
- **Merging into `management` is additive and non-clobbering by default.**
  "Without `-r` the import is idempotent — pre-existing rows are skipped and the
  operator sees them counted as `skipped`" (`Program.cs:1356-1360`). Seeded roles
  and permissions survive untouched. With `-r/--replace` existing rows are
  rewritten from the pack — which is why `install.sh` will **never** pass `-r`.
- **Namespacing still matters**, because "skip existing" means a name collision
  silently keeps the *other* pack's row. Every role/permission/group/workflow
  shortname is prefixed with the pack name.
- **`group` does not round-trip.** Import classifies only `user`, `role`,
  `permission` metas (`:1520-1525`), under the hardcoded parent folders
  `users`/`roles`/`permissions`. There is no group branch anywhere in the
  service. Groups must be created over the API
  (`docs/permissions.md:261-270`).
- **`world` must be patched over the API, not imported.** It is a pre-existing
  bootstrapped row, so an import without `-r` would skip it; and
  `docs/permissions.md:96-98` warns that a non-API write leaves `query_policies`
  ungenerated and the authz cache stale.
- `query_policies` **are** regenerated for imported entries
  (`:2885-2890`), so ordinary pack content is fine.
- **Installer ordering consequence:** the standalone CLI writes straight to the
  DB, so a *running* server keeps a stale in-process authz cache
  (`docs/permissions.md:355-361`: process-local, no cross-instance
  invalidation). `install.sh` will import with the server down, or restart it
  after. **VERIFY:** confirm the materialized views refresh at boot as
  `docs/data-model.md:256-258` states.

---

## 4. Relationships

Two distinct mechanisms:

**(a) `meta.relationships`** — a JSONB array on the entry itself
(`DataAdapters/Sql/SqlSchema.cs:72`). Shape, from the integration test that pins
it (`dmart.Tests/Integration/RelationshipsRefIntegrityTests.cs:47-57`):

```json
{"related_to": {"type":"content","space_name":"assets","subpath":"/sites/north","shortname":"ERB-0142"}}
```

- Gated both ways by RI **when written through the API**: write-time resolution
  and delete-time blocking (`RelationshipsRefIntegrityTests.cs:12-27`,
  `EntryService.cs:124-133`). Only entries-table targets are checked; `user`,
  `role`, `permission`, `space` pass through unchecked (`EntryService.cs:256-260`).
- Returned in query results under `attributes.relationships`
  (`RelationshipsRefIntegrityTests.cs:227`) — so **forward traversal is free**.
- **Reverse traversal is not a public API.** `FindFirstReferencerAsync`
  (`EntryRepository.cs:743-812`) exists only to name the blocker on a failed
  delete, and returns just the first hit. PostgreSQL uses `@>` against
  `idx_entries_relationships_gin`; SQLite walks `json_each` per row and is a
  full scan (`:769-795`).

**(b) `ResourceType.Relationship` as an attachment** — a row in `attachments`
bound to a parent (`docs/data-model.md:161-167`,
`Api/Managed/RequestHandler.cs:406`).

**Proposal:** use **(a)** for every cross-pack link. It travels in the entry's
own meta (so one file describes the entry completely), it is returned with the
record, and it is the form the RI gates understand.

The typed vocabulary needs somewhere to live. `Relationships` is
`List<Dictionary<string, object>>` (`Dmart.Models/Core/Entry.cs:37`), so keys
beyond `related_to` are free-form and stored verbatim in JSONB:

```json
{"related_to": {...}, "attributes": {"relation": "installed_at"}}
```

`@>` containment ignores extra fields on the stored side
(`EntryRepository.cs:734-737`), so the RI probe still matches.
**VERIFY (Phase 1):** that the extra key survives an export→import round trip,
and whether `@relationships[].related_to.shortname:X` is a legal search
selector. The array-query syntax is documented (`docs/query.md:98-120`) but every
test exercises it on `payload.body.*` paths, never on a top-level column
(`dmart.Tests/Unit/Services/QueryHelperTests.cs:90-141`). If it is not legal,
the fallback is a duplicated scalar in `payload.body` for filtering.

---

## 5. Public surface

Anonymous reads need three things, all provisioned inert by `AdminBootstrap`
(`docs/permissions.md:76-109`):

1. an `anonymous` user row,
2. **with at least one role** — zero roles means `world` is never consulted,
3. a `world` permission whose `subpaths` actually name a space (it ships
   `{}`, matching nothing).

`world` is folded into every role's permission set during resolution
(`:80-83`). Its seeded `resource_types` deliberately exclude `user`, `group`,
`role`, `permission`, `acl`, `log`, `history` (`:103-107`).

Note `conditions:["is_active"]` interacts with the walk: `create` and `query`
are exempt from condition checks, `view` is not (`:272-286`) — which is why a
`world` permission with `is_active` passes `/public/query` but fails a direct
`view`.

`/public/submit` (`Api/Public/SubmitHandler.cs:19-22`) takes three URL forms;
the ticket form requires a workflow:

```
/public/submit/{space}/{resource_type}/{workflow}/{schema}/{subpath}
```

Two constraints the brief does not account for:

- It is gated by the **`ALLOWED_SUBMIT_MODELS` setting** — a CSV of
  `space.schema` pairs (`Config/DmartSettings.cs:723-725`,
  `SubmitHandler.cs:55-58`), *not* by a permission. `install.sh` must set it,
  and the value is deployment config, not pack data.
- **Submitted entries are owned by `anonymous`** (`SubmitHandler.cs:93,105-108`).
  So the persona rule "customers see only their own cases" cannot be delivered
  by `own` on a public submission.

**Proposal:** intake stays anonymous into `/servicedesk/intake` (it is genuinely
anonymous — an OTP-verified MSISDN in the payload, not an account). The three
demo customers log in (MSISDN + `MOCK_OTP_CODE`, `Config/DmartSettings.cs:434`)
and their cases are created authenticated, so `own` works as intended. The demo
then shows *both* paths, and the difference between them is a feature of the
story rather than a fudge.

---

## 6. Workflows

- A workflow is an ordinary `content` entry with `schema_shortname: "workflow"`
  under `/workflows` (`seed/spaces/management/workflows/.dm/channel/meta.content.json`).
- Resolution: `WorkflowEngine.LoadWorkflowAsync` → match `states[].state` to the
  ticket's current state, then match `next[].action`
  (`Services/WorkflowEngine.cs:46-67`).
- Target state key is `state`, with `to` accepted for compatibility (`:69-76`).
- **Open/closed is derived**, not declared: a state is open iff it has a `next`
  array (`:33, :89-90`). No `closed_states` list despite the stale header comment
  at `:19`.
- `resolution_required: true` on a transition forces `resolution_reason` to be
  present (`:91-92`, enforced `WorkflowService.cs:79-86`).

**Role gates: `user.Roles` only.**

```csharp
var user = await users.GetByShortnameAsync(actor, ct);
return user?.Roles ?? Array.Empty<string>();
```
`Services/WorkflowService.cs:182-187`

This excludes **both** the implicit `logged_in` role that `PermissionService`
adds (`docs/permissions.md:60-66`) **and** anything derived from groups. Groups
affect the `own` condition only (`docs/permissions.md:280`). So: any persona who
must drive a transition carries the gating role directly on their user row.
Groups are used for ownership scoping, never for workflow gating.

Two defects worth noting (neither blocks us):

- The workflow **schema** declares `states/next/role` (singular)
  (`seed/spaces/management/schema/workflow.json`) while both the engine
  (`WorkflowEngine.cs:80`) and the seeded `channel.json` use `roles`. The schema
  is wrong; we follow the engine.
- The schema declares `states/next/resolutions` (a catalogue) but **nothing
  reads it** — no reference in `Services/` or `Api/`. Only presence of
  `resolution_reason` is checked; its *value* is unvalidated
  (`WorkflowService.cs:79-90`).

**Proposal for the resolution catalogue:** enforce it with
`allowed_fields_values` on the agent's permission (`docs/permissions.md:300-307`)
and *also* list it in the workflow's `resolutions` for the UI.
**VERIFY:** that `allowed_fields_values` can address the top-level
`resolution_reason` attribute (permissions.md mentions a `FlattenAttrs` helper at
`:383`) and not only `payload.body.*`. If it cannot, the honest fallback is a
JSON-Schema `enum` on a `resolution` field inside `payload.body`, with
`resolution_reason` mirroring it.

---

## 7. Plugins

A subprocess plugin speaks one JSON object per line and may interleave
**callback frames** before its final response (`docs/plugins-and-mcp.md:115-146`).
The ops are `load_entry`, `load_user`, `save_entry`, `update_user`, `send_email`,
`ws_broadcast`, `query`, `log`, `get_session_firebase_tokens`,
`invalidate_firebase_tokens`, `get_media_attachment`
(`Plugins/Native/PluginCallbackDispatcher.cs:68-143`).

**So a plugin *can* write without touching REST** — contradicting the brief's
expectation. But `save_entry` is the wrong tool here, for two reasons found in
source:

1. **It bypasses `EntryService` entirely**, going straight to
   `EntryRepository.UpsertWithPriorAsync`
   (`Plugins/Native/NativePluginCallbacks.cs:140-182`). No schema validation, no
   relationship RI, no permission check, no folder content policy, no uniqueness.
   Both our plugins create *linked* entries, which is exactly what RI protects.
2. **History is attributed to the triggering user, not the plugin.**
   `PluginInvocationContext.CurrentActor` is "the hook's user_shortname, or the
   API request's resolved user" (`Plugins/Native/PluginInvocationContext.cs:5-10`),
   and `EmitSaveEntry` passes it to `history.AppendAsync`
   (`NativePluginCallbacks.cs:160-164`). The brief requires "history shows
   automation as its own actor" — `save_entry` gives the opposite.

**Proposal: REST as a scoped service account**, which satisfies both. The
scoping then actually means something, because the write goes through the
permission walk.

**Credentials.** The child inherits dmart's environment — `ProcessStartInfo` sets
no `Environment` and `UseShellExecute=false`
(`Plugins/Native/SubprocessPluginHost.cs:265-274`) — so an env var would be
visible to *every* plugin. Instead: `install.sh` writes
`~/.dmart/plugins/<name>/credentials.json` mode `0600` from an install-time env
var, beside the existing `config.json` (`docs/plugins-and-mcp.md:88-113`).
Nothing secret is committed.

**Event payload** and filter shape are the `config.json` `filters` block —
`subpaths`, `resource_types`, `schema_shortnames`, `actions`
(`docs/plugins-and-mcp.md:99-112`). After-hooks do not block the response
(`:61`), so a crashing plugin cannot fail the originating write — the brief's
"fail safe" requirement is already the platform's behaviour; we will assert it
rather than build it.

---

## 8. MCP

Tools (`docs/plugins-and-mcp.md:217-228`, registered in `Api/Mcp/McpRegistry.cs`):
`dmart_me`, `dmart_query`, `dmart_get_entry`, `dmart_create`, `dmart_update`,
`dmart_delete`, `dmart_semantic_search`.

Scoping is **the caller's own permissions** — the registry descriptions state it
per tool ("Permissions are enforced — the caller sees only what they may see",
`McpRegistry.cs:79,106,143,170,237,264`). An MCP client authenticates as a dmart
user through the OAuth 2.1 chain (`docs/plugins-and-mcp.md:232-261`) and gets a
dmart JWT; there is no separate MCP authorization surface.

So `ai_ops_south` is an ordinary user with a read-only, South-scoped role. Story
5 ("a write attempt is denied and audited") works because `dmart_create` runs
the same permission walk as any write.

---

## 9. Kurdish convention

Existing translations are **Sorani (Central Kurdish) in the Perso-Arabic
script** — `catalog/src/i18n/ku.json`, 2018 keys, e.g.
`"retry": "دووبارە هەوڵبدە"`, `"cancel": "هەڵوەشاندنەوە"`. The Kurdish-specific
letters (ە ڕ ێ ۆ ڵ) confirm Sorani rather than Kurmanji, and there is no Latin
variant anywhere in the tree.

We follow that: `ku` values are Sorani in Perso-Arabic script. The language code
is `ku` in the DB enum and `kurdish` on the wire
(`docs/data-model.md:182,273-276`).

All generated `ar`/`ku` strings go in `TRANSLATIONS.md` as machine-generated,
pending native review.

---

## 10. Core changes requested

**None.** Everything the packs need exists. Three items are *limitations to
document* rather than changes to request:

1. **No query-inside-a-data-asset.** `/managed/execute` resolves a *saved query
   entry* and runs it against the entries table
   (`Api/Managed/ExecuteTaskHandler.cs:29-31`); there is no endpoint that reads
   inside an attached SQLite/Parquet/CSV blob. Handled by design (§datamart), not
   by a core change.
2. **Group import.** Worked around via the API; a `meta.group.json` branch in
   `ImportExportService` would be the smallest fix, but it is not needed for
   these packs and I am not requesting it.
3. **Workflow schema `role`/`resolutions`.** A one-line schema correction and a
   `resolutions` enforcement would both be *improvements to dmart*, not pack
   blockers. Flagging, not requesting.

If review wants any of these built, they become separate PRs with their own
justification.

---

## Proposals for approval

### Storyline name — SETTLED: Shanidar Telecom

`Lamassu` passes the brief's literal test (not an Iraqi operator or retailer),
but **"Lamassu For Mobile Applications" is a real IT company in Almansour,
Baghdad** ([Facebook](https://www.facebook.com/lamassuIraq/)) — a real Iraqi
company with "Lamassu" and "Mobile" in its name is a poor choice for a
fictional mobile operator, so the name changed.

**`Shanidar Telecom`** it is (Shanidar Cave, Erbil governorate — a place and
archaeological site, which also fits the northern framing). Searches for
Shanidar and Anzu as Iraqi telecom businesses return nothing. Dataset directory
is `packs/datasets/shanidar/`. Real operators, deliberately unused for
contrast: Zain Iraq, Asiacell, Korek.

### Pack list

| Pack | Depends | Links | Spaces |
|---|---|---|---|
| `org` | — | — | `org` |
| `catalogue` | org | org | `catalogue` |
| `assets` | org | org | `assets` |
| `servicedesk` | org | catalogue, assets, kb | `servicedesk` |
| `approvals` | org | assets, org | `approvals` |
| `kb` | — | catalogue, servicedesk | `kb` |
| `datamart` | org | assets, catalogue, servicedesk | `datamart` |
| `comms` *(optional)* | org | assets, kb | `comms` |

One space per pack, named for the pack — so a pack's permission subpaths are a
clean `{space: [...]}` and uninstalling is a space drop.

### Relationship vocabulary

Carried as `attributes.relation` alongside `related_to` (§4):
`located_in`, `installed_at`, `about_product`, `served_by`, `spawned`,
`affects`, `uses_article`, `onboarded_as`, `stocked_at`.

### Naming

- Roles/permissions/groups/workflows: `<pack>_<name>` —
  `servicedesk_agent`, `servicedesk_customer_case`, `assets_tech_north`.
- Spaces: bare pack name.
- Anchor shortnames for DEMO.md and verify: `ERB-0142` (site), `BSR-0031`
  (Basra generator site), `KRB-0007` (Arbaeen COW), fixed per story.

### Personas

As the brief lists. Two clarifications forced by findings:

- Every persona that drives a workflow transition gets its gating role
  **directly** (finding 4): `agent_baghdad`→`servicedesk_agent`,
  `sup_south`→`servicedesk_supervisor`, `tech_north_erbil`→`assets_technician`,
  `security_officer`→`approvals_security`, `acct_mgr_dealers`→`account_manager`,
  `backoffice_channel`→`backoffice` (the last two reuse the seeded `channel`
  workflow's existing gate names).
- Groups carry **regional ownership** for the `own` condition only —
  `org_region_north`/`_central`/`_south`.

Demo passwords come from `DMART_PACKS_DEMO_PASSWORD` at install time; nothing
committed.

### Datamart, given finding 7

Each dataset ships **twice**:
- the file as a `data_asset` attachment (downloadable, real Parquet/SQLite/CSV/JSONL), and
- its rows as `content` entries under `/kpis/<region>` etc., which *are*
  queryable and *do* honour path-based ACL.

The README will say plainly that dmart stores and serves the blob but does not
run SQL inside it. That is a smaller claim than the brief's, and true.

### Scales

`small` committed; `medium` (~×10) and `large` (~×250) generated on demand and
gitignored.

---

## Phase 1 verification list

Carried forward as the first work of Phase 1, before any bulk authoring:

1. Round-trip one entry of every resource type the packs use → pin the real
   `.dm` layout for ticket/user/comment/reaction/relationship/data_asset (§1).
2. Confirm `attributes.relation` survives export→import, and whether
   `@relationships[]...` is a legal selector (§4).
3. Confirm `allowed_fields_values` can constrain top-level `resolution_reason` (§6).
4. Confirm the authz materialized views refresh at boot (§3).
5. Confirm a history-bearing re-import duplicates rows, and that `--skip-history`
   prevents it (§2) — this decides `install.sh`'s re-run contract.

---

## Phase 0 outcome

Phase 0 ended at this document: nothing was written beyond `packs/PLAN.md`, no
core code was touched, and no core change was made or requested.

All five were approved on 2026-10-02: Shanidar Telecom, one space per pack with
`<pack>_` prefixes, `attributes.relation` beside `related_to`, datamart
dual-shipping, and authenticated customer creates alongside anonymous intake.

Phase 1 begins with the verification list above — items 1, 2 and 3 can each
change the structures, so they run before any are authored.
