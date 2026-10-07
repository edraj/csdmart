All configurable settings for DMART. Bound to the strongly-typed `DmartSettings` record and loaded from a `config.env` dotenv file and/or environment variables. Values are read **once at boot** and validated before the server starts — there is no hot reload and no file watcher.

## Loading Order

Sources are layered with a strict precedence (later wins): `appsettings.json` < `config.env` < environment variables (`Dmart__Xxx`).

The `config.env` file is located with a first-match-wins lookup:

1. The path in the `BACKEND_ENV` (or `DMART_ENV`) environment variable — used for dev/CI, e.g. a repo-local file

2. `~/.dmart/config.env` — per-user install

3. `/etc/dmart/config.env` — system-wide RPM/DEB install

A cwd-relative `./config.env` is _not_ looked up implicitly. Because the file may carry `JWT_SECRET` and `DATABASE_PASSWORD`, DMART refuses to read it if "other" permission bits are set.

**Strict validation.** Configuration is bound and validated at startup (`ValidateOnStart`). Any **unknown key is rejected** and aborts startup — there is no silent fallthrough for typos. Retired keys — `REDIS_HOST`/`PORT`/`PASSWORD`/`CONNECTION`, `DATABASE_DRIVER`, and `ACTIVE_DATA_DB` — no longer exist and now hard-fail the boot if present.

## General Configuration

| Setting | Description | Default |
|---|---|---|
| `APP_URL` | Public base URL of the server, used to assemble short-link URLs. Empty disables short-link assembly. | `""` |
| `MANAGEMENT_SPACE` | Name of the space that holds users, roles, groups and permissions | `"management"` |
| `MAX_QUERY_LIMIT` | Hard cap on the number of records returned by a query | `10000` |
| `URL_SHORTER_EXPIRES` | Short-link expiration (seconds) | `3600` |
| `REQUEST_TIMEOUT` | Outbound HTTP timeout (seconds) for plugins/webhooks | `35` |
| `JQ_TIMEOUT` | Timeout (seconds) for the `jq` subprocess used by join sub-queries carrying a `jq_filter` | `2` |

## Server & Network

| Setting | Description | Default |
|---|---|---|
| `LISTENING_HOST` | Hostname/IP the Kestrel server binds to | `"0.0.0.0"` |
| `LISTENING_PORT` | Port the server listens on (HTTP + WebSocket share this port) | `8282` |
| `CXB_URL` | URL path prefix for the embedded CXB admin SPA. Set to `/` to serve it at the root. | `"/cxb"` |
| `CAT_URL` | URL path prefix for the embedded Catalog SPA | `"/cat"` |
| `ALLOWED_CORS_ORIGINS` | Comma-separated origins allowed to make cross-site requests. Empty allows only the same-host origin. | `""` |
| `TRUSTED_PROXIES` | Comma-separated IPs/CIDRs whose `X-Forwarded-For` is trusted (for real client IP behind nginx/an LB) | `""` |
| `FORWARDED_FOR_HOP_COUNT` | Number of proxy hops in front of DMART | `1` |
| `OTLP_ENDPOINT` | OpenTelemetry collector endpoint for metrics/traces. Empty disables observability entirely. | `""` |

## Database & Storage

**PostgreSQL (via Npgsql) is the sole runtime data store.** There is no Redis and no filesystem runtime adapter; all caches are in-process. Provide a full connection string with `POSTGRES_CONNECTION`, or leave it unset and let DMART assemble one from the individual `DATABASE_*` components below.

### PostgreSQL Connection

| Setting | Description | Default |
|---|---|---|
| `POSTGRES_CONNECTION` | Full Npgsql connection string. When set, the component settings below are ignored. | `""` (unset) |
| `DATABASE_HOST` | Database hostname | `"localhost"` |
| `DATABASE_PORT` | Database port | `5432` |
| `DATABASE_USERNAME` | Database username | `"dmart"` |
| `DATABASE_PASSWORD` | Database password | `""` |
| `DATABASE_NAME` | Database name | `"dmart"` |
| `DATABASE_POOL_SIZE` | Connection pool size | `10` |
| `DATABASE_MAX_OVERFLOW` | Max overflow connections | `10` |
| `DATABASE_POOL_TIMEOUT` | Pool timeout (seconds) | `30` |
| `DATABASE_POOL_RECYCLE` | Pool recycle time (seconds) | `1800` |
| `DATABASE_KEEPALIVE` | Seconds of socket inactivity before a keepalive probe is sent. 0 disables. | `30` |

### Spaces Folder (import/export only)

