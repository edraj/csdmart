# An LDAP face on dmart (spike report, 2026-10-10)

The question: can dmart **own** the user directory for a suite of dmart, mail,
Matrix, Gitea and Dex, and serve it to them over a thin, read-only LDAP layer,
so that OpenLDAP is no longer needed?

Short answer:

- **The protocol layer is thin, and every real client worked against it
  unchanged.**
- **3,000,000 users is sustainable on two small cores and a couple of
  gigabytes**, now that a user's mailbox, aliases and services are core,
  indexed fields:
  - It held a ten-minute soak of mixed traffic on either engine, with no
    degradation, no errors and flat memory.
  - That traffic was Postfix, Dovecot, Dex and Gitea plus ordinary API calls,
    all at once.
- **What that still does not establish** is listed at the end: the CPU and
  disk are faster than a cheap server's, the load is synthetic, and the run is
  minutes, not weeks.

Reproduce with the scripts beside this file. Re-run them rather than trusting
these numbers on other hardware.

```
bench/ldap-face-interop.sh <dmart binary>          # real clients, 34 checks
CORES=4,5 MEM_DMART=1536M \
  bench/ldap-face-scale.sh <dmart binary> sqlite 3000000 600
CORES=4,5 MEM_DMART=768M MEM_PG=1g PG_CORES=6,7 \
  bench/ldap-face-scale.sh <dmart binary> pg 3000000 600
```

## What was built

### The LDAP face

`Ldap/` has no new dependencies; BER comes from `System.Formats.Asn1`, which
ships with .NET. The Native AOT build is clean, and every number below is from
the AOT binary.

| Supported | Behaviour |
|---|---|
| Simple bind | `UserService.VerifyDirectoryBindAsync`, the credential half of `/user/login`: the same Argon2 memory budget, lockout counter, deactivation gate, rehash-on-login and decoy timing, but no session or JWT. A wrong password the account failed with recently is not counted again (see below) |
| Failed-bind limit | `AUTH_RATE_LIMIT_PER_MINUTE` failed binds a minute per client address, the number the HTTP login endpoints allow; then `busy` (51) before any password is checked. `LDAP_TRUSTED_PEERS` (loopback by default) are exempt |
| Search | All scopes and the full RFC 4511 filter grammar: three-valued logic, attribute aliases, case-insensitive, DN-valued, boolean and telephone matching, and `*` / `+` / `1.1` attribute selection |
| Paged results (RFC 2696) | **Streamed**: each page reads only the rows it needs, and the scan budget applies per request, so a full listing continues across pages |
| Who Am I (RFC 4532), root DSE, subschema (`cn=Subschema`) | Yes |
| Size and time limits | Size: `LDAP_SIZE_LIMIT` for unpaged searches. Time: the client's, ending with `timeLimitExceeded` |
| Add, modify, delete, modify-DN, compare, password modify | `unwillingToPerform` (53). Users change through dmart |
| StartTLS, LDAPS | With `LDAP_TLS_CERT_FILE`/`LDAP_TLS_KEY_FILE`; `LDAPS_PORT` for implicit TLS. The files are re-read when they change. A cleartext password bind from outside `LDAP_TRUSTED_PEERS` is then refused with `confidentialityRequired` (13) |
| SASL | `authMethodNotSupported` (7) |

The tree:

```
<LDAP_BASE_DN>                        dcObject / organization
├── ou=people    uid=<shortname>      every live user except service accounts
├── ou=groups    cn=<group>           groupOfNames; member = users in the group
└── ou=services  cn=<name>            LDAP_SERVICE_ACCOUNTS
```

A user entry has:

- these objectClasses: `inetOrgPerson`, `dmartPerson`, `dmartUser`, and any
  `LDAP_EXTRA_USER_OBJECT_CLASSES` (`freexPerson,freexUser` for matrix-deploy)
- these attributes, mapped from the user's core directory fields:

| LDAP | dmart user |
|---|---|
| `mail` | `mailbox`, else the contact `email` |
| `mailAlias` | `mail_aliases` |
| `authorizedService` | `services` |
| `memberOf` | `groups` |
| `isActive` | active and not deleted |

`userPassword` is never served. Service accounts read everything; any other
bind reads only its own entry; anonymous gets only the root DSE.

### Stale passwords and the lockout

Binds share `/user/login`'s lockout counter: `MAX_FAILED_LOGIN_ATTEMPTS`
wrong passwords lock the account, and every attempt while locked refreshes
the cool-down. Testing Dovecot's auth cache against the face showed what that
does to a mail user. A phone still holding the old password after a change
retries it every time it reconnects, so it locks the account within five
retries, and keeps it locked, web login included, for as long as the phone
keeps trying.

