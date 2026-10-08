DMART ships with a plugin system that lets you extend the platform without forking it. Plugins come in two flavours: **hook plugins** react to before/after action events, and **API plugins** mount their own HTTP routes. They can be compiled into the binary as managed C# classes, or loaded at runtime as native plugins.

## Built-in Plugins

These managed plugins are compiled into the single self-contained binary and registered at startup. The tag shows whether each is a hook or an API plugin.

**audit** Records create, update, and delete actions for audit trails. Hook

**semantic_indexer** Generates and maintains pgvector embeddings so entries can be found by semantic (vector) search. Hook

**realtime_updates_notifier** The live notification path: for each matching event it computes the set of subscription channels (walking subpath prefixes × schema × action × state) and broadcasts a `notification_subscription` message directly to connected clients via the in-process WebSocket manager. Hook

**mcp_sse_bridge** Bridges entry events to the MCP server so Model Context Protocol clients receive live updates. Hook

**resource_folders_creation** Automatically provisions the folder structures a new user or space needs. Hook

**admin_notification_sender** Registered no-op stub. The Python original pushed admin notifications through an SMS/push gateway (Firebase/SMS); that gateway is out of scope for the port, so `HookAsync` only logs today. Hook Stub

**system_notification_sender** Registered no-op stub. The original fanned out per-user notification entries and pushed via the gateway; the query + push sides aren't ported yet, so it only logs. Hook Stub

**local_notification** Registered no-op stub. The real body needs the notification-persistence pipeline (attachment-first notification entries) which hasn't been ported; activating it in a space is a no-op today. Hook Stub

**db_size_info** Exposes an endpoint reporting PostgreSQL storage usage for the deployment. API

The three notification-sender plugins are marked _Stub_: they are registered only so `config.json` references don't warn `PLUGIN_UNKNOWN`, but their hook body is a no-op pending a push/SMS gateway integration. The one live notification path today is `realtime_updates_notifier`, which broadcasts changes over WebSocket.

## Defining Custom Plugins

There are two ways to add your own plugins, each suited to a different workflow.

**Managed C# plugins** Implement `IHookPlugin` or `IApiPlugin` in-process. Fastest and fully typed, but compiled into the binary.

**Native plugins** Dropped into `~/.dmart/plugins/<name>/` and loaded at runtime — no recompilation needed. Ship them as a subprocess executable or a shared library.

Each plugin keeps a `config.json` alongside it that declares its `shortname`, whether it is a `hook` or `api` plugin, and — for hooks — the event filters and ordering that decide when it runs. Ready-made examples live in the `custom_plugins_sdk/` directory.

## Configuration

The `config.json` file defines the plugin's metadata and behavior.

```json
{
  "shortname": "my_custom_plugin",
  "is_active": true,
  "type": "hook",
  "listen_time": "after",
  "ordinal": 10,
  "filters": {
    "subpaths": { "__all_spaces__": ["__all_subpaths__"] },
    "resource_types": ["content", "user"],
    "schema_shortnames": [],
    "actions": ["create", "update"]
  }
}
```

| Key | Description |
|---|---|
| `shortname` | Unique identifier for the plugin |
| `is_active` | Set to `true` to enable the plugin |
| `type` | `hook` — reacts to system events `api` — adds new API endpoints |
| `listen_time` | `before` — execute before action (can block/modify) `after` — execute after action (logging, notifications) |
| `ordinal` | Execution order relative to other plugins (lower = first) |
| `filters` | Event filters. `subpaths` is a map of space → subpath patterns (`__all_spaces__` / `__all_subpaths__` match any space / subpath). For `resource_types`, `schema_shortnames`, and `actions`, an empty array `[]` matches everything. |

## Implementation

### Hook Plugins

A managed hook plugin implements `IHookPlugin`: a `Shortname` and an async `HookAsync(Event, CancellationToken)` method. It is invoked on before/after action events that match its filters.

```csharp
using Dmart.Models.Core;
using Dmart.Plugins;

public sealed class MyHookPlugin : IHookPlugin
{
    public string Shortname => "my_custom_plugin";

    public async Task HookAsync(Event e, CancellationToken ct = default)
    {
        // Your custom logic here
        Console.WriteLine("Event: " + e.ActionType + " on " + e.ResourceType);
        await Task.CompletedTask;
    }
}
```

The `Event` carries details about the action: `SpaceName`, `Subpath`, `ResourceType`, `ActionType`, `UserShortname`, and `Attributes`. Throwing from a `before` hook aborts the action; a `after` hook that throws is logged but does not fail the request.

### API Plugins

A managed API plugin implements `IApiPlugin`: a `Shortname` plus a `MapRoutes` method that wires endpoints into a route group. The plugin manager mounts that group at `/{shortname}` during startup.

```csharp
using Dmart.Plugins;

public sealed class MyApiPlugin : IApiPlugin
{
    public string Shortname => "my_custom_plugin";

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.MapGet("/hello", () =>
            Results.Ok(new { message = "Hello from my custom plugin!" }));
    }
}
```

Endpoints are served under `/{shortname}/...` — the example above answers on `/my_custom_plugin/hello`.

### Native Plugins (runtime-loadable)

To extend a running deployment without recompiling the binary, drop a plugin into `~/.dmart/plugins/<name>/`. DMART supports two native modes and prefers the executable when both are present.

**Subprocess (recommended)** A standalone executable in any language. DMART speaks JSON-lines over stdin/stdout; if the process crashes it is respawned automatically, so it can never take the server down.

**Shared library** A `.so`, `.dylib`, or `.dll` loaded in-process via a small C ABI (`get_info`, `hook`, `handle_request`, `free_string`, `init`, `dmart_plugin_version`). Lowest latency, but a crash takes down the host.

The subprocess protocol is a simple request/response over JSON lines:

```
# dmart → plugin (stdin)      # plugin → dmart (stdout)
{"type":"info"}                {"shortname":"my_plugin","version":"1.0.0","type":"hook"}
{"type":"hook","event":{...}}  {"status":"ok"}
{"type":"request","request":{...}}  {"status":"success","attributes":{...}}
```

Working templates for both modes — plus higher-level examples — live in the `custom_plugins_sdk/` directory of the repository.
