# Directory fields on users: mailbox, aliases, services

Status: implemented on `spike/ldap-face` (PR #365). Companion to
`bench/REPORT-ldap-face.md`.

dmart users become the directory a suite authenticates against: mail, Matrix,
Gitea, Dex. Three facts about a user drive that, and the LDAP spike carried
them provisionally:

- the mail address the deployment hosts: carried in `email`
- the extra addresses that deliver to it: carried in `payload.body.mail_aliases`
- which services the user may use: carried in `groups`

All three were the wrong home. This document moves them into core fields and
gives them indexes that work on both engines.

## Fields

| Field | Type | Meaning |
|---|---|---|
| `mailbox` | string, optional | The address this deployment **hosts** for the user (`alice@imx.sh`). |
| `mail_aliases` | list of strings | Further addresses delivered to the mailbox (`postmaster@imx.sh`). |
| `services` | list of strings | Services the user may use (`mail`, `matrix`, `gitea`). |

### Why `mailbox` is not `email`

`email` is the user's **contact** address. It is verified by OTP, and it is
where password resets and notifications go. It is often somewhere else
entirely, like Gmail. The mailbox is an address the suite provides.
Conflating them is the bug matrix-deploy's README already documents: SSP's
reset mail went to `<uid>@imx.sh`, the very mailbox the user had forgotten
the password for. A user can have either, both, or neither.

### Why services are not groups

Groups already carry two meanings in dmart: team structure (`memberOf`) and
permission delegation (`grantable_by`). Overloading them with "may use mail"
means adding someone to a team silently grants them mail, and revoking mail
means editing team membership. A dedicated field keeps each change explicit
and auditable. Groups keep serving `memberOf`.

### Rules

- **Addresses** (`mailbox` and every alias):
  - Trimmed and lowercased.
  - Must pass the deployment's email format regex.
  - **Unique across every mailbox and alias of every live user**,
    case-insensitively. This is enforced by the database, not only by a
    pre-check.
  - If `USER_MAIL_DOMAINS` is set, the domain must be one of them.
  - Must not be another user's contact `email`. Otherwise LDAP `mail` lookups
    could match two people.
- **Aliases** require a mailbox, and an alias may not repeat the user's own
  mailbox.
- **Services**:
  - Lowercase slugs (`^[a-z][a-z0-9_-]{0,63}$`), deduplicated.
  - If `USER_SERVICES` is set, each must be one of them.
- **Who may set them.** Only through the managed API, under the ordinary user
  update permission. `/user/profile` never touches them: a user cannot grant
  themselves a service or an address.
- **Who may grant a service** (`USER_SERVICE_GRANTERS`), on the model of a
  role's `grantable_by`:
  - The setting is comma-separated `service:role` pairs, one per role that
    may grant that service: `mail:mail_admin,mail:helpdesk,gitea:dev_lead`.
  - A global admin may grant or revoke anything.
  - Anyone else may change only the services a role they hold is listed for.
    A service with no pair is global-admin only.
  - Only the **change** is checked. A `mail` delegate can turn mail on or off
    for a user who also has `gitea` without being trusted with gitea, and an
    update that does not name `services` is not a change.
  - Refusals are `NOT_ALLOWED`, naming the services.
  - The service catalogue (`USER_SERVICES`) is configuration, so its
    granters are too. Restricting who may set `mailbox` and `mail_aliases`
    is a permission's `restricted_fields`, as for any other field.
- **Soft delete releases them.** `mailbox`, `mail_aliases` and `services` are
  cleared with `email`/`msisdn`/`password`, so the addresses become available
  again.

## Storage

The three fields are columns on `users`: `mailbox TEXT`, and
`mail_aliases`/`services` as JSON arrays (`JSONB` on PostgreSQL, `TEXT` on
SQLite). The row stays self-contained for hydration, exports and history.

Two **derived index tables** sit beside it, identical on both engines:

```sql
user_addresses (
    address   TEXT PRIMARY KEY,                       -- lowercased
    shortname TEXT NOT NULL REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
    kind      TEXT NOT NULL CHECK (kind IN ('mailbox', 'alias'))
)  + INDEX (shortname)

user_services (
    service   TEXT NOT NULL,
    shortname TEXT NOT NULL REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED,
    PRIMARY KEY (service, shortname)
)  + INDEX (shortname)
```

### Why tables rather than indexes on the arrays

- **Uniqueness across rows of array elements cannot be expressed as an index
  on either engine.** A GIN index makes PostgreSQL's lookups fast but cannot
  stop two users holding the same alias. The `user_addresses` primary key can.
- **SQLite has no JSON index at all.** On SQLite every alias lookup and every
  service listing would be a full scan, and SQLite is a supported tier.
- **One mechanism for both engines** means one code path to test.

### What the indexes serve

| Query | Source | Plan |
|---|---|---|
| Postfix mailbox / Dovecot: `mail=x` | `user_addresses` (kind `mailbox`), then `users.email` for users without one | PK lookup, then the existing email index |
| Postfix alias: `mailAlias=x` | `user_addresses` (kind `alias`) | PK lookup |
| Gitea sync / listings: `authorizedService=gitea` | `user_services` | PK range scan in shortname order, read a page at a time |
| Uniqueness on write | `user_addresses` PK | constraint |

### Keeping them consistent

The tables are maintained by `UserRepository`, **in the same transaction as
the user write**, on every path that writes a user row:

- `UpsertAsync`, `UpsertManyAsync` and `UpsertWithPriorAsync`
- `RenameAsync`, which re-keys the rows
- `SoftDeleteAsync`, which clears the fields and deletes the rows
- `DeleteAsync` and `ForceDeleteAsync`

This is in code, not triggers, for the reason `Tombstones` gives:
`import --fast` disables triggers.

The rows are **derived from the stored row with SQL**, not from the C# model.
After the upsert, the sync reads `users.mailbox`, `mail_aliases`, `services`
and `is_deleted` back in the same statement. What the index holds is what the
row holds, even where the upsert's conflict clause overrides the model (it
pins `is_deleted`).

