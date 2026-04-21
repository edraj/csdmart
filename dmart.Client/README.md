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

## Dependency injection

Construct the client with an externally-owned `HttpClient` (typed-client
pattern):

```csharp
services.AddHttpClient("dmart", c => c.BaseAddress = new Uri("http://..."));
services.AddSingleton<DmartClient>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("dmart");
    return new DmartClient("http://localhost:8282", http);
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

## License

MIT.
