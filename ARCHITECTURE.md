# Architecture

This document describes how DMART is structured, why it's structured that way,
and what constraints future changes must respect. If you're reading the code
for the first time, start here.

## What DMART is

DMART is a headless information management system, distributed as a single Native AOT binary.

These numbers are the reason for every unusual choice below. Lose sight of them
and you'll be tempted to "simplify" things in ways that give up the advantage.

## The three hard constraints

Everything in this codebase answers to one of three constraints. Every design
decision is downstream of these.

### 1. Native AOT

The server ships as a single AOT-compiled binary. This is not a deployment
convenience — it's a hard architectural constraint that rules out entire
categories of C# code:

- **No runtime reflection for unknown types.** `System.Reflection.Emit`,
  `Expression.Compile`, and dynamic assembly loading are all broken under AOT.
- **No reflection-based serializers.** `System.Text.Json` is used with
  source-generated `JsonSerializerContext` — every DTO that crosses the wire
  is listed in `Models/Json/DmartJsonContext.cs`.
- **No EF Core.** Even its AOT-compatible mode adds reflection surfaces and
  startup cost that defeat the purpose. The data layer uses Npgsql directly.
- **No MVC controllers.** Controllers depend on reflection for parameter
  binding and action dispatch. The API is built entirely on Minimal APIs with
  `app.MapPost`/`MapGet` handlers.
- **No IoC containers beyond the built-in.** `Microsoft.Extensions.DependencyInjection`
  is AOT-safe. Autofac, Castle Windsor, etc. are not.

The csproj enforces this at build time:

```xml
<PublishAot>true</PublishAot>
<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
<EnableAotAnalyzer>true</EnableAotAnalyzer>
<TreatTrimWarningsAsErrors>true</TreatTrimWarningsAsErrors>
<TreatAotWarningsAsErrors>true</TreatAotWarningsAsErrors>
<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
```

If a PR introduces a dependency or pattern that produces trim/AOT warnings,
the build fails. Don't suppress the warnings — fix the root cause or pick a
different library.

### 2. Performance

"It's fine, modern hardware can handle it" is not an argument in this codebase.
The whole point of the port is that it's not fine.

- **Allocations in hot paths matter.** The query path (HTTP → QueryService →
  EntryRepository → Npgsql) is exercised at thousands of requests per second.
  Unnecessary `ToList()`, `string.Format()`, and `Linq` chains add up.
- **Async-heavy code is the norm.** Every IO-bound method is `async`. Under
  .NET 11's runtime-async improvements this gets meaningfully cheaper, which
  is one reason the codebase targets .NET 10 today with an eye on .NET 12 LTS.
- **Startup time is a feature.** Cold start under a second is a demonstrable
  advantage. Adding a reflection-heavy library that loads 200ms
  of metadata at first-request time erases that advantage.

## Request lifecycle

A request hitting csdmart flows through roughly this path:

```
HTTP request
    ↓
Kestrel (ConfigureKestrel in Program.cs)
    ↓
Middleware chain
    ├── CORS
    ├── CXB asset serving (for /cxb/* paths)
    ├── CAT asset serving (for /cat/* paths)
    ├── JWT authentication
    ├── RequestContext scoping
    ├── Request logging
    └── Response compression
    ↓
Minimal API handler (Api/*/Handler.cs)
    ↓
Service layer (Services/*.cs)
    ├── PermissionService — ACL enforcement
    ├── SchemaValidator — JSON Schema validation
    ├── WorkflowEngine — state machine transitions
    └── PluginManager — before/after hook dispatch
    ↓
Data layer (DataAdapters/Sql/*.cs)
    └── Npgsql direct SQL
    ↓
PostgreSQL
```

A typical `POST /managed/request` creating an entry:

1. Kestrel accepts the request; the JSON body is parsed via `DmartJsonContext`
   into a `Request` DTO.
2. `JwtBearer` middleware validates the token and populates `HttpContext.User`.
3. `RequestContext` (scoped DI) pulls the user's shortname and loaded
   permissions; caches them for the request's lifetime.
4. The handler in `Api/Managed/RequestHandler.cs` validates the request shape
   and dispatches to `EntryService`.
5. `EntryService` runs the permission check, validates the payload against its
   JSON Schema, fires the `before_create` plugin hook chain, writes to
   Postgres via `EntryRepository`, then fires the `after_create` hooks.
6. The response is serialized back to JSON via the same source-gen context
   and returned.

## Plugin system

Plugins are first-class. They fall into two categories, each with two variants:

|                   | **Hook plugins**                  | **API plugins**                      |
|-------------------|-----------------------------------|--------------------------------------|
| **Built-in**      | `Plugins/BuiltIn/*.cs`            | `Plugins/BuiltIn/*.cs`               |
| **Native (.so)**  | `Plugins/Native/NativePluginLoader.cs` | Same loader, different config       |

Hook plugins fire on domain events (`create`, `update`, `delete` on entries).
They can run before or after the event. Built-in examples:
`RealtimeUpdatesNotifierPlugin` (broadcasts CRUD events to WebSocket
subscribers), `AuditPlugin` (logs events), `ResourceFoldersCreationPlugin`
(creates `/schema` when a space is created).

