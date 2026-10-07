Comprehensive reference for the DMART API. All endpoints, data structures, and usage examples.

## Base URL & Authentication

The API is served by the self-contained DMART binary (.NET 10 Native AOT) on ASP.NET Core / Kestrel. The base URL for all API requests is typically: `http://localhost:8282` (or as configured via `LISTENING_HOST` / `LISTENING_PORT`).

Most endpoints require authentication with a JWT bearer token (HS256), supplied either as a header or a cookie:

**Header** `Authorization: Bearer <your_token>` **Cookie** `auth_token=<your_token>`

Tokens are obtained via `/user/login` (password or OTP) or through OAuth social login (Google, Facebook, Apple). DMART also exposes a full **OAuth 2.1 Authorization Server** — discovery, Dynamic Client Registration, and authorize/token endpoints under `/.well-known` — used for automatic onboarding of MCP clients.

Interactive API documentation is available as **Swagger UI** at `/docs`, with the raw OpenAPI schema at `/docs/openapi.json` (both served by ASP.NET Core).

## Common Data Structures

### Record

Represents a resource in the system. This is the primary object for creating or updating entities.

```json
{
  "resource_type": "content",
  "shortname": "my-article",
  "subpath": "/blog",
  "uuid": "550e8400-e29b-41d4-a716-446655440000",
  "attributes": {
    "is_active": true,
    "slug": "my-article-slug",
    "displayname": { "en": "My Article" },
    "description": { "en": "A description" },
    "tags": ["news", "tech"],
    "owner_shortname": "jdoe",
    "owner_group_shortname": "editors",
    "created_at": "2023-10-01T12:00:00",
    "updated_at": "2023-10-01T12:00:00",
    "payload": {
      "content_type": "json",
      "schema_shortname": "article",
      "body": { "title": "Hello World", "content": "..." }
    },
    "acl": [
      {
        "user_shortname": "jane",
        "allowed_actions": ["view", "update"]
      }
    ],
    "relationships": [
      {
        "related_to": {
          "space_name": "data",
          "type": "user",
          "subpath": "users",
          "shortname": "jane"
        },
        "attributes": { "role": "editor" }
      }
    ]
  }
}
```

### Request

Used for batch operations (create, update, delete, etc.).

```json
{
  "space_name": "data",
  "request_type": "create",
  "records": [ ... list of Record objects ... ]
}
```

### Query

Used for searching and filtering resources. Supports full-text search, aggregation, joins, and JQ filters.

```json
{
  "type": "search",
  "space_name": "data",
  "subpath": "/blog",
  "exact_subpath": false,
  "filter_types": ["content"],
  "filter_schema_names": ["article"],
  "filter_shortnames": [],
  "filter_tags": ["tech"],
  "search": "@title:Hello*",
  "from_date": "2023-01-01T00:00:00",
  "to_date": "2023-12-31T23:59:59",
  "exclude_fields": ["payload.body"],
  "include_fields": ["shortname", "displayname"],
  "highlight_fields": { "description.en": "" },
  "sort_by": "created_at",
  "sort_type": "descending",
  "retrieve_json_payload": true,
  "retrieve_attachments": false,
  "retrieve_total": true,
  "validate_schema": true,
  "retrieve_lock_status": false,
  "jq_filter": ". | select(.attributes.is_active == true)",
  "limit": 10,
  "offset": 0,
  "aggregation_data": {
    "group_by": ["@tags"],
    "reducers": [
      { "reducer_name": "count_distinct", "alias": "count", "args": ["@shortname"] }
    ]
  },
  "join": [
    {
      "join_on": "uuid",
      "alias": "author_details",
      "query": { "...nested Query..." }
    }
  ]
}
```

## Enums & Possible Values

