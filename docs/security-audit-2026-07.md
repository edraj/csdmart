# Security & Performance Audit — July 2026

**Scope:** full feature-by-feature audit of csdmart — auth/sessions, RBAC/ACL, managed
API + entry lifecycle, query/search + grammar, SQL data layer, attachments/import/export,
HTTP pipeline, public/OAuth/MCP surface, realtime/plugins/workflow, startup/config,
the cxb SPA, and the CLI/SDK.

> The 4 findings from the earlier authn/authz pentest (inactive-role permissions, forged
> token vs session binding, null byte in shortname, ACL deny precedence) are all fixed —
> that suite, `SecurityPenetrationTests`, passes 77/77. This report covers the ground
> beyond it.

---

## Verification baseline

| | Before | After |
|---|---|---|
| `SecurityPenetrationTests` | 77 / 77 pass | 77 / 77 pass |
| Full suite | 1672 pass | **1673 pass, 0 failed** |

The added test is `ResourceTypeConfusionAuthzTests`, a true exploit reproduction: with the
fix reverted the attack request returns `{"status":"success"}`; with it, the request is
denied.

> Also removed here: `dmart.Tests/Integration/UserLookupIndexTests.cs`, a pre-`ceb5e1f`
> leftover asserting index names (`idx_users_email_lower`, `idx_users_msisdn`) that the
> schema no longer creates — it renamed them to `..._unique`. It was failing CI. Its
> replacement, `UserLookupIndexPlanTests.cs`, asserts via `EXPLAIN` that the lookups
> actually *use* both indexes, which is strictly stronger.

---

## Fixed — Critical

| Area | Defect | Fix |
|---|---|---|
| Entry lifecycle | **Resource-type confusion → authz bypass.** `EntryRepository.GetAsync` retries without the `resource_type` filter, but `EntryService` gated on the *client-declared* type. An editor with `update` on `resource_types:["content"]` could declare `content`, be handed a **schema** row, pass the permission walk, and overwrite it (the upsert preserves the real type). | `EntryService.UpdateAsync`/`MoveAsync` re-derive the locator from `existing.ResourceType` before calling `PermissionService`. Regression test added. |
| Realtime | **WebSocket subscriptions had no authorization.** Any authenticated user could `notification_subscription` to any space/subpath and receive space/subpath/shortname/owner for every change there. | `CanSubscribeAsync` gate mirroring `QueryService.CanQueryAsync` (view → query → root fallback). |
| cxb SPA | **Stored XSS via `{@html}` over raw server data** (`Table2Cols.svelte`), reachable from the entry List tab and `/info/settings`. | `{@html}` removed; scalars use auto-escaping interpolation, nesting recurses through real markup. |
| cxb SPA | **Stored XSS via unsanitized `marked()` output** (`Media.svelte`, `MarkdownEditor.svelte`). `marked` v18 does not sanitize. | Output wrapped in `DOMPurify.sanitize(...)`; `dompurify` added to `cxb/package.json`. |

## Fixed — High