The on-disk `spaces/` + `.dm/meta.*.json` layout is a transfer/backup format used only by `import`, `export`, `seed` and `migrate` — never as a live store. `SPACES_FOLDER` defaults empty and, when set, is also the root for an optional append-only `events.jsonl.log` audit trail (disabled unless configured).

## Security & Authentication

Authentication is JWT Bearer (HS256, symmetric key from `JWT_SECRET`) with Argon2 password hashing, plus an OAuth 2.1 Authorization Server. The signing algorithm is fixed at HS256 and is not configurable.

### JWT & Sessions

| Setting | Description | Default |
|---|---|---|
| `JWT_SECRET` | Symmetric signing key (min 32 bytes). **Required — the server refuses to start on the placeholder value.** | `"change-me…"` |
| `JWT_ISSUER` | Expected `iss` claim | `"dmart"` |
| `JWT_AUDIENCE` | Expected `aud` claim | `"dmart"` |
| `JWT_ACCESS_EXPIRES` | Access-token lifetime (seconds) | `2592000` (30d) |
| `JWT_REFRESH_DAYS` | Refresh-token lifetime (days) | `30` |
| `JWT_REQUIRE_TOKEN_USE` | Reject JWTs lacking the `token_use` claim (access vs refresh separation) | `false` |
| `MAX_SESSIONS_PER_USER` | Max concurrent sessions per user | `5` |
| `SESSION_INACTIVITY_TTL` | Idle seconds before a JWT is rejected and its session deleted (0 = disabled) | `0` |
| `SESSION_MAX_LIFETIME_SECONDS` | Hard cap on the refresh-token chain from original login (0 = disabled) | `0` |
| `LOGOUT_ON_PWD_CHANGE` | Invalidate all sessions on password change | `true` |
| `CSRF_PROTECT_COOKIE_AUTH` | Gate cookie-borne auth against cross-site requests (bearer-header callers unaffected) | `true` |

### Brute-force & Rate Limiting

| Setting | Description | Default |
|---|---|---|
| `MAX_FAILED_LOGIN_ATTEMPTS` | Failed attempts before an account is auto-locked | `5` |
| `LOCKOUT_COOLDOWN_SECONDS` | Seconds an account stays locked before a retry may clear it (0 = permanent until admin reset) | `900` |
| `AUTH_RATE_LIMIT_PER_MINUTE` | Per-IP cap on `/user/login` + `/user/otp-request` per 60s | `10` |
| `MAX_OTP_VERIFY_ATTEMPTS` | Wrong guesses allowed against one OTP code before it is invalidated (0 = uncapped) | `5` |
| `LOCK_PERIOD` | Seconds a `PUT /managed/lock` stays held before another user can take it | `300` |

### Registration, OTP & Profiles

| Setting | Description | Default |
|---|---|---|
| `IS_REGISTRABLE` | Allow self-registration via `POST /user/create` | `true` |
| `IS_OTP_FOR_CREATE_REQUIRED` | Require a verified OTP for account creation | `true` |
| `OTP_TOKEN_TTL` | One-time-password time-to-live (seconds) | `300` |
| `ALLOW_OTP_RESEND_AFTER` | Minimum seconds between OTP re-sends to one destination | `60` |
| `ALLOW_PASSWORD_RESET_RESEND_AFTER` | Minimum seconds between password-reset OTP re-sends | `60` |
| `USER_CREATE_DEFAULT_ROLE` | Single role assigned to every self-created user (self-service create ignores roles in the body) | `""` (none) |
| `USER_CREATE_DEFAULT_GROUP` | Single group assigned to every self-created user | `""` (none) |
| `USER_PROFILE_PAYLOAD_PROTECTED_FIELDS` | CSV of payload fields users cannot update via `POST /user/profile` | `""` |
| `ALLOWED_SUBMIT_MODELS` | CSV of `space.schema` pairs allowed for public `/submit` endpoints (empty = none) | `""` |

### Admin Bootstrap

| Setting | Description | Default |
|---|---|---|
| `ADMIN_EMAIL` | Email seeded for the `dmart` admin on first boot only | `""` |
| `ADMIN_PASSWORD` | First-boot admin password. Intentionally omitted from `config.env`; the recommended flow is `dmart passwd dmart <pwd>`. Honoured via `Dmart__AdminPassword` if provided. | `""` (unset) |

## Email & Notifications

### SMTP

| Setting | Description | Default |
|---|---|---|
| `MAIL_HOST` | SMTP server host. Empty falls back to logging the code only. | `""` |
| `MAIL_PORT` | SMTP port | `587` |
| `MAIL_USERNAME` | SMTP username | `""` |
| `MAIL_PASSWORD` | SMTP password | `""` |
| `MAIL_USE_TLS` | Use TLS for the SMTP connection | `true` |
| `MAIL_FROM_ADDRESS` | From email address | `"noreply@admin.com"` |
| `MAIL_FROM_NAME` | From name | `""` |
| `MOCK_SMTP_API` | Short-circuit SMTP delivery (dev/test) | `false` |