| Enum | Values |
|---|---|
| **ResourceType** (30) | `user`, `group`, `folder`, `schema`, `content`, `log`, `acl`, `comment`, `media`, `data_asset`, `locator`, `relationship`, `alteration`, `history`, `space`, `permission`, `role`, `ticket`, `json`, `lock`, `post`, `reaction`, `reply`, `share`, `plugin_wrapper`, `notification`, `csv`, `jsonl`, `sqlite`, `parquet` |
| **RequestType** (7) | `create`, `update`, `patch`, `update_acl`, `assign`, `delete`, `move` |
| **ContentType** (21) | `text`, `comment`, `reaction`, `markdown`, `html`, `json`, `image`, `image_jpeg`, `image_png`, `image_svg`, `image_gif`, `image_webp`, `python`, `pdf`, `audio`, `video`, `csv`, `parquet`, `jsonl`, `apk`, `sqlite` |
| **ActionType** (11) | `query`, `view`, `update`, `create`, `delete`, `attach`, `assign`, `move`, `progress_ticket`, `lock`, `unlock` |
| **QueryType** (12) | `search`, `subpath`, `events`, `history`, `tags`, `random`, `spaces`, `counters`, `reports`, `aggregation`, `attachments`, `attachments_aggregation` |
| **Language** (5) | `arabic`, `english`, `kurdish`, `french`, `turkish` These full spellings are the persisted enum wire values (e.g. `space.languages`). The 2-letter keys `ar` / `en` / `ku` / `fr` / `tr` are ONLY keys inside `displayname` / `description` translation maps — they are NOT Language enum values. |
| **SortType** (2) | `ascending`, `descending` |
| **UserType** (3) | `web`, `mobile`, `bot` |
| **PluginType** (2) | `hook`, `api` |
| **EventListenTime** (2) | `before`, `after` |
| **JoinType** (4) | `left`, `right`, `inner`, `outer` |
| **PublicSubmitResourceType** (2) | `content`, `ticket` The only two resource types accepted by `/public/submit`. |
| **Status** (2) | `success`, `failed` The response-envelope `status` field (see below). |
| **TaskType** (1) | `query` |

### String conventions (NOT typed enums)

The following values are string **conventions** — they are validated/handled as plain strings and have _no_ backing C# enum. Only the two permission condition constants (`own`, `is_active`) are real string constants; reaction kinds, the lock action string, and notification type/priority are inherited Python-era conventions.

| Convention | Values |
|---|---|
| **Permission conditions** (real string constants) | `own`, `is_active` |
| **Reaction kinds** (convention) | `like`, `dislike`, `love`, `care`, `laughing`, `sad` |
| **Lock action** (convention) | `lock`, `unlock` (acquire/release via the `/managed/lock` routes) |
| **Notification type** (convention) | `admin`, `system` |
| **Notification priority** (convention) | `high`, `medium`, `low` |

### Response envelope

Every `/managed`, `/public`, and `/user` endpoint returns the same envelope. A success carries `records` and an `attributes` block (with `total` / `returned` counts on queries); a failure carries a single `error` triple and `records: null`.

```
// success
{
  "status": "success",
  "error": null,
  "records": [ /* ...Record objects... */ ],
  "attributes": { "total": 128, "returned": 10 }
}

// failure
{
  "status": "failed",
  "error": {
    "type": "jwtauth",
    "code": 401,
    "message": "Not authenticated",
    "info": null
  }
}
```

## User Management `/user`

GET `/user/check-existing`

Checks if a user with specific fields already exists.

**Query Params:** `shortname`, `msisdn`, `email` (all optional)

POST `/user/create`

Registers a new user.

```json
{
  "resource_type": "user",
  "shortname": "jdoe",
  "subpath": "users",
  "attributes": {
    "email": "jdoe@example.com",
    "password": "StrongPassword123!",
    "displayname": { "en": "John Doe" }
  }
}
```

POST `/user/login`

Authenticates a user and returns a token.

```json
{
  "shortname": "jdoe",
  "password": "StrongPassword123!"
}
```

Or via OTP:

```json
{
  "msisdn": "1234567890",
  "otp": "123456"
}
```

GET `/user/profile`

