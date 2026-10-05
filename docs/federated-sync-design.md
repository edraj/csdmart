# Federated sync — Phase 0 design

Recon output. **Nothing has been implemented.** This document records the
decisions already taken, the findings that constrain the design, the design that
follows from them, and the open decisions needing approval.

Read against `1d90ae0` (master).

Citations are `path:line`. Where a claim could not be settled from source it is
marked **VERIFY** with the experiment that settles it — those are deliberately
*not* presented as facts.

---

## Requirement

Multiple dmart instances on separate servers, each with its own database. A
*section* — a space plus a subpath — is synced between them. Instances operate
while disconnected and reconcile on reconnect.

Scale, given: **fewer than ten instances**, and **a maximum disconnection of
weeks**. Both numbers matter — version-vector width and tombstone retention key
off them, and both answers keep this tractable.

## Decisions already taken

| # | Decision | Consequence |
|---|---|---|
| D1 | Offline writes, reconciling later — **not** read-replicas | A change log and per-peer cursors are required; a pull-on-demand mirror is not enough |
| D2 | **Not** full multi-master | No version vectors, no conflict detection, no conflict resolution UI, no per-field merge |
| D3 | **Ownership by creator.** Any instance may create anywhere; each entry is editable only by the instance that created it | Removes every *edit* conflict by construction. Leaves exactly one conflict class — see finding 3 |
| D4 | **UUID is the sync key.** The tuple stays a uniqueness constraint | Renames and moves replicate as updates, not as delete+create |
| D5 | Minimum complexity preferred throughout | Section-level ownership with transfer (the "option B" discussed) is deferred, not designed |
| D6 | **`origin_id` is a readable slug** (`baghdad`), operator-chosen, validated against the shortname regex (settles O1) | Usable inside generated shortnames (O3) and legible in logs. Needs a startup check that the configured slug matches what the local data claims, and peer registration must refuse a slug equal to the local one or an existing peer's — nothing else stops two operators picking the same name |
| D7 | **Users sync as shortnames only, never credentials; the receiver creates a user only if that shortname does not already exist** (settles O2) | Satisfies the `owner_shortname` FK (finding 6) without moving password hashes. Same shortname is treated as the same person — see the note below |
| D8 | **No ownership handover** — D3 stands for all content (settles O7). Instead, each peer relationship may carry an **owner map** (`remote shortname → local shortname`) applied on ingest | Transferable ownership stays out of every phase. The map is a refinement of D7, not of D3: it changes who a received entry is *attributed* to, not which deployment may *edit* it |
| D9 | **A deletion record is a full snapshot**: the entry's uuid, complete meta *and* payload, who deleted it, when, and the request headers minus credentials | Tombstones become uuid-keyed (fixes finding 4) and double as a trash bin and the audit trail for deletes, which today write no history row at all. Rows get much larger, and payload content outlives the entry (§5) |
| D10 | **Two retentions on deletion records.** Sync retention (weeks or more) ends a record's use for sync; audit retention (default: forever) ends its existence | `prune-tombstones` raises the sync floor and no longer deletes rows; purging audit data becomes a separate, explicit operation |
| D11 | **The change log is derived, not appended** — `updated_at >= cursor` plus deletion records (settles O8) | Zero added write cost; reuses the parquet incremental machinery and its overlap-not-gap bargain. Idempotent replay is required anyway |
| D12 | **A sync relationship is scoped to space + subpath** (settles O6) | One section can be shared without exposing a whole space, at the price of more cursors per peer. Matches Python dmart's `sync.py` |

D3 is the cheap variant. It is a genuine offline-first system: every instance
writes while disconnected. What it gives up is the ability to *correct* an entry
created elsewhere while that elsewhere is unreachable.

**D7 and D8 together.** On ingest, a received entry's `owner_shortname` is
resolved in order:

1. If the peer's owner map has the shortname, use the mapped local user. That
   user must already exist locally; a map pointing at a missing user is a
   configuration error reported when the peer is configured, not a user
   created on the fly.
2. Otherwise, if a local user with that shortname exists, use it.
3. Otherwise, create it: shortname only, no password, no roles, no group
   membership. Sync never grants access. The stub can sign in only after a
   local admin sets a password (`dmart passwd`), exactly like the passwordless
   admin a fresh install creates.