| Area | Defect | Fix |
|---|---|---|
| Config | **Apple ES256 private key returned by `GET /info/settings`** to any authenticated user — enough to forge Apple `client_secret` assertions. | Added to `RedactedProperties`, plus a name-based safety net that redacts any *string* setting matching `Secret/Password/Key/Token/Credential` so future secrets default to redacted. |
| Repo hygiene | `appsettings.json` holds a real DB password, JWT secret and admin password; it was **not** gitignored and ships into publish/Docker output. | Added to `.gitignore` and `.dockerignore`. |
| Auth | **Password reset never evicted sessions** — a stolen token survived the victim's recovery flow. | `/password-reset-confirm` honours `LogoutOnPwdChange` and calls `DeleteAllSessionsAsync`. |
| Auth | **Username enumeration via Argon2 timing** — a miss returned in ~1 ms, a hit in 100–300 ms. | Constant-work decoy hash burned on both miss branches. |
| Data layer | **`/managed/shortening` 500'd on every call** — `ON CONFLICT (token_uuid)` with no matching unique index (42P10). | Added `idx_urlshorts_token_uuid` (also converts the resolve lookup from a seq scan to an index probe). |
| Query | **ReDoS in the search parser.** `RangeRegex` backtracks quadratically (16 k chars → 1.19 s) with no `MatchTimeout`, on the **unauthenticated** `/public/query`. | 100 ms `matchTimeout` on all five compiled regexes; timeouts and over-length input fail **closed** (`FALSE`), never open. |
| Query | **jq stdout buffered unbounded** — `[range(200000000)]` (24 chars) streams hundreds of MB into the heap. | 32 MB output budget, process killed on overflow; timeout path now awaits the copy task instead of writing into a disposed stream. |
| Plugins | **Caller's bearer token and cookie forwarded to every plugin.** | Shared `CaptureHeaders` strips `authorization`, `proxy-authorization`, `cookie`, `x-channel-key`, `x-api-key` on both transports. |
| Plugins | **Subprocess host deadlock** — untimed blocking `ReadLine()` under a process-global lock wedged all later dispatches permanently. | 30 s bounded exchange; kill + respawn on timeout. |
| Realtime | **One stalled client blocked all broadcasts forever** (no send timeout, sequential fan-out). | 5 s send timeout with peer abort; concurrent `Task.WhenAll` fan-out; payload encoded once per broadcast. |
| Realtime | **Reconnect race** — a stale socket's cleanup unregistered the *live* connection, silently killing the client's feed. | Identity-aware `Disconnect(user, ws)` using atomic compare-and-remove. |
| OAuth | **Unbounded `redirect_uris` on open dynamic registration** — hundreds of MB pinned for 24 h. | Caps: ≤8 URIs, ≤512 chars each, client name ≤128. |
| Pipeline | **35 s request timeout killed every WebSocket and MCP SSE stream.** | Timeout skipped for upgrade requests and `/mcp`. |
| Pipeline | **401/403/429 never reached the access log** — logging was registered inside auth/rate-limit, which short-circuit. Brute-force and enumeration left zero evidence. | `UseRequestLogging()` moved between `UseAuthentication()` and `UseAuthorization()`. |
| Pipeline | **Truncated JSON bodies logged verbatim**, bypassing all redaction — a >32 KB bulk user-create logged plaintext passwords. | Raw fallback replaced with a `{n} bytes` marker. |
| Export | **CSV formula injection** — cells starting with `=`/`+`/`-`/`@` were emitted verbatim; quoting does not neutralize them. | Leading `'` prefix in `EscapeField`. |
| Data layer | **Attachment listings fetched the `media` bytea and discarded it** — a 100-record query moved ~1.5 GB for a response with zero media bytes. | `SelectColumnsNoMedia` projection (`NULL::bytea`) on list/query paths; `GetAsync` keeps media for downloads. |

## Fixed — Medium