Retrieves the profile of the currently logged-in user.

POST `/user/profile`

Updates the profile of the currently logged-in user.

POST `/user/logout`

Logs out the current user.

POST `/user/delete`

Deletes the current user's account.

POST `/user/otp-request`

Requests an OTP for login or verification.

POST `/user/otp-request-login`

Requests an OTP specifically for login purposes.

POST `/user/password-reset-request`

Initiates the password reset process.

POST `/user/otp-confirm`

Verifies the OTP sent to the user.

POST `/user/reset`

Resets a user's password (requires appropriate permissions).

POST `/user/validate_password`

Checks if the provided password is correct for the current user.

### Social Login Callbacks

Handles callbacks from social login providers.

GET `/user/google/callback`

Google OAuth callback.

GET `/user/facebook/callback`

Facebook OAuth callback.

GET `/user/apple/callback`

Apple OAuth callback.

## Managed Content `/managed`

Endpoints for authenticated management of content and resources.

POST `/managed/request`

Performs batch operations (create, update, delete, etc.).

```json
{
  "space_name": "data",
  "request_type": "create",
  "records": [
    {
      "resource_type": "content",
      "shortname": "new-item",
      "subpath": "/items",
      "attributes": {
        "displayname": {"en": "New Item"},
        "payload": {
          "content_type": "json",
          "body": {"key": "value"}
        }
      }
    }
  ]
}
```

**Delete with `force` / `dry_run`:** a `delete` request accepts two top-level flags. `force: true` cascade-deletes a non-empty folder (and all its contents); a plain delete of a non-empty folder is rejected. `dry_run: true` projects the full cascade and returns an affected-count report _without removing anything_.

```json
{
  "space_name": "data",
  "request_type": "delete",
  "force": true,
  "dry_run": true,
  "records": [
    { "resource_type": "folder", "shortname": "archive", "subpath": "/" }
  ]
}
// -> success envelope; each record's attributes carry a per-category
//    "report" of what WOULD be deleted, plus "dry_run": true (nothing removed).
```

POST `/managed/query`

Executes a query against the database.

```json
{
  "type": "search",
  "space_name": "data",
  "subpath": "/content",
  "search": "*",
  "limit": 5
}
```

POST `/managed/semantic-search`

Natural-language (vector) search — embeds the query and returns the top-N most similar entries by cosine distance over `entries.embedding`. Requires the pgvector extension _and_ a configured embedding provider (`EMBEDDING_API_URL`); when either is missing it returns a clean `400` failure envelope. Results are permission-filtered, and `limit` is clamped to a max of 100.

```
// request
{
  "query": "how do refunds work",
  "space_name": "data",        // optional
  "subpath": "/articles",       // optional prefix
  "resource_types": ["content"],// optional
  "limit": 10                    // optional (default 10, max 100)
}

// success -> each record's attributes carry space_name, similarity (0..1), uri
{
  "status": "success",
  "records": [
    {
      "resource_type": "content",
      "shortname": "refund-policy",
      "subpath": "/articles",
      "attributes": { "space_name": "data", "similarity": 0.83,
                      "uri": "dmart://data/articles/refund-policy" }
    }
  ],
  "attributes": { "returned": 1, "matched": 3 }
}

// not configured
{ "status": "failed",
  "error": { "type": "request", "code": 400,
             "message": "semantic search not configured — set EMBEDDING_API_URL ..." } }
```

POST `/managed/reindex-embeddings`

Admin tool that (re)embeds entries into the pgvector column — used to backfill or rebuild the semantic-search index. Runs as a background job.

GET `/managed/reindex-embeddings/status`

Admin-only. Returns the live progress of the current or last re-index run.

POST `/managed/import`

Imports data from a ZIP file. Body: `multipart/form-data` with `zip_file`.

POST `/managed/export`

Exports data based on a `Query` object.

POST `/managed/csv`

Exports query results as CSV.

PUT `/managed/progress-ticket/{space}/{subpath}/{shortname}/{action}`