Step 2 means **shortname equality is identity**: an unrelated `ahmad` on each
of two deployments becomes one person on receipt. The owner map is the remedy
when that is wrong (`ahmad → ahmad_basra`), and it is also how a deployment
folds a peer's users into its own (`field_agent_7 → basra_ops`).

The map rewrites attribution only. Edit rights still follow `origin_id` (D3,
phase 2): an entry received from `basra` and mapped to a local owner is still
editable only on `basra`. **VERIFY** in phase 1 whether history rows carry the
actor as an FK to `users`; if they do not, history keeps the original shortname
unmapped, so the record of who actually did the work survives the remapping.

---

## Headline: seven findings that constrain the design

| # | Assumption | Reality | Consequence |
|---|---|---|---|
| 1 | Identity needs designing | **Already there, and correct.** `uuid UUID PRIMARY KEY` plus `UNIQUE (shortname, space_name, subpath)`, and `move` preserves the uuid | D4 needs no schema change on `entries`. The tuple is an address; the uuid is the identity |
| 2 | Tombstones need building | **Already production-grade**, with four correctness rules and a retention floor | Deletion propagation is mostly solved |
| 3 | A tuple collision fails loudly | **It silently merges two entities.** The upsert resolves `ON CONFLICT (shortname, space_name, subpath)` and does not update `uuid` | The single most dangerous finding here. A uuid-keyed import path is mandatory, not a refinement |
| 4 | Tombstones identify the row | **`deletions` has no `uuid` column** — it is tuple-keyed | A tombstone cannot be applied safely under D4 without adding the uuid |
| 5 | Deployment identity exists somewhere | **Nothing.** No `site_id`, `node_id`, `instance_id` or `replica_id` anywhere. And two of those names are already taken by unrelated meanings | Must be added first — the one thing that cannot be reconstructed later — and must not be called `site` or `replica` (§4.1) |
| 6 | Entries can sync alone | **`owner_shortname` is an FK to `users`**, and scoped exports deliberately omit users | Some user identity must sync before the first entry can |
| 7 | A restore is a local matter | **A restore silently and permanently loses data.** Peers' cursors still say "I have everything from you up to N", so rows the restore rewound are never re-shipped | Phase 3's trigger is not "offline too long" but "this deployment's state went backwards" — a broader condition (§9) |

---

## 1. Identity — uuid is identity, the tuple is an address

Both already exist and both are enforced (`DataAdapters/Sql/SqlSchema.cs:195`,
`:222`):

```sql
CREATE TABLE IF NOT EXISTS entries (
    uuid  UUID PRIMARY KEY,
    ...
    UNIQUE (shortname, space_name, subpath)
```

The same shape repeats for `users`, `roles`, `groups`, `permissions`,
`attachments` and `spaces` (`SqlSchema.cs:50`–`:252`).

**The uuid survives a move or a rename.** `EntryRepository.MoveOnceAsync` is
(`DataAdapters/Sql/EntryRepository.cs:1022`):

```sql
UPDATE entries
   SET space_name = $2, subpath = $3, shortname = $4,
       query_policies = $5, updated_at = ...
 WHERE uuid = $1
```

That settles D4 on evidence rather than preference. Had peers matched on the
tuple, a rename would arrive as "the entry at the old address is gone, a new one
exists at a new address" — the receiver would apply a deletion that never
happened and create a duplicate with a fresh identity, losing the history chain.
Matching on uuid makes a rename what it is: an update of three columns.

Uniqueness of the tuple deliberately excludes `resource_type`
(`Services/EntryService.cs:438`), so an address identifies at most one row of any
type. That is the right granularity for a collision check.

## 2. Ownership by creator

Each entry carries the instance that created it. Only that instance may update or
delete it; every other instance applies incoming changes for it and refuses local
writes to it.

This is what removes conflict handling entirely. Two instances adding entries to
the same folder is **not** a conflict — different entries, different uuids, no
interaction. Only two instances editing *the same entry* is, and D3 makes that
impossible.

The enforcement point is `EntryService`, which already carries the equivalent
gate for locks: `Services/EntryService.cs:32-39` returns `LOCKED_ENTRY` with the
holder's name when another user holds the entry. A `FOREIGN_ENTRY` refusal
alongside it is the same shape.

