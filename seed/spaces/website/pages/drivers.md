Integrate DMART with official client libraries.

## Python

**pydmart** Official Python client. pip install pydmart Usage

```python
from pydmart import DmartService

dmart = DmartService("http://localhost:8282")
await dmart.login("dmart", "change-me")

resp = await dmart.query({
    "type": "subpath",          # QueryType
    "space_name": "management",
    "subpath": "/users",
    "limit": 10,
})
for record in resp.records:
    print(record.shortname)
```

[PyPI →](https://pypi.org/project/pydmart/)

## C# / .NET

**Dmart.Client** Async C# client for .NET Standard 2.1, .NET 8 & .NET 10 (AOT-friendly). dotnet add package Dmart.Client Usage

```csharp
using Dmart.Client;
using Dmart.Models.Api;
using Dmart.Models.Enums;

using var client = new DmartClient("http://localhost:8282");
await client.LoginAsync("dmart", "change-me");

var resp = await client.QueryAsync(new Query
{
    Type = QueryType.Subpath,
    SpaceName = "management",
    Subpath = "/users",
    Limit = 10,
});

foreach (var record in resp.Records ?? [])
    Console.WriteLine(record.Shortname);
```

[NuGet →](https://www.nuget.org/packages/Dmart.Client)

## TypeScript / JavaScript

**@edraj/tsdmart** Fully typed client for Node, Deno, Bun, and browsers. npm install @edraj/tsdmart Usage

```typescript
import { Dmart, QueryType } from "@edraj/tsdmart";

Dmart.setBaseURL("http://localhost:8282");
await Dmart.login("dmart", "change-me");

const resp = await Dmart.query({
  type: QueryType.subpath,
  space_name: "management",
  subpath: "/users",
  limit: 10,
});
console.log(resp?.records?.map((r) => r.shortname));
```

[NPM →](https://www.npmjs.com/package/@edraj/tsdmart)

## Dart / Flutter

**dmart** Native Dart package for cross-platform apps. flutter pub add dmart Usage

```
import 'package:dmart/dmart.dart';

final dmart = Dmart(baseUrl: 'http://localhost:8282');
await dmart.login('dmart', 'change-me');

final resp = await dmart.query(QueryRequest(
  type: QueryType.subpath,
  spaceName: 'management',
  subpath: '/users',
  limit: 10,
));
for (final record in resp.records) {
  print(record.shortname);
}
```

[pub.dev →](https://pub.dev/packages/dmart)
