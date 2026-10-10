# Directory replica

A read-only copy of a dmart's users and groups, kept in step over HTTP, so a
second host can answer LDAP lookups and binds on its own. The case it exists
for is the mail host:
- i7 delivers mail and authenticates IMAP and SMTP;
- the directory lives on i1, behind a home link;
- mail must keep working when i1 or that link is down.

Today i7 does this with an OpenLDAP replica fed by syncrepl. dmart's LDAP face
(`Ldap/`) cannot feed syncrepl, and the federated-sync design (PR #331) is a
different thing: it covers offline writes and never moves credentials. This
is the piece in between.

## Shape

```
 primary (i1)                                replica (i7)
 ───────────                                 ───────────
 users, groups  ──GET /managed/directory-feed──▶  users, groups (SQLite)
                     as a bot listed in                │
                     DIRECTORY_FEED_READERS            └─ LDAP face ◀── Dovecot, Postfix
```

- **Pull, not push.** The replica polls every `DIRECTORY_REPLICA_INTERVAL_SECONDS`
  (30 by default). The primary keeps no state about its replicas.
- **Read-only.** Users change on the primary. The replica's copy is
  overwritten from it; nothing flows back.
- **Credentials included.** The feed carries every user's password hash, so
  the replica verifies binds itself. That is what makes it useful when the
  primary is down, and it is why the feed is off unless a reader is listed.

## The feed

`GET /managed/directory-feed`, authenticated like any managed endpoint, and
answered only for the shortnames in the primary's `DIRECTORY_FEED_READERS`
that are active **bots**: a person given a listed name, or taking one a
delete freed, reads nothing. Anyone else gets 403, a global admin included.
Rows carry what a replica serves and no more: no payload, admin notes,
social-login ids, device or login history. It has two modes:

| Mode | Returns | Order and resume |
|---|---|---|
| `full` | every live user | shortname, bytewise; `after` |
| `changes&since=T` | every user whose row changed at or after T, soft-deleted ones included | `(updated_at, shortname)`; `after_time` + `after` |

The first page of either also carries the whole group list. The first page
of a changes walk carries the users hard-deleted or renamed away since T,
read from the `deletions` tombstones that every delete and rename writes,
along with the primary's clock (`server_time`) and its tombstone retention
floor.

Several writes had to change for `updated_at` to be a complete change log:
- a **soft delete** now stamps `updated_at`;
- a **rename** stamps it with the host's clock, not PostgreSQL's `NOW()`,
  which runs in the database server's timezone;
- **`dmart passwd`** and the **legacy-lockout repair** stamp it too, and so
  does the replacement of an imported `{SSHA}` with Argon2id at its owner's
  first sign-in. An ordinary rehash (new Argon2 parameters) does not: it
  changes nothing a replica serves differently.

**Bytewise order.** The full walk resumes after the last shortname, and the
replica reconciles each page against its own rows in the same range. Both
ends compare shortnames bytewise (`COLLATE "C"` on PostgreSQL, served by
`idx_users_shortname_bytes`; SQLite's default), so a PostgreSQL primary and
a SQLite replica agree on what lies between two names. With PostgreSQL's
default collation 'Bob' sorts between 'alice' and 'carol', SQLite puts it
first, and a replica comparing the two deleted the users in between.

## The replica

`Services/DirectoryReplica.cs`, enabled by `DIRECTORY_REPLICA_OF`:

1. **First sync, a full walk.** Each page is applied, and the local rows that
   fall between the previous page and this one but are missing from it are
   removed. Memory stays at one page whatever the directory's size.
2. **Every poll after, a changes walk** from the watermark minus 65 minutes.
   A write stamps `updated_at` before it commits, and the overlap catches one
   that commits late; it is over an hour because dmart stamps host-local
   time, and when the primary's clock falls back at the end of daylight
   saving, the next hour's writes carry stamps from before the watermark.
   Deletions are applied first, then rows, then groups (a group's owner must
   be a local user; one the replica lacks is replaced by the replica's own
   account). Rows the replica already has, unchanged, are skipped, so the
   overlap costs a comparison per row, not a write.
3. **Back to a full walk** when the primary's tombstones no longer reach the
   watermark (a pruned deletions table).

The watermark is the primary's `server_time` at the start of the last
complete walk, kept in `directory_replica_state` with `synced_at`, the
replica's own clock at that walk.

Rows are written exactly as the primary has them: `updated_at` (which LDAP
serves as `modifyTimestamp`), uuid, and the password hash, including its
absence. Two things stay local:
- **The lockout counters.** A bind on the replica counts against the
  replica's copy; the feed does not carry them.
- **Hashes are not upgraded on the replica.** A bind there leaves an
  imported `{SSHA}` as it is; the primary replaces it at the owner's next
  sign-in there, and the replica picks that up.
- **Rows the replica's own bootstrap created.** Its admin owns its local
  management space. A user the primary no longer has, but who owns local
  rows, is soft-deleted rather than deleted.

## Failure behaviour

- **Before the first sync completes**, every LDAP lookup and bind answers
  `unavailable` (52). Postfix defers mail rather than bouncing it for an
  address the replica has not heard of yet, and a mail client is not told
  its password is wrong.
- **After that, a failed poll only logs.** The replica keeps serving its copy
  and says how old it is. The poll loop survives any exception, because an
  exception escaping a hosted service would stop the host and take the LDAP
  face with it.
- **A restart while the primary is down** serves the copy on disk at once,
  as old as `synced_at` says.
- **Too old to vouch for a password.** Once the last sync is older than
  `DIRECTORY_REPLICA_MAX_STALENESS_HOURS` (default 24; 0 never), binds answer
  `unavailable`: the copy may still accept a password the primary has
  changed, or an account it has deleted. Lookups keep answering, so mail
  keeps flowing to the mailboxes the replica knows. `/health/ready` answers
  503 until a sync succeeds, and while ready reports
  `directory_replica_synced_at`.
- **Lag** is the poll interval plus the time a walk takes. A password changed
  on the primary works on the replica after the next poll. The old password
  stops working there at the same moment, except in Dovecot's auth cache
  (see matrix-deploy's `roles/dovecot/defaults/main.yml`).

## Configuration

On the primary:

```
DIRECTORY_FEED_READERS="i7replica"          # bot users that may read the feed
```

On the replica:

```
DIRECTORY_REPLICA_OF="http://10.77.0.2:8282"   # over WireGuard, or https
DIRECTORY_REPLICA_SHORTNAME="i7replica"
DIRECTORY_REPLICA_PASSWORD="..."
DIRECTORY_REPLICA_INTERVAL_SECONDS=30
DIRECTORY_REPLICA_MAX_STALENESS_HOURS=24       # then binds answer unavailable
LDAP_PORT=389                                  # plus the usual LDAP_* settings
LISTENING_HOST="127.0.0.1"                     # its HTTP API has nothing to offer
```

Keep the replica's HTTP API on loopback. Users edited there would be
overwritten by the next change on the primary.

## Verified

- **Integration tests, on both engines.** The primary runs on the leg's
  engine and the replica on its own SQLite file. They cover:
  - the first sync: hashes, directory fields, groups, an LDAP bind;
  - a change, a soft delete, a hard delete and a rename on the primary;
  - lockout counters staying local;
  - the feed refusing a global admin, anonymous callers and a listed person,
    and leaving out what a replica does not serve;
  - an unsynced replica answering `unavailable`;
  - a restart serving the copy on disk, and binds refused once it is older
    than the limit;
  - bytewise keyset order on both engines;
  - a group whose owner the replica does not have.
- **`bench/directory-replica-smoke.sh`**, two real processes with Dovecot 2.4
  and Postfix against the replica:
  - the first sync, then an IMAP login and an alias lookup;
  - a password changed on the primary through `/user/profile`, then the new
    one working and the old one refused on the replica;
  - the primary killed, then logins and lookups still working.

## Not done

- **Deployment.** i7 would need a dmart instance with the LDAP face, and
  matrix-deploy a role for it. The LDAP face is not in production on i1 yet
  either.
- **More than one primary.** A replica follows exactly one.