**VERIFY** — whether the attachment tables' move/rename path also preserves the
uuid. Only `entries` was read (`EntryRepository.cs:1022`). Experiment: move an
entry with attachments, assert each attachment's uuid is unchanged.

## 3. The one remaining conflict, and why it is dangerous today

Creation is unrestricted under D3, so two disconnected instances can create
entries with **different uuids at the same tuple**:

- `baghdad` creates `/cases/invoice-2026-001`, uuid `7f3a…`
- `erbil` creates `/cases/invoice-2026-001`, uuid `b219…`

Neither is an edit conflict. But the existing write path does not reject this —
**it silently merges them.** Both upsert sites resolve on the tuple
(`EntryRepository.cs:181`, `:308`):

```sql
ON CONFLICT (shortname, space_name, subpath) DO UPDATE SET
    is_active = EXCLUDED.is_active, ..., payload = EXCLUDED.payload, ...
```

The `DO UPDATE SET` list (`EntryRepository.cs:181-201`) does **not** include
`uuid` or `created_at`. So importing Erbil's entry through this path leaves
Baghdad's uuid in place and overwrites every other column with Erbil's content.
Two distinct entities become one, Erbil's identity is discarded, and nothing
errors. That is precisely the failure `DataAdapters/Sql/Tombstones.cs:11`
describes as "the worst shape a replication bug can take" — drift that is never
noticed.

**Therefore a uuid-keyed import path is mandatory.** It must upsert
`ON CONFLICT (uuid)` and treat a tuple collision as an explicit case, never let
the tuple index resolve it.

### The resolution rule

Two halves:

**Generated names — prevent the collision.** Auto-shortnames are 8 hex
characters from a UUID, i.e. 32 bits (`Api/Managed/RequestHandler.cs:346`,
`Services/CsvService.cs:288`). `CsvService.cs:276` already records that a
self-collision is "expected roughly once" across a 100,000-row import on a single
instance. Independent minting on several instances makes it a certainty over
time. Include the instance in the generated name.

**Chosen names — detect, then rename deterministically.** The arriving entry
keeps its uuid, takes a suffixed shortname, and is recorded as renamed so a human
can settle it properly. Never reject: rejection loses data.

The tiebreak must be a **pure function of the two rows**, so every instance
reaches the same answer regardless of the order in which it pulls. **Tiebreak on
the uuid — the lexicographically lower uuid keeps the address.**

Do *not* tiebreak on `created_at`. Timestamps are stored local-naive in
`timestamp without time zone`, and `Services/ParquetArchiveService.cs:307-312`
documents the consequence: a watermark taken in UTC rather than
`TimeUtils.Now()` "silently skips every row changed inside the offset — which is
the corruption this whole mechanism exists to prevent," caught only by a test
returning 3 rows instead of 1. Independent servers in different offsets make
timestamp ordering unusable for a correctness decision. UUID comparison is
clock-free, totally ordered, and identical everywhere without coordination.

## 4. Deployment identity, the change log, and cursors

### 4.1 Two jobs, two names

Nothing in the codebase identifies a deployment: `site_id`, `node_id`,
`instance_id` and `replica_id` all return nothing. The concept is genuinely
absent, not merely named something else.

It is needed for four jobs:

1. **Suppressing echoes.** Without a provenance stamp, deployment B republishes
   A's changes as its own, A receives them back as foreign, and a change
   circulates indefinitely once there are three deployments.
2. **Per-peer progress.** One cursor per peer. A single global "last synced"
   cannot work when peers go offline at different times.
3. **Safe generated identifiers** — see §3.
4. Expressing causality, *if* D2 is ever revisited. Not needed under D3.

Jobs 1 and 3 are **provenance** — a property of a row, saying which deployment
created it. Job 2 is **participation** — a property of the sync relationship,
saying who the counterparts are. They carry the same value today, which is why
conflating them is tempting, but their lifetimes differ: when a deployment is
decommissioned it leaves the peer registry, while its rows must keep their
provenance for ever. One field for both forces a choice between orphaning rows
and rewriting history. So:

- **`origin_id`** on rows — provenance.
- **`peers`** — the participant registry and its cursors.

Neither is called `site` or `replica`, and deliberately. Both words already have
unrelated, load-bearing meanings here:

- **`site`** is the HTTP same-site/cross-site origin concept — `SameSite`,
  `SameSiteMode`, and the cross-site reasoning in `Auth/JwtBearerSetup.cs:68`
  and `:246` that governs whether cookie auth is accepted. "Site" in this
  codebase reads as cookie policy.
