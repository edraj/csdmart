# Dmart.Models

Wire-format data types (`Record`, `Query`, `Response`, enums, …) for the
[dmart](https://github.com/edraj/csdmart) structured CMS/IMS. Shared
between the dmart server and any C# client SDK —
[`Dmart.Client`](https://www.nuget.org/packages/Dmart.Client) is the
primary consumer.

## Install

```sh
dotnet add package Dmart.Models
```

## Usage

```csharp
using Dmart.Models.Api;
using Dmart.Models.Enums;

var query = new Query
{
    Type = QueryType.Subpath,
    SpaceName = "management",
    Subpath = "/users",
    Limit = 10,
};
```

Most consumers will go through `Dmart.Client` — install that package
instead if you want the ready-made HTTP client:

```sh
dotnet add package Dmart.Client
```

## 1.0.7 — `IDmartData` contract

Adds `Dmart.Models.Contracts.IDmartData` — the backend-neutral interface
implemented by both `Dmart.Client.DmartClient` and
`Dmart.SqlAdapter.DmartSqlAdapter`, so consumers can swap HTTP ↔ direct-DB
backends via DI. Also adds the shared `Dmart.Models.Core.HistoryRow` and the
`Dmart.Models.Api.DmartException` hierarchy (`DmartNotFoundException`,
`DmartConflictException`, `DmartValidationException`,
`DmartPermissionDeniedException`).

## License

MIT.