Updates the state of a ticket workflow.

```json
{
  "resolution": "Fixed",
  "comment": "Done"
}
```

GET `/managed/payload/{resource_type}/{space}/{subpath}/{shortname}.{ext}`

Gets the raw payload of a resource.

POST `/managed/resource_with_payload`

Uploads a file as payload. Body: `multipart/form-data` with `payload_file`, `request_record`, `space_name`.

POST `/managed/resources_from_csv/{resource_type}/{space}/{subpath}/{schema}`

Creates resources from a CSV file.

GET `/managed/entry/{resource_type}/{space}/{subpath}/{shortname}`

Gets the metadata of a resource.

GET `/managed/byuuid/{uuid}`

Gets an entry by UUID.

GET `/managed/byslug/{slug}`

Gets an entry by slug.

GET `/managed/health/{health_type}/{space}`

Runs a health check. Types: `soft`, `hard`.

PUT `/managed/lock/{resource_type}/{space}/{subpath}/{shortname}`

**Acquires** an entry lock for the caller. While a lock is held, `update` and `delete` from any user other than the lock holder are rejected; the holder may re-lock (extend) their own lock. Query results can surface the current holder via `retrieve_lock_status` (see the `Query` shape above).

DELETE `/managed/lock/{space}/{subpath}/{shortname}`

**Releases** the lock the caller holds on an entry.

GET `/managed/reload-security-data`

Reloads the in-process permissions and roles cache from PostgreSQL.

POST `/managed/execute/{task_type}/{space}`

Runs a **saved query** task. The only defined `task_type` is `query`: DMART loads a saved query entry by `shortname`, parses its `payload.body` as a `Query`, and executes it. `query_overrides` merge into the loaded query, and any `$param` placeholders inside the query's `search` string (e.g. `@status:$state`) are substituted from the overrides; unresolved `@field:$param` fragments are stripped before execution.

The misspelled legacy path `/managed/excute/{task_type}/{space}` is also mapped for client compatibility.

```json
{
  "shortname": "open-tickets",
  "subpath": "/tasks",
  "query_overrides": { "state": "open", "limit": 20 }
}
```

POST `/managed/apply-alteration/{space}/{alteration_name}`

Applies a recorded alteration to an entry.

GET `/managed/shortening/{space}/{**rest}`

**Creates** a short link for a DMART resource URL and returns a random token. The resolvable `short_url` is bounded by `APP_URL` and the token expires after `URL_SHORTER_EXPIRES` seconds.

```
// -> { "short_url": "https://app.example.com/managed/s/aB3xY9" }
```

GET `/managed/s/{token}`

Resolves a short token and issues a `302` redirect to its original URL (anonymous-accessible, rate-limited).

## Public Access `/public`

Endpoints for unauthenticated or public access (if configured).

POST `/public/query`

Executes a query publicly.

GET `/public/query/{type}/{space}/{subpath}`

Public query via URL params.

GET `/public/entry/{resource_type}/{space}/{subpath}/{shortname}`

Retrieves a public entry.

GET `/public/payload/{resource_type}/{space}/{subpath}/{shortname}.{ext}`

Retrieves a public payload.

POST `/public/submit/{space}/{**rest}`

Submits data to a public endpoint (e.g. a form). An optional **leading** path segment selects the resource type: it is parsed against **PublicSubmitResourceType**, so only `content` or `ticket` are honored. When the leading segment is _not_ one of those, it is treated as the space name and the resource type defaults to `content` (it is not rejected). Submitting a `ticket` additionally requires a workflow shortname in the path.

POST `/public/attach/{space}`

Attaches a file to a record publicly.

POST `/public/excute/{task_type}/{space}`

Executes a task publicly.

GET `/public/byuuid/{uuid}`

Gets a public entry by UUID.

GET `/public/byslug/{slug}`

Gets a public entry by slug.

## QR Codes `/qr`