- **`replica`** is `session_replication_role` / `SetReplicaRoleAsync`, the
  bulk-import setting that `DataAdapters/Sql/Tombstones.cs:20` warns bypasses
  triggers.

`instance` is avoided for a different reason: in common usage `instance_id`
denotes *one process run* and invites regeneration on boot. This value must
survive restarts, upgrades and **database restores**, and must never be reused by
a different deployment. "Site" remains the right word for the *concept* in prose
— a separately administered, autonomous, long-lived location — and is used that
way throughout this document.

`origin_id` must be stamped into existing rows while there is still one
deployment. Once two have diverged without it, authorship cannot be
reconstructed, because it was never recorded.

### 4.2 The log

What the change log needs: `origin_id`, a per-deployment monotonic counter, the
entity uuid, the operation, and enough payload to apply it. The `peers` table
holds the last counter seen from each counterpart.

A deployment's own `origin_id` comes from config and is checked at startup
against what the data says: a silently regenerated value would make every local
row look foreign, which under D3 means every local row becomes read-only.

**Incremental selection is already correct and does not need rebuilding.**
`since` filters `updated_at`, not `created_at`
(`DataAdapters/Sql/EntryRepository.cs:498`: `AND updated_at >= $4`), backed by
`idx_entries_updated_at` (`SqlSchema.cs:372`). The watermark is taken *before*
the read (`ParquetArchiveService.cs:314`) so consecutive runs overlap rather than
gap — `:304-306`: "Overlap costs a re-shipped row that the import upserts away; a
gap loses one silently." That bargain only holds if replay is idempotent; see
§7.

Note which date mechanism is **not** usable: `Query.FromDate`/`ToDate` exist
(`Dmart.Models/Api/Query.cs:32-33`) and the zip export exposes them as
`--from`/`--to` (`Cli/CommandHandler.cs:39`), but they filter `created_at` —
`ParquetArchiveService.cs:380-383`: "an entry EDITED since the last run keeps its
original created_at, so an increment built on it would miss exactly the rows it
exists to carry." Only the parquet `--since` path is a valid delta.

**VERIFY** — whether `ParquetArchiveService.ImportAsync`
(`Services/ParquetArchiveService.cs:977`) upserts by uuid or by tuple. If by
tuple it inherits finding 3 directly. Experiment: import a parquet directory
containing an entry whose uuid differs from a local entry at the same tuple;
assert the local uuid is unchanged and the content is not overwritten.

## 5. Deletions

`DataAdapters/Sql/Tombstones.cs` is already built for incremental consumers and
enforces four rules (`:15`, `:20`, `:25`, `:31`):

1. Same transaction as the delete — a crash between the two loses the deletion
   invisibly, so every method takes the caller's connection.
2. In code, not a trigger — `import --fast` sets
   `session_replication_role='replica'`, which bypasses triggers during exactly
   the bulk operations that move the most rows.
3. Cascades tombstone every descendant, over the *same predicate* as the delete,
   so the two cannot disagree about what was removed.
4. `PruneAsync` raises a retention floor in the same transaction, so a watermark
   inside the pruned window **warns** instead of reading zero deletions and
   reporting success (`deletion_retention`, `SqlSchema.cs:361`;
   `ParquetArchiveService.cs:688`).

**The gap is finding 4: `deletions` carries no uuid** (`SqlSchema.cs:335-343`,
and identically in SQLite at `SqliteSchema.cs:363-371`):

```sql
CREATE TABLE IF NOT EXISTS deletions (
    id BIGSERIAL PRIMARY KEY, table_name TEXT NOT NULL,
    space_name TEXT NOT NULL, subpath TEXT NOT NULL, shortname TEXT NOT NULL,
    resource_type TEXT NOT NULL DEFAULT '', deleted_at TIMESTAMP NOT NULL DEFAULT NOW()
);
```

A tuple-keyed tombstone is unsafe under D4 in two ways. An entry renamed and then
deleted produces a tombstone naming the *final* address, which a peer that has
not yet seen the rename cannot match. Worse, where §3's collision has occurred,
applying a tuple-keyed tombstone deletes whichever entry currently holds that
address — possibly not the one that was deleted.

