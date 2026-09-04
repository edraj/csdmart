#!/usr/bin/env python3
"""
Sample hook plugin for dmart.

Reads JSON lines from stdin, writes JSON lines to stdout. Python is used here
for brevity — a plugin is just an executable, so any language works.

Deploy:
  mkdir -p ~/.dmart/plugins/sample_hook
  cp plugin.py ~/.dmart/plugins/sample_hook/sample_hook
  chmod +x ~/.dmart/plugins/sample_hook/sample_hook
  cp config.json ~/.dmart/plugins/sample_hook/
"""

import json
import sys

# Single source of truth for the plugin's version. Baked into the binary
# you ship: dmart reads this back via the {"type":"info"} response and
# surfaces it on GET /info/plugins and the SUBPROCESS_PLUGIN_REGISTERED
# log line. Bump alongside any behavior change.
__version__ = "1.1.0"

# Set from the info frame's "host" object. Older dmart builds send a bare
# {"type":"info"} and answer no callbacks — sending one to those would be read
# as this plugin's final response and desynchronise the exchange, so the
# capability has to be checked rather than assumed.
HOST_CALLBACKS = False

# Monotonic id for callback correlation. dmart echoes whatever we send back
# verbatim; the scheme is ours to choose.
_next_id = 0


def callback(op, args):
    """Ask dmart to do something, mid-exchange, and wait for the answer.

    Returns the `result` document. Raises when dmart rejected the FRAME
    (unknown op, bad args) — that is a plugin bug, not a runtime failure.
    A failed operation comes back with ok=true and the failure inside
    `result`, so callers inspect that themselves.
    """
    global _next_id
    _next_id += 1
    print(json.dumps({"type": "callback", "id": _next_id, "op": op, "args": args}), flush=True)

    reply = json.loads(sys.stdin.readline())
    if not reply.get("ok"):
        raise RuntimeError(f"callback {op} rejected: {reply.get('error')}")
    return reply["result"]


def handle_info(msg):
    global HOST_CALLBACKS
    HOST_CALLBACKS = bool(msg.get("host", {}).get("callbacks"))
    return {"shortname": "sample_hook", "version": __version__, "type": "hook"}

def handle_hook(event):
    action = event.get("action_type", "?")
    space = event.get("space_name", "?")
    shortname = event.get("shortname", "?")
    user = event.get("user_shortname", "?")
    print(f"[sample_hook] {action} {space}/{shortname} by {user}", file=sys.stderr)

    if HOST_CALLBACKS:
        # Route the same line through dmart's logging pipeline, so it lands in
        # the operator's configured sinks under `plugin.sample_hook`
        # rather than only on stderr. `code` 0 means dmart accepted it.
        callback("log", {
            "level": 2,  # 0=trace 1=debug 2=info 3=warn 4=error 5=critical
            "message": f"{action} {space}/{shortname} by {user}",
        })

    return {"status": "ok"}

def main():
    # readline() rather than `for line in sys.stdin` because callback() reads
    # dmart's reply from the same stream mid-iteration; one explicit reader
    # keeps who-consumes-which-line obvious.
    while True:
        line = sys.stdin.readline()
        if not line:
            break  # dmart closed stdin — clean shutdown
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
            msg_type = msg.get("type", "")

            if msg_type == "info":
                response = handle_info(msg)
            elif msg_type == "hook":
                response = handle_hook(msg.get("event", {}))
            else:
                response = {"status": "error", "message": f"unknown type: {msg_type}"}

            print(json.dumps(response), flush=True)
        except Exception as e:
            print(json.dumps({"status": "error", "message": str(e)}), flush=True)

if __name__ == "__main__":
    # Ctrl+C in the controlling terminal sends SIGINT to every process in the
    # foreground process group — this plugin included. dmart also receives it
    # and begins a clean shutdown which closes our stdin; that's the signal
    # we actually want to obey. Ignoring SIGINT here keeps us running until
    # dmart's stdin close drops us out of the `for line in sys.stdin` loop,
    # so shutdown is orderly and there's no traceback to scare operators.
    import signal
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    try:
        main()
    except KeyboardInterrupt:
        # Belt-and-suspenders: if SIG_IGN somehow didn't stick (e.g. the
        # plugin is launched from a context that resets signal handlers),
        # still exit silently rather than dumping a Python traceback.
        pass