So a failed bind whose password matches one of the account's last two failed
passwords is not counted again. A guesser gains nothing: a repeated wrong
guess is still wrong, and distinct guesses count as before. Only an HMAC of
each failed password is kept, in memory, under a key that dies with the
process (`Ldap/LdapBindGuard.cs`). Dovecot's negative cache already limits
such a client to one bind per `auth_cache_negative_ttl`. This makes those
binds harmless as well as rare.

### The directory fields

See `docs/user-directory-fields.md`.

- **The fields.** `mailbox`, `mail_aliases` and `services` are core user
  fields. They are set only through `/managed/request`, and only under the
  usual user-update permission.
- **The indexes.** Two tables, `user_addresses` and `user_services`, index
  them on both engines. They are maintained in the same transaction as every
  user write.
- **Uniqueness.** Addresses are unique across all users, case-insensitively,
  and the database enforces it, so a racing writer loses its whole write.
- **Rebuild.** The index is rebuilt at startup when it is empty. Until it is
  ready, lookups that need it answer `unavailable` rather than "no such
  user", which makes Postfix defer mail instead of bouncing it.

## Interop: 42 of 42, on both the JIT and the AOT binary

Every client ran in a container from the packages matrix-deploy deploys,
pointed at dmart with **the filters in matrix-deploy's own templates,
unchanged**:

| Client | Version | What was exercised |
|---|---|---|
| libldap (`ldapsearch`, `ldapwhoami`, `ldapmodify`) | openldap-clients 2.6.14 | root DSE, bind outcomes (49 / 53), the Dex filter, self-only visibility, anonymous refusal (50), paged search, `member=` group lookup, `memberOf`, noSuchObject (32), write refusal (53) |
| Postfix `postmap -q` | postfix-ldap 3.10, Fedora 43 | the mailbox map (`result_format = %s/`) and the alias map (`mailAlias=%s`); a contact email is not a local mailbox |
| Dovecot `doveadm auth test` | 2.4, Fedora 43 | `passdb ldap { bind = yes }` with i7's filter |
| TLS | the same clients | LDAPS and StartTLS (`-ZZ`) from libldap, a paged listing over StartTLS, Postfix's map over LDAPS, Dovecot over StartTLS (and failing in `ldap_start_tls_s()` when it trusts the wrong CA, which proves the passing run was encrypted), and a client that does not trust the CA refusing to connect. The CA is a throwaway one made per run |
| Dex | v2.45.1 (i1's pin) | the LDAP connector from `roles/dex` **over LDAPS** (Go's TLS stack, where the others use OpenSSL), via a password grant; the ID token carries the **hosted mailbox** |
| Gitea | 1.27.3 (i1's pin) | `gitea admin auth add-ldap` with `roles/gitea`'s flags, then API basic auth; the account gets the hosted mailbox |

libldap 2.6 sends minimal BER lengths; `LdapCodecTests` pins its actual
bytes. The decoder reads BER rather than DER, so the long form is accepted too.

## Tests

- **Unit**:
  - 36 for the LDAP face: DNs, the filter decoder and evaluator, and the codec
    against captured libldap bytes
  - 14 for address and service normalization
- **Integration**, over real hosts, on **both** engines:
  - 9 for the face: lookups through each index, the contact-email fallback,
    the service listing, the scan budget and streamed paging, a not-ready
    index answering `unavailable`, binds and the lockout, visibility
  - 10 for the fields: normalization, clashes (case-insensitive, against
    contact emails), a racing writer, allowlists, updates releasing old
    aliases, soft delete, rename, hard delete, the rebuild, and that
    `/user/profile` cannot grant anything
- **Full suites**: SQLite 2,945 passed and PostgreSQL 2,975 passed, 0 failures.

## Scale: 3,000,000 users on two E-cores

### Conditions

Chosen to look like a small server, not a developer laptop:

- **CPU.** dmart is pinned to **two E-cores** (Intel Core Ultra 7 365, 3.6 GHz
  max). PostgreSQL's postmaster and its backends are pinned to two others.
  The Python load generators share those four cores, so **every figure is a
  floor**.
- **Memory**, as cgroup caps with no swap:
  - SQLite: dmart at **1.5 GB**, which has to hold the database's cache too.
    The file is 2.9 GB, so it does not fit.
  - PostgreSQL: dmart at **768 MB**, and PostgreSQL at **1 GB** with
    `shared_buffers=256MB`. The database is 3.8 GB.
- **Disk.** The databases sit on disk (NVMe), not tmpfs.
- **Data.** 3M users, each with:
  - a contact email and a hosted mailbox, at different domains
  - one alias
  - services `mail` + `matrix`, and every third user also `gitea` (1M Gitea users)
- **Workloads.** Every search is the exact filter of the client named.
  Lookups pick users uniformly at random, with no caching help from skew.

### Index rebuild at startup

The users were cloned in SQL without index rows, so the first constrained
start had to rebuild **13M index rows** (6M addresses, 7M service grants):

| | SQLite | PostgreSQL |
|---|---|---|
| Rebuild | 41 s | 118 s |

### Each workload alone (8 connections unless noted)

| Workload (client) | SQLite | PostgreSQL |
|---|---|---|
| `uid=` lookup, 1 connection (Dex / Gitea) | 5,535/s · p99 0.41 ms | 667/s · p99 3.7 ms |
| `uid=` lookup (Dex / Gitea) | 11,563/s · p99 3.2 ms | 9,290/s · p99 2.0 ms |
| `mail=` lookup (Postfix mailbox, Dovecot) | 10,456/s · p99 3.4 ms | 7,526/s · p99 2.4 ms |
| `mailAlias=` lookup (Postfix alias) | 10,398/s · p99 3.4 ms | 6,890/s · p99 2.6 ms |
| bind (Dovecot `bind = yes`) | 75/s · p50 106 ms | 76/s · p50 104 ms |
| dmart `GET /user/profile`, 4 connections | 3,463/s · p99 3.1 ms | 2,744/s · p99 2.8 ms |
| full paged listing of 1M Gitea users, 500 per page | 32 s | 39 s |

### Everything at once, for 10 minutes

Six clients ran concurrently: Postfix (mailbox and alias maps), Dex/Gitea
logins, Dovecot binds, Gitea's paged user sync, and dmart's own API.

| Workload | Connections | SQLite | PostgreSQL |
|---|---|---|---|
| `mail=` (Postfix mailbox) | 4 | 1,822/s · p99 15.7 ms | 1,163/s · p99 13.2 ms |
| `mailAlias=` (Postfix alias) | 2 | 912/s · p99 15.7 ms | 552/s · p99 13.5 ms |
| `uid=` (Dex / Gitea) | 2 | 946/s · p99 15.3 ms | 641/s · p99 12.5 ms |
| bind (Dovecot) | 2 | 25/s · p99 157 ms | 42/s · p99 81 ms |
| `GET /user/profile` (app traffic) | 2 | 456/s · p99 22 ms | 244/s · p99 22 ms |
| Gitea sync, 1M users per listing | 1 | 4 complete, 126 s each | 7 complete, 77 s each |
| dmart memory (PSS / own heap) | | 738–768 / 443–473 MiB, under the 1.5 GB cap | 350–429 / 313–391 MiB, under the 768 MB cap |
| PostgreSQL memory | | | ~398 MB, under the 1 GB cap |
| Errors logged | | 0 | 0 |

**Every per-minute rate stayed within a few percent of the mean** (e.g. SQLite
`mail=` 1,806–1,873/s, PostgreSQL 1,130–1,202/s), and memory did not grow
across the ten minutes.

### How to read it

- **The index fixes are what make 3M work.** The first run below found the
  Postfix alias map failing outright at 1M users. Here alias lookups run as
  fast as every other indexed lookup, and a full listing of a million users
  completes in tens of seconds instead of hitting the scan cap.
- **The engines trade differently.**
  - SQLite runs inside dmart's two cores. It has the lowest per-lookup
    latency, but it competes with Argon2 for those cores, which is why its
    binds fall to 25/s under mixed load.
  - PostgreSQL works on its own cores. It is slower per lookup (a network
    round trip each), but binds keep more of the CPU.
- **Binds are the expensive operation, by design.** About 76/s on two cores
  alone, and 25–42/s while serving everything else, is the Argon2 cost at
  OWASP parameters. That is the same ceiling `/user/login` has, and the same
  cost slapd would pay for `{ARGON2}` hashes, except slapd has no memory
  budget for it.
  - Sustained 25–42/s is 90k–150k logins an hour.
  - A mail server's IMAP clients reconnect often, so production should enable
    Dovecot's auth cache rather than spend an Argon2 verify on every
    reconnect. i7 does not enable it today.
- **Rough demand arithmetic.** These are estimates, not measurements.
  - Postfix consults the mailbox and alias maps per recipient.
  - About 2,700 such lookups a second (SQLite, under mixed load) is on the
    order of a thousand recipients a second, far more than a two-core mail
    host could deliver anyway.

### What the scale run found and fixed

The first PostgreSQL attempts **reported 0 entries** for every address lookup.
The startup rebuild had failed, and the server came up anyway. Three things
needed fixing:

1. **The rebuild had a timeout.** Npgsql's default is 30 s; 13M rows take
   longer. The rebuild now runs with no command timeout, in one
   all-or-nothing transaction.
2. **The foreign keys made it pathological on PostgreSQL.**
   - Checked per row, each insert took `FOR KEY SHARE` on its `users` row:
     after 18 minutes it had read 31 GB and written 10 GB, and was still
     going.
   - Deferred, the same 13M checks run at `COMMIT`, which is bound by the
     connection's 30-second timeout.
   - The rebuild now drops the two foreign keys and re-adds them in the same
     transaction, which validates them with one join: 118 s in total.
   - A `DISTINCT` that spilled a 7M-row sort to disk became
     `ON CONFLICT DO NOTHING`.
3. **A failed rebuild looked like "no such user".** For Postfix that is a
   permanent 550, a bounce. Lookups that need the index now answer
   `unavailable` until it is ready, so Postfix and Dovecot defer, and the
   rebuild retries every minute.

All three came from running at scale with caps. None of them showed up in the
tests, where the data is small.

### An earlier, generous run (superseded)

At 1M users, unconstrained (62 GB of RAM, SQLite on tmpfs), with aliases in
`payload.body` and services as groups:

- indexed lookups were fast
- the Postfix alias map failed with `adminLimitExceeded` after a 1.6–1.9 s scan
- the Gitea-style full listing could not complete

Those two findings drove the core fields and the streamed paging above. That
run's details are in this file's git history.

### What this still does not establish

- **Real small hardware.** The cores are 2026 laptop E-cores and the disk is
  NVMe. A cheap VPS or an ARM board has slower cores and much slower disk, so
  cache misses there cost more. The caps limit memory, not speed.
- **Real traffic.** The access pattern is uniform-random. Real traffic is
  skewed, which usually helps caches, but it was not measured. No user writes
  ran during the soak.
- **Duration and size.** Ten minutes, not days. 3M users, not 10M.
- **The network.** Clients connected over loopback. A real deployment adds a
  network hop per lookup, and a TLS handshake per connection.
- **Repeatability.** One run per engine for each configuration (the SQLite one
  twice, with matching results).

## Still open

1. ~~**TLS.**~~ Done: LDAPS and StartTLS, checked with libldap, Postfix,
   Dovecot and Dex (Go's TLS) in the interop run.
2. ~~**Per-IP bind rate limiting.**~~ Done, with the stale-password rule below.
3. **Replication.** i7 authenticates mail against a *local* replica so mail
   survives i1 or the home link being down.
   - dmart cannot feed syncrepl.
   - The federated-sync design (PR #331) does **not** cover this: it is for
     offline writes and never syncs credentials.
   - Proposed instead: a read-only dmart replica that pulls changed identity
     rows (users, the index tables) from the primary over WireGuard, keeps
     them in local SQLite, and serves LDAP and binds.
   - The building blocks exist: `updated_at`, `deleted_at`, the Parquet
     tombstones. Two catches: a soft delete sets `deleted_at` but not
     `updated_at`, and rehash-on-login leaves `updated_at` alone (harmless,
     since the old hash verifies the same password).
4. ~~**Password self-service.**~~ Done: dmart's change and reset flows
   replace SSP. A reset may name the account by its hosted mailbox; the code
   goes to the contact channel. See `docs/user-directory-fields.md`.
   Not done: importing `{SSHA}` hashes from OpenLDAP.
5. ~~**Per-service delegation.**~~ Done: `USER_SERVICE_GRANTERS`.
6. ~~**cxb, SDKs and query grammar**~~ Done. tsdmart's types wait on a
   release of that package.
7. **matrix-deploy settings:**
   - Gitea's `add-ldap` sets no page size. An unpaged sync of more than 500
     users hits the size limit here, as it would on slapd's defaults, so add
     `--page-size 500`.
   - Enable Dovecot's auth cache (see binds above).
8. ~~**Smaller items**~~ Done:
   - a subschema entry at `cn=Subschema`, named by the root DSE. It defines
     everything the face serves, with the freex definitions copied from
     matrix-deploy, OIDs included
   - time limits end a search with `timeLimitExceeded`. Scans now hand back
     control between pages of rows, so a limit holds even when nothing matches
   - group `member` lists are loaded only when the answer needs them. Dex's
     and Gitea's `(member=<user DN>)` for `cn` reads only that user's row.
     When every list is needed, it is one query for all the groups, not one
     per group (each of which was a full scan on SQLite)