Note: the `/qr` generate/validate endpoints are currently minimal / a stub in this port and are not yet fully featured.

GET `/qr/generate/{resource_type}/{space}/{subpath}/{shortname}`

Generates a QR code for a resource.

POST `/qr/validate`

Validates a scanned QR code.

```json
{
  "resource_type": "user",
  "space_name": "data",
  "subpath": "/users",
  "shortname": "jdoe",
  "qr_data": ""
}
```

## System Info `/info`

GET `/info/me`

Gets current user info.

GET `/info/settings`

Gets system settings (restricted to 'dmart' user).

GET `/info/manifest`

Returns system version and status.

## Realtime & WebSockets `/ws`

DMART pushes live updates over WebSocket connections. A built-in notifier plugin broadcasts entry changes to subscribed clients.

GET `/ws`

Opens an authenticated WebSocket connection for realtime notifications. The JWT is passed as a query param (`ws://host/ws?token=JWT`) or via the `auth_token` cookie; the socket is closed with `401` if the token is invalid, the user is inactive, or the session has been revoked.

After connecting, the client subscribes by sending a `notification_subscription` message. DMART builds a channel name of the form `space:subpath:schema:action:state`, defaulting any omitted segment to the `__ALL__` wildcard. The realtime notifier plugin then broadcasts every matching CRUD event to subscribed clients.

```
// client -> server: subscribe (omit fields to wildcard them)
{
  "type": "notification_subscription",
  "space_name": "data",
  "subpath": "/tickets",
  "schema_shortname": "ticket",   // optional -> __ALL__
  "action_type": "update",         // optional -> __ALL__
  "ticket_state": "open"           // optional -> __ALL__
}
// -> channel "data:/tickets:ticket:update:open"

// server -> client on connect
{ "type": "connection_response", "message": { "status": "success" } }
```

POST `/send-message/{user}`

Sends a message to a specific connected user.

POST `/broadcast-to-channels`

Broadcasts a message to subscribers of one or more channels.

GET `/ws-info`

Returns information about active WebSocket connections.

## Model Context Protocol `/mcp`

DMART ships a built-in **MCP server** (Streamable HTTP transport, spec `2025-03-26`) so AI agents can query and mutate entries as tools. It is **off by default** — set `ENABLE_MCP=true` to expose it; otherwise `/mcp` and the OAuth 2.1 endpoints are unmapped and answer `INVALID_ROUTE` (HTTP 422). All requests are authenticated — the caller's JWT flows through to each tool handler. Sessions are tracked via the `Mcp-Session-Id` header. Pair it with the OAuth 2.1 Authorization Server for automatic client onboarding.

POST `/mcp`

Sends a JSON-RPC MCP request (initialize, list/call tools, etc.).

GET `/mcp`

Opens the server-sent-events (SSE) stream for the MCP session.

DELETE `/mcp`

Terminates the current MCP session.

### Available tools (11)

The MCP server exposes these tools to agents. Every tool runs under the caller's JWT, so DMART's permission model is enforced identically to the HTTP API — there is no admin escape hatch.

| Tool | Purpose |
|---|---|
| `dmart_me` | Return the authenticated caller's profile. |
| `dmart_spaces` | List the spaces the caller can access. |
| `dmart_query` | Search / list entries. Results are hard-capped at **50** records per call. |
| `dmart_read` | Read a single entry (metadata + payload). |
| `dmart_schema` | Fetch a schema definition for a space/subpath. |
| `dmart_create` | Create a new entry. |
| `dmart_update` | Update / patch an existing entry. |
| `dmart_delete` | Delete an entry. Requires an interactive **elicitation** confirmation before it proceeds; folders take an optional `force` flag to cascade-delete non-empty contents. |
| `dmart_history` | Retrieve the change history of an entry. |
| `dmart_download` | Download the raw payload / attachment of an entry. |
| `dmart_semantic_search` | Natural-language vector search (requires pgvector + an embedding provider, same as `/managed/semantic-search`). |
