# dmart External Plugin Development Guide

Build external plugins that dmart loads at runtime — no dmart recompilation needed.

## How a plugin works

Your plugin is a standalone **executable**. dmart starts it, then talks to it
over stdin/stdout in JSON lines — one line per message. If it crashes, dmart
respawns it on the next event, so a fault in your code cannot take the host
down.

That means any language: Python, Node, Go, Rust, C#, even bash. Round-trip
latency is roughly 0.1ms.

> **In-process `.so` plugins have been removed.** dmart used to also load
> shared libraries directly via `dlopen`, which was faster (~1us) but ran
> third-party code inside the host process — a segfault there killed dmart —
> and could not work at all in a static build. Everything those plugins could
> do, including every callback into dmart, is available here. If you have a
> `.so` deployed, dmart now reports it as a load failure at startup and on
> `GET /info/plugins`; port it using this guide.

## Writing a plugin

### Protocol

dmart sends one JSON line per message to your plugin's stdin. Your plugin writes one JSON line response to stdout.

**Message types:**

```
→ stdin:  {"type":"info","host":{"callbacks":1}}
← stdout: {"shortname":"my_plugin","version":"1.0.0","type":"hook"}

→ stdin:  {"type":"hook","event":{...}}
← stdout: {"status":"ok"}

→ stdin:  {"type":"request","request":{...}}
← stdout: {"status":"success","attributes":{...}}
```

