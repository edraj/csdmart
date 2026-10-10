# An LDAP face on dmart: spike report (2026-10-10)

The question: can dmart **own** the user directory for a suite of dmart, mail,
Matrix, Gitea and Dex, and serve it to them over a thin, read-only LDAP layer,
so that OpenLDAP is no longer needed?

Short answer: **the protocol layer really is thin, and every real client
worked against it unchanged.** It is not yet shown to serve a multi-million
directory on modest hardware. The scale run below was too generous to show
that, and it found gaps: the mail-alias lookup and full listings are
unindexed. Those and the product gaps are listed at the end; none of them is
a protocol problem.

Reproduce with the two scripts beside this file. Re-run them rather than
trusting these numbers on other hardware.

```
bench/ldap-face-interop.sh "dotnet bin/Release/net10.0/dmart.dll"   # or the AOT binary
bench/ldap-face-scale.sh <dmart binary> sqlite 1000000 4,5
PG_CONN_ARGS="127.0.0.1 55432 dmart <pw> <throwaway db>" bench/ldap-face-scale.sh <dmart binary> pg 1000000 4,5
```

## What was built

`Ldap/` is 1,828 lines, about 1,430 of them code rather than comment, and it
has no new dependencies. BER comes from `System.Formats.Asn1`, which ships
with .NET, and the Native AOT publish is clean.

| Supported | Behaviour |
|---|---|
| Simple bind | Checked by `UserService.VerifyDirectoryBindAsync`, the credential half of `/user/login`: the same Argon2 memory budget, lockout counter, deactivation gate, rehash-on-login and decoy timing, but no session or JWT |
| Search | All three scopes and the full RFC 4511 filter grammar, with three-valued logic, attribute aliases, case-insensitive, DN-valued, boolean and telephone matching, and `*` / `+` / `1.1` attribute selection |
| Paged results (RFC 2696), Who Am I (RFC 4532), root DSE | Yes |
| Add, modify, delete, modify-DN, compare, password modify | Refused with `unwillingToPerform` (53). Users are changed through dmart |
| StartTLS / LDAPS | **Not yet**. StartTLS answers `unavailable` (52) |
| SASL | `authMethodNotSupported` (7) |

The tree:

```
<LDAP_BASE_DN>                        dcObject / organization
├── ou=people    uid=<shortname>      every live user except service accounts
├── ou=groups    cn=<group>           groupOfNames; member = users in the group
└── ou=services  cn=<name>            LDAP_SERVICE_ACCOUNTS
```

A user entry carries `inetOrgPerson` plus `dmartPerson`/`dmartUser`, the
schema Python dmart's `ldap_manager` plugin shipped, plus any
`LDAP_EXTRA_USER_OBJECT_CLASSES` (`freexPerson,freexUser` for matrix-deploy).
It has these attributes:

- `uid`, `cn`/`displayName`, `sn`, `givenName`, `mail`, `mobile`, `preferredLanguage`
- `isActive`: TRUE only for an active, non-deleted user
- `authorizedService` and `memberOf`, both from the user's **dmart groups**
- `mailAlias`, from `payload.body.mail_aliases`
- `entryUUID` and the timestamps

`userPassword` is never served.

Access works like this:

- **Service accounts** read everything.
- **Any other bind** reads only its own entry, the equivalent of `by self read`.
- **Anonymous** gets the root DSE and nothing else. Anonymous *bind* succeeds
  so that `ldapsearch -x` and root-DSE probes work, but it grants nothing more.

## Interop: 33 of 33, on both the JIT and the AOT binary

Every client ran in a container from the packages matrix-deploy actually
deploys, pointed at dmart with **the filters in matrix-deploy's own templates,
unchanged**:

| Client | Version | What was exercised |
|---|---|---|
| libldap (`ldapsearch`, `ldapwhoami`, `ldapmodify`) | openldap-clients 2.6.14 | root DSE, bind outcomes (49 / 53), the Dex filter, self-only visibility, anonymous refusal (50), paged search, `member=` group lookup, `memberOf`, noSuchObject (32), write refusal (53) |
| Postfix `postmap -q` | postfix-ldap 3.10, Fedora 43 | the mailbox map (`result_format = %s/`) and the alias map (`mailAlias=%s`) |
| Dovecot `doveadm auth test` | 2.4, Fedora 43 | `passdb ldap { bind = yes }` with i7's filter |
| Dex | v2.45.1 (i1's pin) | the LDAP connector config from `roles/dex`, via a password grant: ID token for alice; deactivated and unauthorized users refused |
| Gitea | 1.27.3 (i1's pin) | `gitea admin auth add-ldap` with `roles/gitea`'s flags, then API basic auth: the account is created from the directory; unauthorized users get 401 |

The only failures in the first run were two checks of mine that expected the
wrong casing in Dex's error text.

One assumption was wrong and is now pinned by a test: libldap 2.6 sends
**minimal** BER lengths, not the four-byte long form older liblber used.
`LdapCodecTests` carries libldap's actual bytes for a bind and for the Dex
search. The decoder still reads BER rather than DER, so the long form is
accepted too.

## Tests

- **36 unit tests** (`dmart.Tests/Unit/Ldap/`): DN parsing and escaping; the
  filter decoder and evaluator, including three-valued negation, the
  nesting-depth limit and anchor extraction; and the codec against the
  captured libldap bytes.
- **6 integration tests** (`dmart.Tests/Integration/LdapFaceTests.cs`) on a
  real host over a socket. They passed on **both** CI legs (SQLite, and
  PostgreSQL 17 in a throwaway container) and cover:
  - bind outcomes, and the lockout counter rising on a bad bind and clearing on a good one
  - lookup by uid and by mail
  - the keyset scan and the group-membership JSON query, the two queries whose SQL differs per engine
  - self-only visibility
- **Full suite on SQLite**: 2,918 passed, 0 failed.

## Scale: 1,000,000 users

Hardware: an Intel Core Ultra 7 365 laptop, used as a stand-in for small
hardware:

- dmart, the v1.5.22+spike AOT build, is pinned to **two E-cores** (3.6 GHz max, `taskset -c 4,5`).
- PostgreSQL 17 runs in a container with its postmaster pinned to the other two E-cores.
- The Python load generator shares those same four cores, so **every figure is a floor**.

Users were cloned from one API-created template in about 6 s on SQLite and
27 s on PostgreSQL. Each has an email, two groups and one mail alias. Every
search is the exact filter of the client named.

**What this run does not establish.** It shows the LDAP layer is not the
bottleneck for indexed lookups at 1M users. It does **not** show that
dmart serves millions of users on modest hardware. The conditions were
generous:
- The machine has 62 GB of RAM, so both databases were fully cached.
- The SQLite file sat on tmpfs, i.e. in memory.
- Each workload ran alone for 10 seconds, with no ordinary dmart traffic
  alongside and no soak.
- 1M users, not several million.

| Workload (client) | Connections | SQLite | PostgreSQL |
|---|---|---|---|
| `uid=` lookup (Dex / Gitea) | 1 | 6,386/s · p50 0.13 ms · p99 0.44 ms | 4,748/s · p50 0.17 ms · p99 0.79 ms |
| `uid=` lookup (Dex / Gitea) | 8 | 13,505/s · p50 0.47 ms · p99 2.80 ms | 16,205/s · p50 0.42 ms · p99 1.35 ms |
| `mail=` lookup (Postfix mailbox, Dovecot) | 8 | 12,778/s · p99 3.08 ms | 16,407/s · p99 1.31 ms |
| bind (Dovecot `bind = yes`) | 8 | 83/s · p50 97 ms | 87/s · p50 92 ms |
| `mailAlias=` lookup (Postfix alias map) | 1 | **fails**: 1.9 s, then `adminLimitExceeded` | **fails**: 1.6 s, then `adminLimitExceeded` |
| Server memory after all of the above (PSS) | | 552 MiB (258 MiB own heap) | 203 MiB (164 MiB own heap) |

For comparison, an earlier SQLite run on two **P-cores** gave 20,706/s for
`uid=` with 8 connections and 125 binds/s.

How to read it:

- **Indexed lookups are not the bottleneck.** `uid`, `mail` and `mobile` go
  through the same indexes `/user/login` uses. Tens of thousands of lookups a
  second on two small cores is far beyond what a mail server or an IdP asks of
  a directory.
- **Binds cost what logins cost.** 83–87/s on two E-cores is the Argon2
  budget at OWASP parameters (19 MiB, t=2), not LDAP overhead. It is the same
  ceiling `/user/login` has on this hardware. slapd with `{ARGON2}` hashes
  would pay the same per bind, without dmart's memory budget.
- **The alias map is the clearest gap.** `mailAlias` lives in
  `payload.body`, which no index covers. Every unindexed search walks the
  users table in keyset pages, and `LDAP_MAX_SCAN` (100,000) stops it.
  Postfix consults `virtual_alias_maps` for **every recipient**, so at this
  scale alias delivery breaks. The fix was measured by hand on the same
  1M-row PostgreSQL table:

  ```sql
  CREATE INDEX ... ON users USING GIN ((payload->'body'->'mail_aliases') jsonb_path_ops);
  -- 189 ms parallel seq scan  →  0.067 ms bitmap index scan; 34 MB index, built in 2.7 s
  ```

  The face then needs to treat `mailAlias` as an indexed attribute and push
  it into SQL. SQLite has no JSON index, so it would need a small
  `user_mail_aliases(alias, shortname)` side table maintained on write.
- **Unindexed scans are about 10× slower per row than the database could
  be.** Each scanned row is hydrated as a full `User` and filtered in C#: 100k
  rows took 1.6–1.9 s, while PostgreSQL's own sequential scan covered all
  1M in 189 ms. Pushing simple equality clauses into the WHERE clause would
  help every unindexed filter, not only aliases.
- **RSS overstates memory; read PSS.** With SQLite, every pooled connection
  maps the same database file (`mmap_size` 256 MiB), and RSS counts each
  mapping. One run showed 1.8 GiB RSS against 418 MiB PSS. The scale script
  reports PSS.

## What the spike settles, and what it leaves open

**Settled:**
- The protocol subset the suite needs is small, and it is implementable
  inside dmart with no dependencies.
- Real clients accept it without configuration changes beyond the host and
  bind DN.
- Binds can share dmart's credential machinery, including the lockout. An
  LDAP client is not a way around `/user/login`'s defences.

**Open, roughly in order:**

1. **Index `mailAlias`** (above) before any deployment with more than a few
   thousand users relies on the Postfix alias map.
2. **TLS.** LDAPS and StartTLS. Until then, loopback or WireGuard only, as
   the setting's comment says.
3. **Per-IP bind rate limiting.** The account lockout applies, but the HTTP
   side's per-IP `auth-by-ip` limiter has no LDAP equivalent yet.
4. **Replication.** i7 authenticates mail against a *local* OpenLDAP replica
   so mail survives i1 or the home link being down. dmart cannot feed
   syncrepl, and the federated-sync design (PR #331) does **not** cover this.
   That design is for offline writes and deliberately never syncs
   credentials, while a directory replica exists to verify passwords
   locally. This needs its own read-only, credential-carrying mechanism. The
   likeliest shape is a dmart replica that pulls changed identity rows
   (users, services, addresses) from the primary over WireGuard, keeps them in
   local SQLite and serves LDAP and binds. The building blocks exist
   (`updated_at`, `deleted_at`, the Parquet tombstones), with two catches:
   - a soft delete sets `deleted_at` but not `updated_at`
   - rehash-on-login deliberately leaves `updated_at` alone; that one is
     harmless, because the old hash verifies the same password
5. **Password self-service.** SSP changes passwords over LDAP, which the
   face refuses by design. dmart's own reset flow (`/user/otp-request`,
   `/user/password-reset-confirm`) would replace SSP.
6. **Product decisions taken provisionally:**
   - `authorizedService` = dmart groups
   - `mailAlias` = `payload.body.mail_aliases`
   - service accounts named in config rather than by role
   - anonymous bind allowed but powerless
7. **Full listings are untested at scale.** Gitea's `--synchronize-users`
   (which matrix-deploy enables) sends its user filter with `%s` replaced by
   `*`: an unindexed listing of every Gitea user. At 1M users that hits the
   500-entry size limit unpaged, or `LDAP_MAX_SCAN` paged. Paged searches
   also materialize the whole result set rather than reading a page at a time.
8. **`mail` conflates two addresses.** The face serves dmart's `email` (the
   verified contact address used for one-time codes and resets) as LDAP
   `mail`. A suite that hosts mail needs the hosted mailbox there, which is a
   different address for anyone whose contact address is elsewhere.
9. **Smaller items:**
   - There is no subschema entry, which some GUI browsers want.
   - Time limits are ignored.
   - Group `member` lists cost one query per group.