Adding `uuid` to `deletions` is a schema change on both engines plus every
`Tombstones.RecordAsync` call site, since the `INSERT ... SELECT` must project it.

**The record itself (D9).** Every tombstone carries the deleted entry in full —
uuid, meta and payload — plus the actor, the deletion time and the request
headers. Headers go through the same filter history already applies, which
drops `authorization` and `cookie` (**VERIFY** where that filter lives: the
comment at `Api/User/RegistrationHandler.cs:58` names the rule). Three
consequences to design for in phase 0:

- **Cascades multiply the cost.** Rule 3 tombstones every descendant, so
  deleting a folder of 10,000 entries writes 10,000 full snapshots in the
  delete's transaction. The `INSERT ... SELECT` keeps it one statement, but the
  rows are now as large as the entries they replace.
- **Payload outlives the entry.** Deleting content no longer removes it from
  the database. Anyone who needs it truly gone (personal data, a legal request)
  needs the audit purge of D10, scoped to the entry.
- **Undelete becomes possible** but is not designed here. The record is
  sufficient for it; the endpoint is a separate feature.

**Two retentions (D10).** Sync and audit want different lifetimes, so one table
carries both. The existing `deletion_retention` floor becomes the *sync*
floor: `prune-tombstones` raises it, and an increment whose watermark is below
it still warns and forces a full reconcile (§9, phase 3), but no row is removed.
Rows are deleted only by a separate audit purge, by age or by entry, which
defaults to never.

**Retention must exceed the maximum disconnection**, i.e. weeks per the
requirement, and a reconnect past the floor must force a full-state reconcile
rather than an increment. The warning exists; the policy and the full-reconcile
path do not.

**VERIFY** — the current retention default and prune cadence. No setting for
either was located. Experiment: find the `PruneAsync` caller and its schedule.

## 6. What cannot sync

To be settled before stage 1, not discovered during it.

- **Users, roles, permissions.** `owner_shortname TEXT NOT NULL REFERENCES
  users(shortname) DEFERRABLE INITIALLY DEFERRED` (`SqlSchema.cs:206`) means the
  first entry received from a peer, owned by a user absent locally, violates the
  FK. `DEFERRABLE INITIALLY DEFERRED` defers to commit, not past it. But scoped
  parquet exports deliberately omit these tables because `users` holds the Argon2
  password hash (`ParquetArchiveService.cs:34`, `:497`). So entry sync requires
  syncing *some* user identity — at minimum shortnames, without credentials.
- **Locks are meaningless across a partition.** A lock held on one instance
  cannot block another that is disconnected (`Services/LockService.cs:68`). They
  degrade to advisory-per-instance.
- **`group` does not round-trip** through import/export — import classifies only
  user, role and permission metas (`packs/PLAN.md` finding 3).
- **Relationship integrity.** References dangle while the target has not
  arrived, and relationships are not RI-checked on the bulk import path at all
  (`packs/PLAN.md`), so a verifier has to do it.
- **Permission caches do not invalidate across instances.**
  `docs/architecture.md:341`: "No cross-instance invalidation (yet)." ACL changes
  arriving by sync leave peers serving stale decisions.
- **Workflow state.** Not a conflict under D3, since only the owner advances a
  ticket. Becomes unmergeable if D2 is ever revisited.

## 7. Dependencies on known defects

- **History import is not idempotent** — every line inserts a fresh row, so a
  re-shipped increment duplicates history (`packs/PLAN.md` finding 2). The
  overlapping-watermark design in §4 depends on idempotent replay, so this must
  be fixed before increments ship history.
- **Stale comment.** `ParquetArchiveService.cs:32` states the incremental
  watermark "is stamped but not yet USED to select rows." It is used —
  `EntryRepository.cs:498`. Anyone picking this up is told the mechanism they
  need does not exist.

History is *not* required under D3 — ownership by creator needs no merge — but
`HistoryRow.Diff` is `{field_path: {old, new}}` (`Services/HistoryDiffUtil.cs:9`,
`:114`) and `HistoryRow.LastChecksumHistory` is a per-entry hash chain
(`Dmart.Models/Core/HistoryRow.cs`). Together those would support field-level
three-way merge and cheap divergence detection should D2 be revisited. Arrays are
stored as whole values at their key (`HistoryDiffUtil.cs:21-30`), so array edits
would be field-granular, not element-granular.