| Area | Defect | Fix |
|---|---|---|
| MCP | `Mcp-Session-Id` never bound to the caller — another user could drain a session's SSE stream, delete it, or **answer its pending destructive-delete confirmation**. | `ResolveOwnedSession` compares `session.UserShortname` to the actor; fails closed. |
| OAuth | Open redirect on `/oauth/authorize` post-validation error branches, reachable with no login. | Rendered as on-page 400s instead of redirects. |
| OAuth | Consent page showed only the opaque `client_id`, so a self-registered phishing client was indistinguishable from a real one. | Renders HTML-encoded client name + redirect-URI host. |
| OAuth | Google `email_verified` ignored before email-based account linking. | Unverified emails dropped; `IsEmailVerified` only set from a verified claim. |
| Public API | `/public/entry?retrieve_attachments=true` returned attachments with no per-attachment ACL check. | Filtered through `CanReadAsync`. |
| QR | `/qr/validate` was anonymous and **unconditionally returned success** over a stub service. | Both QR handlers return 501 until implemented. |
| Auth | `/user/otp-confirm` marked contacts verified without proving ownership of the destination. | Flags gated on a match against the user's own email/msisdn. |
| Auth | Anonymous callers could delete a victim's live OTP via the max-attempts purge. | Actor check moved above `VerifyAndConsumeAsync`. |
| Auth | `/user/check-existing` and `/user/validate_password` were unthrottled oracles; the latter also bypassed lockout and allocated 100 MiB per call. | Both rate-limited; failed validations now count toward lockout. |
| Auth | `AllowOtpResendAfter` defaulted to **1 second** while the sample config documents 60. | Default corrected to 60. |
| Workflow | Failed ticket lock still stamped `processed_by`; a fine-grained `lock` grant was unusable. | Update moved after a successful lock; `actionOverride: "lock"`. |
| Workflow | `progress-ticket` leaked ticket existence and workflow state before authorization. | Pre-authorization gate (accepts `view`/`progress_ticket`/`update`). |
| Audit log | `x-channel-key` shared secret persisted verbatim into `events.jsonl`. | Added to the excluded-header set. |
| Pipeline | Log forging via percent-encoded CRLF in the request path. | Control characters escaped before logging. |
| Pipeline | `Cache-Control: no-store` forced onto all SPA assets — ~2.7 MB re-downloaded every page load. | Skipped for content-hashed assets; `index.html`/`config.json`/API keep `no-store`. |
| Pipeline | No Content-Security-Policy despite the SPA being same-origin with the API. | CSP added for HTML responses, scoped to skip `/docs` and `/oauth` (they ship their own). |
| Pipeline | Every JSON response round-tripped through a `JsonNode` DOM with four full-size copies. | `Utf8JsonWriter` direct write + 1 MB bypass threshold. |
| Data layer | Per-request session lookup scanned the user's whole session bucket. | Added `idx_sessions_shortname_token`. |
| Crypto | JWT signature compared with ordinary string equality. | `CryptographicOperations.FixedTimeEquals`. |
| Realtime/perf | `WorkflowEngine` leaked a pooled `JsonDocument` buffer per ticket transition. | Both call sites wrapped in `using`. |
| cxb | Permissions/roles survived sign-out in `localStorage`. | Cleared at all three cleanup paths. |

---

## Not fixed — deferred, with reasons

These are **confirmed** defects that were deliberately left for a follow-up because they
change API semantics, need a data migration, or need a design decision.