Debug output goes to stderr (forwarded to dmart's console).

**The `host` object in the info frame tells you what this dmart supports.**
`"callbacks":1` means callback frames will be answered — see
[Calling back into dmart](#calling-back-into-dmart-subprocess). Older dmart
builds send a bare `{"type":"info"}`; treat a missing `host` as "no callbacks"
and skip them, because a callback frame sent to such a host is read as your
final response and desynchronises the exchange.

**`version` is optional but recommended.** Surface your plugin's version so operators can see it on `GET /info/plugins` and in dmart's startup log (`SUBPROCESS_PLUGIN_REGISTERED: my_plugin v1.0.0 (hook) …`). Source the literal from your build artifact rather than hand-maintaining it in code: read it from `package.json`/`__version__`/the value linked by `go build -ldflags "-X main.Version=..."`. Absent versions resolve to `"0.0.0"`.

### Quick Start (Python)

```python
#!/usr/bin/env python3
import json, sys

for line in sys.stdin:
    msg = json.loads(line.strip())

    if msg["type"] == "info":
        print(json.dumps({"shortname": "my_hook", "version": "1.0.0", "type": "hook"}), flush=True)

    elif msg["type"] == "hook":
        event = msg["event"]
        print(f"[my_hook] {event['action_type']} {event['space_name']}/{event.get('shortname','?')}", file=sys.stderr)
        print(json.dumps({"status": "ok"}), flush=True)
```

### Quick Start (Bash)

```bash
#!/bin/bash
while IFS= read -r line; do
    type=$(echo "$line" | jq -r '.type')
    case "$type" in
        info)    echo '{"shortname":"bash_hook","version":"1.0.0","type":"hook"}' ;;
        hook)    echo '{"status":"ok"}' ;;
        request) echo '{"status":"success","attributes":{"hello":"world"}}' ;;
    esac
done
```

### Quick Start (Node.js)

```javascript
#!/usr/bin/env node
const readline = require('readline');
const rl = readline.createInterface({ input: process.stdin });

rl.on('line', line => {
    const msg = JSON.parse(line);
    if (msg.type === 'info')
        console.log(JSON.stringify({shortname: 'node_hook', version: '1.0.0', type: 'hook'}));
    else if (msg.type === 'hook')
        console.log(JSON.stringify({status: 'ok'}));
});
```

### Deploy

```bash
mkdir -p ~/.dmart/plugins/my_hook

# Copy your script/binary as the plugin executable (name matches directory)
cp my_hook.py ~/.dmart/plugins/my_hook/my_hook
chmod +x ~/.dmart/plugins/my_hook/my_hook

# Add config — see "Filter shape" below for the full vocabulary.
cat > ~/.dmart/plugins/my_hook/config.json << 'EOF'
{
  "shortname": "my_hook",
  "is_active": true,
  "type": "hook",
  "listen_time": "after",
  "filters": {
    "subpaths": { "__all_spaces__": ["__all_subpaths__"] },
    "resource_types": ["content"],
    "schema_shortnames": [],
    "actions": ["create", "update", "delete"]
  }
}
EOF

# Restart dmart
# Look for: SUBPROCESS_PLUGIN_REGISTERED: my_hook (hook)
```

### API plugins

Same protocol, but respond to `{"type":"request","request":{...}}`:

```python
#!/usr/bin/env python3
import json, sys

for line in sys.stdin:
    msg = json.loads(line.strip())

    if msg["type"] == "info":
        print(json.dumps({
            "shortname": "my_api",
            "version": "1.0.0",
            "type": "api",
            "routes": [
                {"method": "GET", "path": "/"},
                {"method": "GET", "path": "/greet/{name}"}
            ]
        }), flush=True)

    elif msg["type"] == "request":
        req = msg["request"]
        path = req.get("path", "/")
        user = req.get("user", "anonymous")

        if "/greet/" in path:
            name = path.split("/greet/")[1].rstrip("/")
            print(json.dumps({"status": "success", "attributes": {"greeting": f"Hello, {name}!"}}), flush=True)
        else:
            print(json.dumps({"status": "success", "attributes": {"plugin": "my_api", "user": user}}), flush=True)
```

#### Returning binary from an API plugin

To answer with something that isn't JSON — a generated PDF, an image — wrap the
bytes instead of returning a normal response:

```json
{"binary": true, "content_type": "application/pdf",
 "body_b64": "JVBERi0xLjQK…", "filename": "report.pdf"}
```

dmart decodes it and streams the bytes with that content-type, adding a
`Content-Disposition: attachment` header when `filename` is present. Anything
without `"binary": true` flows through as JSON exactly as usual.

### Handling calls in parallel (`workers`)

dmart runs **one** process per plugin by default and sends it one message at a
time. That is not incidental: the hook and request frames carry no correlation
id, so a reply is matched to its request by arrival order, and two exchanges
sharing one pipe could each read the other's answer. Serialising is what makes
the protocol safe — and it is why your plugin can stay a simple
`while line in stdin: handle; print` loop.

To handle calls concurrently, ask for more processes in `config.json`:

```json
{ "shortname": "my_plugin", "is_active": true, "type": "hook", "workers": 4 }
```

dmart then starts 4 copies and dispatches each call to whichever is free.
**Your plugin does not change** — each worker still sees one message at a time.

The catch is state. Anything your plugin keeps between calls — a counter, a
cache, a warm connection — now exists once *per worker*, and consecutive calls
need not land on the same one. If that matters, keep the state in dmart (via
the `save_entry` callback) rather than in the process, or leave `workers` at 1.

Every worker gets its own `{"type":"info"}` handshake as it starts, including
when one is restarted after a crash, so they cannot disagree about what the
host supports. The allowed range is 1-32; anything outside is clamped and
logged.

### Calling back into dmart

Before writing its final response, your plugin can send any number of
**callback frames** — requests back into dmart. dmart answers each on your
stdin, then you carry on and finish the exchange normally.

```
← stdout: {"type":"callback","id":1,"op":"query","args":{"type":"search","space_name":"acme"}}
→ stdin:  {"type":"callback_result","id":1,"ok":true,"result":{"status":"success","records":[...]}}
← stdout: {"status":"ok"}                     ← the response, as usual
```

`id` is echoed back verbatim — use whatever you already correlate on.

**`ok` is about the frame, not the operation.** `ok:false` means dmart could
not understand the callback (unknown `op`, missing args) and you should fix or
rebuild the plugin. An operation that ran and failed comes back as `ok:true`
with the failure inside `result` — an `{"error":…}` document, or a non-zero
`code`. Keeping the two separate is what lets you tell "dmart doesn't
understand me" (rebuild needed) from "the save didn't work" (retry or report).

| `op` | `args` | `result` |
|------|--------|----------|
| `load_entry` | `space`, `subpath`, `shortname`, `resource_type` (optional) | the entry, or `{"entry":null}` |
| `load_user` | `shortname` | the user, or `{"user":null}` |
| `save_entry` | `entry` (the entry object) | `{"code":0}` |
| `update_user` | `user` (the user object) | `{"code":0}` |
| `send_email` | `to`, `subject`, `html` | `{"code":0}` |
| `ws_broadcast` | `channel`, `message` | `{"code":0}` |
| `query` | the query document itself | a standard response envelope |
| `log` | `level` (0–6), `category` (optional), `message` | `{"code":0}` |
| `get_session_firebase_tokens` | `shortname`, `inactivity_ttl_seconds` (optional) | `["token", …]` |
| `get_media_attachment` | `space`, `subpath`, `shortname` | `{"media_b64":…,"length":N}`, or `{"media":null}` on a miss |

`get_media_attachment` base64-encodes the blob, so it costs about 33% more
bytes on the wire than the file itself. A miss is `{"media":null}` rather than
an empty string — an absent attachment and a zero-byte one are not the same
thing.

`code` is `0` for ok, non-zero for error.

**`query` runs as the user that triggered your hook**, so it sees exactly what
that user is allowed to see. Add `"as_actor"` to the query document to override:
a string impersonates that user, JSON `null` runs as system with no ACL filter.
Omit it unless you mean it.

Two limits keep a misbehaving plugin from wedging dmart: **256 callbacks per
exchange**, and a **30s timeout per line** (not per exchange — a long honest
chain of callbacks is fine, going silent for 30s is not). Exceeding either kills
the process; it respawns on the next event.

A callback must not re-enter your own plugin — one stdio pipe cannot carry two
exchanges at once, so dmart rejects the nested call with
`{"status":"error","message":"reentrant plugin call rejected"}` rather than
corrupting both.

```python
import json, sys

def callback(op, args, _id=[0]):
    _id[0] += 1
    print(json.dumps({"type": "callback", "id": _id[0], "op": op, "args": args}), flush=True)
    reply = json.loads(sys.stdin.readline())
    if not reply.get("ok"):
        raise RuntimeError(f"callback {op} rejected: {reply.get('error')}")
    return reply["result"]

# inside your hook handler, once info reported host.callbacks:
entry = callback("load_entry", {"space": "acme", "subpath": "/docs", "shortname": "readme"})
callback("log", {"level": 2, "message": f"loaded {entry.get('shortname')}"})
```

## Event Object (what hook plugins receive)

```json
{
  "space_name": "myspace",
  "subpath": "posts",
  "shortname": "my_entry",
  "action_type": "create",
  "resource_type": "content",
  "schema_shortname": "blog_post",
  "user_shortname": "admin",
  "attributes": {}
}
```

## Request Object (what API plugins receive)

```json
{
  "method": "GET",
  "path": "/my_api/greet/alice",
  "query": {"key": "value"},
  "headers": {"accept": "application/json"},
  "body": null,
  "user": "admin"
}
```

**Credentials are stripped from `headers`.** `authorization`,
`proxy-authorization`, `cookie`, `x-channel-key` and `x-api-key` never reach a
plugin — with any of them it could replay the caller's identity against dmart's
own API. `user` is the already-resolved actor, which is what you actually need.

## config.json Reference

```json
{
  "shortname": "my_plugin",
  "is_active": true,
  "type": "hook",
  "listen_time": "after",
  "ordinal": 100,
  "concurrent": true,
  "workers": 1,
  "filters": {
    "subpaths": { "__all_spaces__": ["__all_subpaths__"] },
    "resource_types": ["content"],
    "schema_shortnames": [],
    "actions": ["create", "update", "delete"]
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `shortname` | string | Must match directory name |
| `is_active` | bool | `false` = not loaded |
| `type` | `"hook"` or `"api"` | Plugin type |
| `listen_time` | `"before"` or `"after"` | Hook only. `before` can abort actions |
| `ordinal` | int | Execution order (lower = first, default 9999) |
| `concurrent` | bool | After-hooks: `true` = fire-and-forget (default) |
| `workers` | int | Processes to run for this plugin (default 1, max 32). >1 handles calls in parallel — see "Handling calls in parallel" |
| `filters.subpaths` | object | Per-space subpath dict — see "Filter shape" below |
| `filters.resource_types` | string[] | Empty = all, or list specific resource types |
| `filters.schema_shortnames` | string[] | Empty = all, or list specific content schemas |
| `filters.actions` | string[] | Empty = all, or: create, update, delete, move, lock, unlock, etc. |

## Filter shape (permission-style)

`filters.subpaths` is a **dictionary** keyed by space name. The same
vocabulary the permission engine uses:

| Sentinel | Meaning |
|----------|---------|
| `"__all_spaces__"` (as a key) | Match any space |
| `"__all_subpaths__"` (in the value list) | Match any subpath under that space |
| `"__current_user__"` (inside a pattern) | Replaced by the event's `user_shortname` |

```json
"filters": {
  "subpaths": {
    "myspace":          ["tickets", "issues"],
    "shared":           ["__all_subpaths__"],
    "__all_spaces__":   ["public"]
  },
  "resource_types":   ["content"],
  "schema_shortnames": ["bug_report"],
  "actions":          ["create", "update"]
}
```

A subpath pattern is a hierarchical prefix: `"tickets"` matches event
subpaths `"tickets"`, `"tickets/open"`, `"tickets/open/p1"` — but NOT
`"ticketsearch"`. An **empty** `subpaths` dict means the plugin doesn't
fire on any event; explicitly opt in to "everything" with
`{ "__all_spaces__": ["__all_subpaths__"] }`.

Empty `resource_types` / `schema_shortnames` / `actions` lists each
mean "match every value of that dimension" — same convention as
permissions. `schema_shortnames` is only consulted when the event's
`resource_type` is `content`.

### Migrating from the legacy flat-array shape

Configs that still use `"subpaths": ["__ALL__"]` or `"__ALL__"` in
`resource_types` / `schema_shortnames` will be **rejected at load** with
a clear migration error. Convert:

```diff
-"subpaths": ["__ALL__"]
+"subpaths": { "__all_spaces__": ["__all_subpaths__"] }

-"resource_types": ["__ALL__"]
+"resource_types": []

-"schema_shortnames": ["__ALL__"]
+"schema_shortnames": []
```

The `always_active` flag (used to bypass the old per-space
`active_plugins` opt-in) is gone — every plugin now self-declares
its scope.

## Hook Lifecycle

```
Client request
  │
  ▼
Before hooks (listen_time: "before")
  │ Plugin returns error → ACTION ABORTED
  │
  ▼
Action executes (create/update/delete/...)
  │
  ▼
After hooks (listen_time: "after")
  │ concurrent=true  → fire-and-forget (failures logged)
  │ concurrent=false → awaited (failures logged, don't fail action)
```

The per-space `active_plugins` opt-in list **no longer exists** — a
plugin fires for every event matched by its own `filters` block. The
field on the `spaces` table is left in place for back-compat with older
servers but is no longer read or written.

API plugins ignore `filters` entirely — routes are mounted if `is_active: true`.

## Directory Layout

```
~/.dmart/plugins/
  my_hook/
    config.json
    my_hook       ← executable, named after its directory
  my_api/
    config.json
    my_api
```

dmart looks for a file named exactly like the directory first, then for any
other executable in it. A directory with a `config.json` but no executable is
reported as a load failure rather than skipped silently.

## Sample Projects

Working examples in this directory:

| Directory | Language | Type | Shows |
|-----------|----------|------|-------|
| `sample_hook/` | Python | Hook | The info/hook exchange, plus a `log` callback |
| `sample_api/` | Python | API | Route declaration and request handling |