## 8. Prior art

The Python repo has `backend/sync.py` (229 lines), the only existing
implementation. There is no equivalent in csdmart — nothing under `Cli/` matches
`sync`, and the command table offers only `import <zip>` and `export`
(`Cli/CommandHandler.cs:38-39`).

`sync.py` queries `managed/query` on both sides, SHA1s each record, diffs by
shortname, then creates local-only records on the target, **deletes target-only
records**, and updates those whose hash differs. It is a proof of concept for one
flat folder and should not be the basis for this work:

- `limit` defaults to 100 with a manual offset and no paging loop. Because
  deletion is inferred from absence, every target entry past the first page looks
  removed and is deleted.
- One-way and destructive: the target is forced to mirror the source.
- `exact_subpath: True`, one subpath per invocation — no recursion.
- The checksum covers only shortname, displayname, description and
  `payload.checksum`. `is_active`, `tags`, `relationships`, `acl`, owner and
  workflow state are all invisible.
- Attachments are not synced.
- Local credentials are hardcoded at module scope.

What it does establish is intent: section-scoped (`-sp` space, `-su` subpath)
sync between two named instances.

---

## 9. Restore interacts badly with cursors

Raised after the first draft and **not yet resolved**; it widens phase 3.

Restoring a deployment from a parquet archive rewinds its own rows. Its peers'
cursors do not rewind — each still records "I have everything from this
deployment up to counter N". So every row the restore undid is never re-sent, the
restored deployment permanently lacks content its peers believe it has, and
nothing detects the divergence. That is the same failure shape
`DataAdapters/Sql/Tombstones.cs:11` calls the worst kind: drift that is never
noticed.

The same applies, more quietly, to any operation that moves a deployment's state
backwards — a partial restore, a manual `DELETE`, a database rollback.

Likely resolution: a restore must invalidate the peers' view of this deployment
and force the full reconcile that phase 3 already builds. That makes phase 3's
trigger **"this deployment's state went backwards"** rather than merely "a peer
was offline past the retention floor", which is a broader and harder condition to
detect — it probably needs a generation or epoch counter bumped on restore, so a
peer can notice the rewind rather than having to be told.

Unresolved either way. Phase 3 should not be scoped until it is.

## Objective

**One sentence:** several independently administered dmart deployments, each
usable while cut off from the others, agreeing about the sections they share once
they can talk again — without an operator ever hand-carrying a zip.

What that is worth, concretely:

- **A branch office keeps working when the link is down.** Today a remote office
  either runs against a central server and stops when the network does, or runs
  its own dmart and diverges permanently with no way back.
- **Content authored anywhere reaches everywhere.** Head office publishes a
  catalogue, a schema, a workflow or a solution pack once; every deployment has
  it without a manual import.
- **No single point of failure for reads or writes.** Each deployment serves its
  own users from its own database.
- **The shared sections converge without a human adjudicating.** Convergence is
  mechanical: ownership-by-creator means there is never a question of who wins,
  so reconciliation needs no judgement call.

What it is explicitly **not**: a cluster, a hot standby, a backup strategy, or a
way to scale one logical dmart across machines. Deployments stay independent and
separately administered. Backup remains the parquet archive's job.

The success test for the whole programme: **unplug a deployment for a month, keep
working on it, plug it back in, and have both sides end up correct — including
deletions, renames and attachments — with no operator intervention and no
silent loss.**

## Staging

Four phases. Each is independently useful and leaves the system in a working
state; none requires the next one to exist.

| Phase | Delivers | Useful on its own because | Depends on |
|---|---|---|---|
| **0** | `origin_id` on every deployment and stamped into existing rows; `deletions` widened to the D9 snapshot with the D10 split retention | Nothing observable changes — but the data becomes *capable* of carrying provenance, which it can never be made to do retroactively | — |
| **1** | Change log, `peers` cursors, pull transport, uuid-keyed import, deterministic collision rename | This is replication working end to end. Sections sync, deletions and renames carry correctly. Still trusting everyone to behave | 0; D7/D8 user resolution; idempotent history (§7) |
| **2** | Ownership enforcement — a `FOREIGN_ENTRY` refusal on writes to entries another deployment created | Turns the D3 convention into an invariant. Before this, a well-meaning local edit to a foreign entry is silently overwritten on the next pull, with no warning | 1 |
| **3** | Full-reconcile path for a peer returning past the tombstone retention floor | Bounds the damage from the one failure mode phases 1–2 cannot handle: a deployment offline longer than deletions are kept, where increments are provably incomplete | 1 |