### SMS Gateway

| Setting | Description | Default |
|---|---|---|
| `SEND_SMS_OTP_API` | POST endpoint for OTP SMS delivery. Empty logs the code only. | `""` |
| `SEND_SMS_API` | POST endpoint for general SMS delivery | `""` |
| `SMS_SENDER` | Optional sender ID / from-name inlined into the SMS request body | `""` |
| `SMPP_AUTH_KEY` | Value of the `auth-key` header sent to the SMS gateway | `""` |
| `MOCK_SMPP_API` | Short-circuit SMS delivery (dev/test) | `false` |
| `MOCK_OTP_CODE` | Fixed OTP code returned when mocking is enabled | `"123456"` |

## Logging

| Setting | Description | Default |
|---|---|---|
| `LOG_FORMAT` | `"text"` (human-readable) or `"json"` (structured JSON lines) | `"text"` |
| `LOG_LEVEL` | `trace`, `debug`, `information`, `warning`, `error`, `critical`, `none` | `"information"` |
| `LOG_FILE` | Log file path. Empty = stdout only (container/journald friendly). | `""` |
| `LOG_MAX_BYTES` | Max file size before rotation. 0 disables rotation. | `1073741824` (1 GB) |
| `LOG_BACKUP_COUNT` | Rotated archive retention (< 0 unlimited, 0 truncate, > 0 keep N) | `-1` |

## Third-Party Integrations

### OAuth / Social Login

Leaving a provider’s `CLIENT_ID` blank disables it cleanly — its endpoints return a "provider not configured" error instead of attempting an outbound call.

| Setting | Description |
|---|---|
| `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET` / `GOOGLE_OAUTH_CALLBACK` | Google OAuth credentials + redirect URI |
| `FACEBOOK_CLIENT_ID` / `FACEBOOK_CLIENT_SECRET` / `FACEBOOK_OAUTH_CALLBACK` | Facebook OAuth credentials + redirect URI |
| `APPLE_CLIENT_ID` / `APPLE_TEAM_ID` / `APPLE_KEY_ID` / `APPLE_P8_PRIVATE_KEY` / `APPLE_OAUTH_CALLBACK` | Apple Sign In. The client secret is a short-lived ES256 JWT that DMART mints on the fly from the .p8 key. |

### Embeddings (semantic search, opt-in)

When the `pgvector` extension is installed _and_ `EMBEDDING_API_URL` is set, DMART embeds every entry on create/update and exposes `POST /managed/semantic-search` plus the `dmart_semantic_search` MCP tool. The endpoint is OpenAI-compatible.

| Setting | Description | Default |
|---|---|---|
| `EMBEDDING_API_URL` | Embeddings endpoint. Empty keeps semantic search off. | `""` |
| `EMBEDDING_API_KEY` | Bearer token for the embeddings endpoint | `""` |
| `EMBEDDING_MODEL` | Model name sent in the request body | `"text-embedding-3-small"` |

### Channel Authentication

| Setting | Description | Default |
|---|---|---|
| `ENABLE_CHANNEL_AUTH` | Gate requests on the `x-channel-key` header | `false` |
| `CHANNELS_CONFIG_PATH` | Path to the channels JSON file (empty = `~/.dmart/channels.json`) | `""` |

## Miscellaneous

| Setting | Description | Default |
|---|---|---|
| `ENFORCE_FOLDER_CONTENT_POLICY` | Enforce a folder’s content-policy arrays on create/update/move. False = dry-run (violations warn-logged but allowed). | `true` |
| `ENABLE_INNER_JOIN_PUSHDOWN` | Push eligible inner joins into SQL as correlated EXISTS semi-joins. False forces the in-memory fallback. | `true` |
| `ENABLE_MCP` | Expose the Model Context Protocol surface: `/mcp`, the OAuth 2.1 authorization server (`/oauth/*`) and its `/.well-known/oauth-*` discovery documents. False leaves all of them unmapped (`INVALID_ROUTE`, HTTP 422). | `false` |
| `IMPORT_MAX_ENTRIES` | Reject zip imports declaring more than this many entries (decompression-bomb guard). 0 disables. | `500000` |
| `IMPORT_MAX_UNCOMPRESSED_BYTES` | Reject zip imports whose declared uncompressed size exceeds this. 0 disables. | `2147483648` (2 GiB) |
