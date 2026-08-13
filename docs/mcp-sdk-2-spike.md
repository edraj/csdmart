# Spike: the official MCP C# SDK 2.0 under dmart's AOT rule

**Question asked:** does `ModelContextProtocol.AspNetCore` 2.0 survive dmart's
Native AOT publish, where ILC warnings are errors?

**Answer: yes** — with three specific accommodations, all recorded below.

**Status: findings only.** The spike branch (`spike/mcp-sdk-2`, commit
`6ea8a4b`, 2026-07-29) was deleted after this was written; it was 119 lines
explicitly marked *not for merge*, based on a master ~9 months stale. Nothing
here says dmart *should* adopt the SDK — only what adopting it would cost, and
what the spike deliberately left unanswered.

---

## Why this is worth keeping

dmart hand-rolls its MCP server (`Api/Mcp/`). The recurring question is whether
to move to the official SDK, and the first gate is always the same one every
third-party dependency faces here: **the 100%-AOT / no-reflection rule**.

That gate has now been tested twice, with opposite results:

| Library | Verdict |
|---|---|
| Parquet.Net (any version) | **fails** — reflection inside the library; see [`parquet-export-design.md`](./parquet-export-design.md) §2 |
| MCP SDK 2.0 | **passes**, with the accommodations below |

Worth recording precisely because the results differ. The rule is not
automatically fatal to third-party libraries — it depends entirely on whether
the library reflects, and whether its reflection can be routed around.

## What the spike built

One tool (`dmart_query_sdk`) expressed the SDK's way and mounted at `/mcp-sdk`,
**alongside** the hand-rolled server rather than replacing it, so an AOT publish
exercised the SDK without changing what `/mcp` served.

`dmart_query` was chosen as the representative case on purpose: it has
everything that would break AOT if anything would — nested collections and
`[EnumMember]` enums in the input (so the SDK must build a JSON schema for
them), a DI-resolved service, actor-derived authorization, and dmart's own
`Response` envelope as the return type (so the SDK must serialize a type our
source-gen context owns).

## The three accommodations

1. **Register tools explicitly.** `WithTools<T>()`, never
   `WithToolsFromAssembly()` — the latter scans by reflection and is `IL2026`
   under AOT.

2. **Every type crossing the SDK boundary needs a source-generated context.**
   The SDK serializes tool arguments and results through its own resolver chain
   (`McpJsonUtilities` + `AIJsonUtilities`), which has no reflection fallback
   under AOT. A missing type throws at **startup**, while building tool schemas
   — not on first call. The cost is one context entry per tool argument and
   result type, which is how dmart already works everywhere else
   (`DmartJsonContext`), so it is idiomatic rather than a new burden.

3. **Hand the SDK a mutable copy of its options, and restate the naming
   policy.** `McpJsonUtilities.DefaultOptions` is frozen once used, so the
   context must be prepended to a copy. And the SDK's options — not the
   context's `JsonSourceGenerationOptions` — decide property naming, so
   dmart's snake_case wire contract has to be set there explicitly. Miss it and
   the *same* `Response` type serializes as `resource_type` over REST and
   `resourceType` over MCP.

## What the spike deliberately did NOT settle

These are the real cost of a migration, and the spike is explicit that it did
not touch them:

- **Authorization.** The hand-rolled server calls `RequireActor(http)` and lets
  the caller's JWT flow into every service call. The spike took the actor from
  `IHttpContextAccessor` — it works, but it is ambient. A real port has to
  decide how MCP requests carry identity under a transport that is "stateless
  by default" in 2.0.
- **Session binding.** `McpEndpoint.ResolveOwnedSession` ties `Mcp-Session-Id`
  to the authenticated caller (the #133 fix). The SDK has no equivalent notion.
  **Reproducing that is the hard part of a real port** — not the tool
  translation this spike demonstrated.

## What adoption would buy

Typed arguments: the SDK derives each tool's input schema from a record, with
`[Description]` on the properties. The hand-rolled equivalent
(`McpTools.QueryAsync`) still pulls the same fields out of `JsonElement` by
hand — nine such calls in that one method, and a schema maintained separately
from the code that reads it.

That is the whole case for adopting it, and it is a real one. Whether it
outweighs re-solving authorization and session binding is the open question.

## If you want the code back

The branch is gone but the commit survives until git garbage-collects it:

```
git switch -c spike/mcp-sdk-2 6ea8a4bfd552edd5deede0418b4b9ec00e3053ec
```

It would need rebasing regardless — its `dmart.csproj` predates both the SQLite
native-library pin and the `InternalsVisibleTo` for the test project.