API plugins expose new HTTP endpoints. `DbSizeInfoPlugin` is the only
built-in example.

Native plugins are `.so` files dropped into `~/.dmart/plugins/<name>/` with a
`config.json` describing filters (which subpaths, resource types, schemas,
and actions they care about). They export a small C ABI — `get_info()`,
`hook()` or `handle_request()`, `free_string()` — and can be written in any
language that produces a shared library. See `custom_plugins_sdk/` for
examples in C#, Rust, and C.

## Configuration

Configuration sources, in priority order (later wins):

1. `config.env` at `$BACKEND_ENV`, `./config.env`, or `~/.dmart/config.env`
2. Environment variables prefixed `Dmart__` (double underscore = nested path)

Configuration is strict: unknown keys in `config.env` cause startup to fail.
This is deliberate — silent typos (`DATABAE_HOST` vs `DATABASE_HOST`)
have burned us before.

All settings live in `Config/DmartSettings.cs`. If you're adding a setting,
add it there, add it to `config.env.sample`, and add validation in
`DmartSettingsValidator` if it has constraints.

## Directory guide

```
Api/                   HTTP endpoint handlers (Minimal API)
  Managed/             Authenticated CRUD, query, upload, workflow
  Public/              Unauthenticated query and submit
  User/                Login, logout, OTP, profile
  Info/                Manifest, settings
  Mcp/                 Model Context Protocol SSE bridge
  Qr/                  QR code generation
Auth/                  JWT, Argon2 password hashing, OTP, OAuth providers
Cli/                   Interactive CLI client (dmart cli subcommand)
Config/                DmartSettings, dotenv parser, validators
DataAdapters/Sql/      Postgres repositories and schema
Middleware/            CORS, CXB asset server, request logging, headers
Models/                Core domain types and API DTOs
  Api/                 Request/Response envelopes
  Core/                Entry, User, Role, Permission, Space
  Enums/               Domain enums with [EnumMember] for snake_case wire format
  Json/                DmartJsonContext — the source-gen JSON hub
Plugins/
  BuiltIn/             Compiled-in plugins
  Native/              .so loader and callback bridge
Services/              Domain logic — EntryService, QueryService, PermissionService, etc.
Utils/                 Shared helpers (QueryPolicies, etc.)

admin_scripts/         Docker and Ansible deployment aids
custom_plugins_sdk/    Sample plugin projects in C#, Rust, C
cxb/                   Embedded Svelte admin UI (source)
cat/                   Embedded Svelte user UI (source)
dist/                  RPM spec, systemd units, shell completions
dmart.Tests/           Unit and integration tests
plugins/               Built-in plugin config.json files (shipped with binary)
```

## What's deliberately excluded

These are choices made consciously that may look like omissions:

- **No EF Core, no Dapper, no ORM.** Npgsql directly. Queries are explicit SQL
  in `DataAdapters/Sql/*.cs`. This makes the SQL visible and trivially AOT-safe.
- **No MediatR, no CQRS.** A handler calls a service calls a repository. The
  abstractions we have earn their weight.
- **No AutoMapper.** Mapping between DTOs and domain types is hand-written.
  It's a few dozen lines in total and it trims cleanly.
- **No FluentValidation.** Validation lives in services or in
  `DmartSettingsValidator`. FluentValidation is reflection-heavy.
- **No Autofac or other third-party DI.** Built-in DI only.
- **No Serilog or NLog.** Built-in `ILogger` with an optional JSON console
  provider configured in `Program.cs`. Logs to stdout; systemd captures them.

Some of these might be worth revisiting if the case for them becomes strong.
None has a strong case today.

## Threading and concurrency

- **Stateless request handling.** Each request gets a fresh `RequestContext`
  (scoped); services are singletons. No request state leaks between requests
  through service fields.
- **Connection pooling via Npgsql.** Connections are acquired per operation
  and returned to the pool. Don't hold connections across async boundaries.
- **WebSocket broadcasts are fire-and-forget.** `WsConnectionManager` holds
  the live set; sends don't block request completion.
- **Plugin hooks run synchronously in the request path.** A slow plugin slows
  down every request matching its filters. This is a known trade-off — plugins
  that need to be async-dispatched should enqueue work and return immediately.

## Testing strategy

- **Unit tests** in `dmart.Tests/` cover services and utilities in isolation.
- **Integration tests** spin up the app via `WebApplicationFactory` and hit
  real endpoints against a test Postgres instance.
- **E2E smoke tests** via `curl.sh` — 80 checks covering the full API surface.
  Used in CI and for local verification.

If you add a new endpoint, add at least one integration test. If you touch the
wire format of any existing endpoint, the parity tests will catch you — don't
bypass them without understanding what you're breaking.

## When in doubt

- If you're about to add a library, check its trim warnings first.
- If you're about to use reflection, stop. There's a source-gen equivalent.
- If you're about to "clean up" an unusual pattern, check the comments. This
  codebase documents its battle scars inline — those patterns are load-bearing.