### What each phase is really about

**Phase 0 — make the data capable.** No behaviour change, no feature, nothing a
user sees. It exists because provenance cannot be backfilled: once two
deployments have been writing independently without it, no migration can work out
which one created which row. It is the only phase whose omission is
*unrecoverable* rather than merely inconvenient, and it is also the smallest.

**Phase 1 — make it work.** The substantive engineering, and the phase where the
existing machinery pays off: tombstones, the `since` watermark and the overlap
bargain are already built and correct (§5, §4.2). What is new is the change log,
the cursors, a transport, and the uuid-keyed import that finding 3 makes
mandatory. At the end of phase 1 the success test above passes for cooperative
deployments.

**Phase 2 — make it safe.** Phase 1 converges correctly only if nobody edits an
entry they do not own. Nothing stops them. The failure is quiet: the edit sticks
locally, looks saved, and vanishes on the next pull. Phase 2 refuses the write at
`EntryService` instead, alongside the existing lock gate (§2), so the user is
told rather than misled. This is the phase that makes D3 real rather than a
convention written in a document.

**Phase 3 — handle the long absence.** Tombstone retention has to exceed the
maximum disconnection, and the requirement says weeks. A deployment that returns
after longer than that cannot be brought up to date by an increment, because the
deletions it needed have been pruned — and `PruneAsync`'s retention floor means
this is *detected* rather than silently wrong (§5). Phase 3 is the recovery path:
fall back to a full state comparison for the affected sections. Without it the
answer to "the office was offline for three months" is a manual rebuild.

### What is deliberately not in any phase

- Transferable section ownership (the "option B" of D5) — not needed (D8). The
  owner map covers attribution; edit rights stay with the creating deployment.
- Version vectors, conflict detection, conflict resolution UI — excluded by D2.
- Syncing users, roles, permissions or groups as content — see §6. D7 creates
  bare user stubs only; it never carries credentials, roles or groups.
- Cross-deployment cache invalidation, locks or workflow coordination — §6.

## Open decisions

**O1 — `origin_id` name and format.** *Settled: D6 (readable slug).*

**O2 — User identity across instances.** *Settled: D7 (shortnames only, create
if missing), refined by the D8 owner map.*

**O3 — Generated-shortname format.** Prefix with `origin_id`
(`baghdad_a3f91b02`), or widen the hex and accept probabilistic safety? Recommend
the prefix: it makes collision impossible rather than unlikely, and the regex
permits `_` but **not** `-` (`Services/CsvService.cs:204`), so the separator
must be `_`. **VERIFY** the regex before implementing.

**O4 — Collision rename suffix.** `_{origin_id}` is readable; `_{uuid[..8]}` is
unambiguous. Recommend `_{origin_id}`, with the uuid appended only if that is
also taken.

**O5 — Transport.** The parquet export/import pair is filesystem- and
operator-only; the zip pair has HTTP endpoints (`/managed/export`,
`/managed/import`, `Api/Managed/ImportExportHandler.cs:15`, `:54`). Add an HTTP
surface to the parquet path, or sync over a new endpoint of its own?

**O6 — Scope of a sync relationship.** *Settled: D12 (space + subpath).*

**O7 — Is D3 sufficient?** *Settled: D8. Yes for all content; no ownership
handover. The owner map handles attribution instead.*

**O8 — Derive the change log, or append to it?** *Settled: D11 (derive).*

## Phase 1 verification list

Run before implementing. Each is marked **VERIFY** above and is deliberately not
stated as fact.

1. Attachment move/rename preserves the attachment uuid (§2).
2. `ParquetArchiveService.ImportAsync` upsert key — uuid or tuple (§4).
3. Tombstone retention default and prune cadence (§5).
4. The shortname regex's permitted separator characters (O3).
5. Both engines behave identically for 1–3. CI runs PostgreSQL and SQLite legs
   separately and they have diverged before.

## Stopping here

No schema change, no `origin_id`, no transport, no core changes yet. Phase 0 can
start now that O1, O2, O6, O7 and O8 are settled (D6–D12). §9 is needed before
phase 3.