The foreign keys make drift a constraint error rather than a silent orphan: a
path that deletes or renames a user without the index rows fails at commit.

`DirectoryIndexRepair` rebuilds both tables from `users` at startup when they
are empty but some user has directory fields. That covers the upgrade, and
any restore that loaded `users` without them. The rebuild is the same SQL as
the per-row sync, with no shortname filter, in one all-or-nothing transaction
with no command timeout. The 3M-user scale run shaped three details:

- **The services insert uses `ON CONFLICT DO NOTHING`, not `DISTINCT`.**
  `DISTINCT` over 7M rows is a sort that spills to disk under a memory cap.
- **On PostgreSQL the foreign keys are dropped and re-added in the same
  transaction.** Kept in place, every row fires a check that takes
  `FOR KEY SHARE` on its `users` row: a random read and a dirtied page each.
  At 3M users that ran 18 minutes without finishing. Deferred instead, the
  same checks run at `COMMIT`, which is bound by the connection's 30-second
  timeout. Re-adding validates every row with one join: the whole rebuild
  took 118 s on PostgreSQL and 49 s on SQLite.
- **Failure is safe.** If the rebuild fails, `DirectoryIndexStatus` flips to
  not-ready, the rebuild retries every minute, and the LDAP face answers
  lookups that need the index with `unavailable`. "No such entry" would make
  Postfix bounce mail for real addresses with a permanent 550; `unavailable`
  makes it defer. `uid` lookups do not need the index and keep working.

### Clashes

The handler pre-checks uniqueness to give a readable error: `INVALID_DATA`
naming the address and noting that it is taken, without saying by whom. Two
concurrent writers claiming the same alias are settled by the primary key.
The loser's whole user write rolls back and surfaces as the same error.

## LDAP mapping

| LDAP | dmart |
|---|---|
| `mail` | `mailbox`, else `email` |
| `mailAlias` | `mail_aliases` |
| `authorizedService` | `services` |
| `memberOf` | `groups` (unchanged) |

Indexed attributes for the search planner are `uid`, `mail`, `mailAlias` and
`mobile` (point lookups), and `authorizedService` (a range read a page at a
time). A filter anchored on none of them still scans. Paged searches now read
a page at a time instead of materializing the result. `LDAP_MAX_SCAN` bounds
the rows examined **per request** (per page, for paged searches), so a
paged full listing continues across pages instead of failing.

## Exports

- **Parquet** gains the three columns. Restore reads archives without them as
  empty, so older backups still restore.
- **Zip** export/import carries them through `User`'s JSON, as `mailbox`,
  `mail_aliases` and `services`.
- Either way the index tables are rebuilt from the restored rows by the write
  path itself.

## Clients

- **Search.** `@services:gitea`, `@mail_aliases:help@example.org` and
  `@mailbox:alice@example.org` work in the query grammar, on the users table.
  Values are folded to lowercase like the stored ones. On PostgreSQL the two
  array columns have GIN indexes, as `roles` and `groups` do.
- **cxb.** The user form has a "Mail and services" section: the mailbox,
  aliases one per line, and services comma-separated.
- **Updates check only what changes.** An admin UI sends the whole record
  back on every save, so the rules above run only when a field's value
  differs from the stored one, as for `email` and `msisdn`. A mailbox that
  was valid when it was set does not block unrelated edits after
  `USER_MAIL_DOMAINS` narrows.
- **SDKs.** `dmart.Client` shares `Dmart.Models`, so `User` has the fields.
  tsdmart has a `UserDirectoryFields` type on the profile and on user records.