| Severity | Defect | Why deferred |
|---|---|---|
| **High** | `type=history` / `type=attachments` / `type=events` queries have **no row-level authorization** — `AppendAclFilter` early-returns for those tables and they have no `query_policies` column. A user with any grant in a space can read every history `diff` in it. | Needs a schema/predicate design (EXISTS-join to `entries`, or a `query_policies` column on those tables). Highest-value remaining item. |
| **High** | `CanQueryAsync`'s root fallback (`HasAnyAccessToSpaceAsync`) ignores `actions` and `resource_types`, so a write-only grant passes a read gate. Compounds the row above. | Changing it will revoke access some deployments rely on; needs an intentional break. |
| **High** | ACL self-insertion: `update_acl` gates on plain `update`, and attachment updates write `acl`/`owner_group_shortname` from client attributes. | Needs an "can the writer perform the actions they are granting?" rule. |
| **High** | `filter_fields_values` ACL clause is string-concatenated after the caller's `search`, so a trailing `or` neutralizes it. Same class via `join_on`'s right-hand path. | Fix is to parse the two expressions separately and AND the emitted clauses — a real change to `QueryService`, worth doing carefully. |
| **High** | Refresh tokens are not session-bound: they survive logout **and** password change, and rotation has no reuse detection. `SessionMaxLifetimeSeconds` defaults to 0 (disabled). | Needs a `jti` store + rotation semantics. |
| **High** | Social login bypasses `IsActive`, lockout and device-lock gates that password login enforces. | Needs the gate order aligned across providers. |
| **High** | `/managed/export` builds the whole zip in a `MemoryStream` (hard-fails >2 GiB); export does one attachment + one history query **per entry**. | Streaming rewrite. |
| **High** | Missing index for the hot list query's `ORDER BY updated_at` (`(space_name, subpath, updated_at DESC)`), and `idx_entries_tags_gin` uses `jsonb_path_ops`, which cannot serve the `?|` predicate. | Index changes on large tables want `CONCURRENTLY` + a migration window. |
| Medium | `/managed/health`, `/managed/reload-security-data`, `/managed/apply-alteration` have no authorization beyond "logged in"; health also runs 12 scans, one O(attachments × entries), with no timeout. | Needs a decision on which admin predicate gates operator tooling. |
| Medium | `IsGlobalAdminAsync` ignores `resource_types`, so a content-scoped `__all_spaces__` grant is treated as super-admin. | Tightening may lock out existing operators. |
| Medium | Folder `unique_fields` gate is dead for non-entry types (raw vs normalized subpath in `UniquenessValidator.ValidateRawAsync`). | Enabling it will start rejecting writes that currently succeed. |
| Medium | Client-supplied `uuid` is mass-assigned and `Guid.Parse` throws → 500 with a silently committed partial batch (no per-record try/catch in the dispatcher). | Wants the batch error envelope reworked alongside. |
| Medium | Unvalidated `space_name` on `resource_with_payload` / `resources_from_csv` reaches `Path.Combine` in `SpaceEventLogger` → writes outside the spaces root. | Needs the `RequestRegex` gate applied to non-`/managed/request` entry points. |
| Medium | Control characters (NUL) in unvalidated fields (`slug`, `payload.body.*`, move destinations) still reach Postgres → 500. Regex anchors use `$`, which admits a trailing newline. | Wants one shared control-char screen across all attribute leaves. |
| Medium | Before-action plugin hooks fire **before** authorization for User/Role/Group/Permission/Space. | Reordering touches every dispatch branch. |
| Medium | `collaborators`/`reporter` silently dropped by `ApplyPatch` — `RequestType.Assign` reports success and discards them, and every ticket lock re-runs the full update pipeline. | Behavioural fix, needs a test sweep. |
| Medium | Global authz-cache flush on **every** user write; no single-flight on misses; query-policy LIKE fan-out is unbounded, undeduplicated and rebuilt per request. | Real throughput win; wants benchmarking. |
| Medium | Schemas/workflows are exfiltrated to public `plantuml.com` for diagram rendering. | Needs a self-hosted renderer or config switch. |
| Medium | `dmart init` writes `~/.dmart/config.env` (JWT secret + DB password) mode 0644. | One-line `SetUnixFileMode` fix, in CLI code outside this pass. |
| Medium | Plugin loader executes any executable/`.so` under `~/.dmart/plugins/*` with no ownership or writability check. | Wants the same perms discipline `Settings.cs` already applies to config. |
| Low | `Dmart.SqlAdapter` has three broken statements (`MoveAsync` leaves stale `query_policies`; reads a non-existent `user_permissions_cache`; `InitializeSpacesAsync` has an invalid conflict target). | SDK-side; no in-tree caller. |

---

## Confirmed clean

PKCE and redirect-URI exact matching · authorization-code entropy/replay · SQL injection
(every user token is allowlisted, quote-doubled, or bound as `$N`) · CORS · forwarded-header
trust · JWT secret validation · Argon2id parameters and constant-time password/OTP compare ·
session-row binding on the HTTP **and** WebSocket paths · zip-slip and decompression bombs ·
import/export authorization · download path traversal · DI lifetimes (no captive dependencies) ·
AOT/JSON source-gen discipline · `HttpClient` reuse · TLS verification · shell-command injection.
