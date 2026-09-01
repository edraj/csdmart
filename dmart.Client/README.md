# Dmart.Client

Async C# client for the [dmart](https://github.com/edraj/csdmart)
structured CMS/IMS HTTP API. Mirrors
[pydmart](https://github.com/edraj/pydmart) — same method surface,
same wire shapes, idiomatic `async`/`await` in C#.

## Install

```sh
dotnet add package Dmart.Client
```

`Dmart.Models` (the shared wire types) comes along as a dependency.

## Method surface

`Dmart.Client` mirrors the same dmart HTTP surface that
[`pydmart`](https://github.com/edraj/pydmart) and
[`tsdmart`](https://github.com/edraj/tsdmart) wrap — feature parity by
method name and response shape across all the dmart language SDKs.
[`Dmart.SqlAdapter`](../Dmart.SqlAdapter/README.md) exposes the same method
names for everything that's DB-feasible, so a consumer can swap
backends by changing the constructor.

| Feature                       | Client          | SqlAdapter (DB) |
|-------------------------------|-----------------|-----------------|
| **Auth (Client-only)**        |                 |                 |
| `LoginAsync` / `LoginByAsync` | yes             | n/a             |
| `LogoutAsync`                 | yes             | n/a             |
| `CreateUserAsync` (register)  | yes             | n/a             |
| `UpdateUserAsync`             | yes             | n/a             |
| `CheckExistingAsync`          | yes             | n/a             |
| `GetProfileAsync`             | yes             | yes (`actor` is "self") |
| `OtpRequestAsync` / `OtpRequestLoginAsync` | yes | n/a            |
| `PasswordResetRequestAsync` / `ConfirmOtpAsync` | yes | n/a       |
| `ValidatePasswordAsync`      | yes             | n/a             |
| `DeleteAccountAsync`          | yes             | n/a             |
| `GoogleMobileLoginAsync` / `FacebookMobileLoginAsync` / `AppleMobileLoginAsync` | yes | n/a |
| **Entry CRUD (both)**         |                 |                 |
| `LoadAsync` / `LoadOrNoneAsync` / `IsEntryExistAsync` | yes | yes |
| `GetByUuidAsync` / `GetBySlugAsync` | yes        | yes             |
| `GetEntryByCriteriaAsync`     | yes (best-effort) | yes           |
| `GetSchemaAsync`              | yes             | yes             |
| `CreateAsync` / `UpdateAsync` / `SaveAsync` | yes | yes          |
| `DeleteAsync` / `MoveAsync`   | yes             | yes             |
| `RequestAsync`                | yes (raw envelope) | n/a — use typed CRUD |
| `RetrieveEntryAsync`          | yes (raw envelope) | `LoadAsync` does the typed version |
| **Query (both)**              |                 |                 |
| `QueryAsync` / `QueryEntriesAsync` | yes        | `QueryAsync` returns `(Total, Records)` |
| `GetSpacesAsync` / `LoadSpacesAsync` | yes      | `GetSpacesAsync` (typed dict) |
| `FetchSpaceAsync`             | yes             | yes             |
| `GetChildrenAsync`            | yes             | yes             |
| `LoadUserMetaAsync`           | yes             | yes             |
| `GetUserPermissionsAsync`     | n/a — read `roles` on the profile | yes (DB cache) |
| `QueryHistoryAsync`           | yes (uses `QueryType.History`) | yes (`histories` table) |
| **Locks (both)**              |                 |                 |
| `LockEntryAsync` / `UnlockEntryAsync` | yes     | n/a — use `TryLockAsync` |
| `TryLockAsync` / `UnlockAsync`| yes (parity-shape over endpoints) | yes |
| `GetLockerAsync`              | n/a — no HTTP endpoint | yes      |
| **Multipart / bulk (Client)** |                 |                 |
| `UploadWithPayloadAsync`      | yes             | n/a — files served by server |
| `PublicAttachAsync`           | yes             | n/a             |
| `CsvAsync` / `ResourcesFromCsvAsync` | yes      | n/a — server-orchestrated |
| `ImportAsync` / `ExportAsync` | yes             | n/a             |
| **Workflow / submit (Client)** |                |                 |
| `ProgressTicketAsync`         | yes             | n/a — workflow plugin dispatch |
| `SubmitAsync`                 | yes             | n/a             |
| **Public reads (Client)**     |                 |                 |
| `PublicQueryGetAsync` / `PublicExecuteAsync` | yes | n/a — call DB directly |
| **Payload (Client)**          |                 |                 |
| `GetPayloadAsync` / `GetAttachmentUrl` | yes    | n/a — DB doesn't serve URLs |
| `FetchDataAssetAsync`         | yes             | n/a — server query engine |
| **Info (Client)**             |                 |                 |
| `GetInfoMeAsync` | yes | n/a — server-side identity |
| `GetManifestAsync` / `GetSettingsAsync` | yes | n/a — both require super_admin |
| `GetSpaceHealthAsync`         | yes             | n/a — server orchestration |
| **Admin (Client)**            |                 |                 |
| `ReindexEmbeddingsAsync` / `ApplyAlterationAsync` | yes | n/a       |
| `ReloadSecurityDataAsync` / `SemanticSearchAsync` | yes | n/a       |
| `GetShortLinkAsync` / `GetShorteningAsync` | yes | n/a              |
| `SendMessageAsync` / `BroadcastToChannelsAsync` / `GetWsInfoAsync` | yes | n/a |

Auth methods live exclusively on `Dmart.Client` — authentication is a
server concern, and a direct-DB caller is already inside the trust
boundary. Server-orchestrated features (workflow, plugin dispatch,
file payloads, manifest, etc.) similarly stay HTTP-only.

## Usage

```csharp
using Dmart.Client;
using Dmart.Models.Api;
using Dmart.Models.Enums;

using var client = new DmartClient("http://localhost:8282");

// Authenticate — token is cached on the instance and attached to
// every subsequent request.
await client.LoginAsync("dmart", "change-me");

// Query users.
var resp = await client.QueryAsync(new Query
{
    Type = QueryType.Subpath,
    SpaceName = "management",
    Subpath = "/users",
    Limit = 10,
});

foreach (var record in resp.Records ?? [])
    Console.WriteLine(record.Shortname);

await client.LogoutAsync();
```

## Attribute bags and numbers

`Record.Attributes`, `Request.Attributes` and `Response.Attributes` are
`Dictionary<string, object>`, so values are resolved by their **runtime** type.
On net8.0+ the client serializes through a source-generated context, which means
the set of types an attribute bag can carry is closed:

- every JSON scalar — `string`, `bool`, `int`, `long`, `double`, `decimal`,
  `float`, `short`, `ushort`, `byte`, `sbyte`, `uint`, `ulong`, `Guid`,
  `DateTime`, `DateTimeOffset`, `JsonElement`
- the common collections — `object[]`, the primitive arrays, `List<object>`,
  `List<string>`/`List<int>`/`List<long>`/`List<double>`/`List<decimal>`/`List<bool>`,
  `Dictionary<string, object>`, `Dictionary<string, string>`

Dictionary **keys** go on the wire exactly as you wrote them. The snake_case
wire convention applies to model property names (`space_name`, `resource_type`),
not to keys you choose: an attribute called `myKey` is stored and read back as
`myKey`.

Anything else — including your own POCOs — must be handed over as a
`JsonElement`, serialized against **your** context so trimming and AOT stay
sound:

```csharp
Attributes = new Dictionary<string, object>
{
    ["payload"] = JsonSerializer.SerializeToElement(myModel, MyContext.Default.MyModel),
};
```

**Reading numbers back.** The client hands you JSON numbers as `JsonElement`s
and never reformats them: what the server sent is what you get, byte for byte.
A field that reads back as `10240.0` was *stored* that way — Python renders
every float like that (`json.dumps(10240.0)` -> `"10240.0"`), as does .NET for a
`decimal` carrying scale, so one field can arrive as `10240.0` while its integer
siblings arrive clean.

That form is a problem for `int`, because **JSON Schema and System.Text.Json
disagree about it**. dmart validates payload bodies with JSON Schema, where
`"type": "integer"` means "a number with a zero fractional part" — so `10240.0`
is a valid integer, and dmart accepts, stores and returns it. `JsonSerializer`
refuses to read it into an `int`:

```
JsonException: The JSON value could not be converted to System.Int32.
```

Left alone, that makes the client stricter than the server it talks to. Two
converters close the gap, accepting exactly what the schema calls an integer and
nothing wider — a real fraction is still rejected (checked against the raw token,
so a fraction below `decimal`'s precision cannot round itself away), and
comparison runs through `decimal` so values past 2^53 keep every digit. They
respect whatever `JsonNumberHandling` you have set, so registering them does not
quietly cancel `AllowReadingFromString` or `WriteAsString` for `int` and `long`:

```csharp
using Dmart.Client.Json;

public sealed class Grant
{
    [JsonPropertyName("data")]
    [JsonConverter(typeof(IntegralInt32Converter))]   // reads 10240.0 as 10240
    public int Data { get; set; }
}
```

The attribute form needs no options plumbing and stays trim/AOT-safe. For a
whole body, register them instead — System.Text.Json wraps them for `int?` and
`long?` automatically:

```csharp
var options = new JsonSerializerOptions
{
    Converters = { new IntegralInt32Converter(), new IntegralInt64Converter() },
};
var grant = entry.Payload!.Body!.Value.Deserialize<Grant>(options);
```

`DmartClient.DefaultJsonOptions` and the source-gen context both carry them, so
the client's own types read the same way on every target framework. Writing is
unaffected: a round trip puts a plain integer back on the wire, never `10240.0`.

## Error handling

Any non-success envelope (`status=failed`), non-2xx HTTP response with a
parsable error payload, or transport-level failure throws
`DmartException`:

```csharp
try
{
    await client.LoginAsync("nope", "wrong");
}
catch (DmartException ex)
{
    // ex.StatusCode — HTTP status (or 0 on transport errors)
    // ex.Error.Type / Code / Message — server's api.Error envelope
}
```

## Configuration

The constructor accepts three styles, mirroring `Dmart.SqlAdapter`'s
options pattern so the two SDKs configure the same way.

### Style 1 — Raw URL (simplest)

```csharp
using var client = new DmartClient("http://localhost:8282");
```

### Style 2 — Strongly-typed `DmartClientOptions`

Use when binding from `IConfiguration` / `appsettings.json` or when you
need to set a default bearer token, request timeout, or default headers
at construction time.

```csharp
using var client = new DmartClient(new DmartClientOptions
{
    BaseUrl        = "http://localhost:8282",
    AuthToken      = "preset-bearer-or-null",   // optional
    Timeout        = TimeSpan.FromSeconds(30),   // optional
    DefaultHeaders =                             // optional
    {
        ["X-Tenant-Id"] = "tenant-42",
    },
});
```

| Property         | Type                              | Default | Notes |
|------------------|-----------------------------------|---------|-------|
| `BaseUrl`        | string?                           | `null`  | Required. Falls back to `DMART_BASE_URL` env var when unset. Trailing slashes are stripped. |
| `AuthToken`      | string?                           | `null`  | Pre-set bearer for service-to-service calls. `LoginAsync` overwrites it. |
| `Timeout`        | TimeSpan?                         | `null`  | Applied only when DmartClient owns its HttpClient (i.e., NOT when you pass your own via the typed-client pattern). |
| `DefaultHeaders` | Dictionary&lt;string, string&gt;  | empty   | Added to every outgoing request via `TryAddWithoutValidation` so non-standard names like `X-…` are accepted. |

### Style 3 — Environment variable only

The simplest "containerized service" case: export the URL, construct
with default options, no config-binding needed.

```sh
export DMART_BASE_URL=https://dmart.example.com
```

```csharp
using var client = new DmartClient(new DmartClientOptions());
```

If neither `BaseUrl` nor `DMART_BASE_URL` is set, the constructor
throws `InvalidOperationException` so the misconfiguration surfaces at
startup rather than as a mysterious 404 on the first request.

### `appsettings.json` example

```json
{
  "Dmart": {
    "BaseUrl": "https://dmart.example.com",
    "Timeout": "00:00:30"
  }
}
```

ASP.NET's standard `IConfiguration` binding picks env vars up out of
the box. Use `:` (POSIX) or `__` (Docker) as the path separator:

```sh
export Dmart__BaseUrl=https://dmart.example.com
export Dmart__AuthToken=$(cat /run/secrets/dmart_service_token)
```

## Dependency injection

### Minimal-API host (`Program.cs`)

```csharp
using Dmart.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DmartClientOptions>(
    builder.Configuration.GetSection("Dmart"));
builder.Services.AddSingleton<DmartClient>(sp =>
    new DmartClient(sp.GetRequiredService<IOptions<DmartClientOptions>>().Value));
```

### Typed-client (`IHttpClientFactory`) pattern

Lets ASP.NET manage the underlying `HttpClient` lifetime (handler
rotation, DNS refresh, Polly policies). `DmartClient` does not apply
`Timeout` in this mode — configure that on the `HttpClient` itself.

```csharp
services.Configure<DmartClientOptions>(config.GetSection("Dmart"));
services.AddHttpClient("dmart", (sp, c) =>
{
    var opts = sp.GetRequiredService<IOptions<DmartClientOptions>>().Value;
    c.BaseAddress = new Uri(opts.ResolveBaseUrl());
});
services.AddSingleton<DmartClient>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<DmartClientOptions>>().Value;
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("dmart");
    return new DmartClient(opts, http);
});
```

`HttpClient` disposal stays with the caller in that mode; the default
constructor (`new DmartClient("url")`) owns its HttpClient and disposes
it when the client is disposed.

## Supported frameworks

- `netstandard2.1` — .NET Framework 4.8+ / Mono / Xamarin via the
  standard bridge.
- `net8.0` — LTS .NET 8.
- `net10.0` — current .NET.

## 1.0.7 — Interchangeable backends

`Dmart.Client.DmartClient` and `Dmart.SqlAdapter.DmartSqlAdapter` now both
implement `Dmart.Models.Contracts.IDmartData`, so an ASP.NET app can depend on
the interface and swap HTTP ↔ direct-DB backends via DI.

**Breaking changes:**

- `DmartException` (and `DmartPermissionDeniedException`) moved to namespace
  `Dmart.Models.Api`, and a typed hierarchy was added: `DmartNotFoundException`,
  `DmartConflictException`, `DmartValidationException`,
  `DmartPermissionDeniedException` (all `: DmartException`).
  `catch (DmartException)` still works; update `using` directives.
- `HistoryRow` moved to `Dmart.Models.Core.HistoryRow` (shared by both SDKs).
- The typed query/spaces/children/profile methods are exposed under the
  interface names `QueryEntriesAsync` / `LoadSpacesAsync` /
  `GetChildrenEntriesAsync` / `GetProfileAsync(actor)`; the client's
  `QueryAsync` / `GetSpacesAsync` / `GetChildrenAsync` / `GetProfileAsync()`
  keep their `Response`-shaped contract for back-compat.

**Behavior changes:**

- `LoadUserMetaAsync` now requests the payload body
  (`retrieve_json_payload=true`), so `User.Payload.Body` is populated where it
  was previously `null` — matching the SQL adapter, which reads the whole
  payload column. Responses are marginally larger.
- `GetProfileAsync(actor)` (the `IDmartData` overload) reads `GET
  /user/profile` — the caller's own profile, identified by the bearer token;
  the `actor` argument is ignored. The profile endpoint doesn't project
  `uuid`/`space_name`/`owner_shortname`, so those `User` fields are
  synthesized (`""`/`"management"`/`""`). Use `LoadUserMetaAsync` (requires
  managed read permission) when you need the full row.

## License

MIT.
