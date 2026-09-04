#!/usr/bin/env python3
"""
Sample API plugin for dmart. Mounts endpoints under /sample_api/.

Same stdin/stdout JSON-line protocol as a hook plugin; the difference is
`"type": "api"` in the info response plus a `routes` list, and answering
{"type":"request"} instead of {"type":"hook"}.

Deploy:
  mkdir -p ~/.dmart/plugins/sample_api
  cp plugin.py ~/.dmart/plugins/sample_api/sample_api
  chmod +x ~/.dmart/plugins/sample_api/sample_api
  cp config.json ~/.dmart/plugins/sample_api/
"""

import json
import sys

# Single source of truth for the plugin's version. Baked into the artifact you
# ship: dmart reads it back via the {"type":"info"} response and surfaces it on
# GET /info/plugins and the SUBPROCESS_PLUGIN_REGISTERED log line.
__version__ = "1.0.0"


def handle_info(_msg):
    # Routes are mounted relative to /<shortname>/, so "/" below is
    # /sample_api/ and "/greet/{name}" is /sample_api/greet/alice.
    return {
        "shortname": "sample_api",
        "version": __version__,
        "type": "api",
        "routes": [
            {"method": "GET", "path": "/"},
            {"method": "GET", "path": "/greet/{name}"},
        ],
    }


def handle_request(req):
    # dmart strips the caller's credentials from `headers` before handing the
    # request over; `user` is the already-resolved actor, which is what a
    # plugin legitimately needs.
    path = req.get("path", "/")
    user = req.get("user", "anonymous")

    if "/greet/" in path:
        name = path.split("/greet/", 1)[1].strip("/") or "world"
        return {
            "status": "success",
            "attributes": {"greeting": f"Hello, {name}!", "plugin": "sample_api", "user": user},
        }

    return {
        "status": "success",
        "attributes": {
            "plugin": "sample_api",
            "description": "A sample API plugin",
            "user": user,
        },
    }


def main():
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
            elif msg_type == "request":
                response = handle_request(msg.get("request", {}))
            else:
                response = {"status": "error", "message": f"unknown type: {msg_type}"}

            print(json.dumps(response), flush=True)
        except Exception as e:
            print(json.dumps({
                "status": "failed",
                "error": {"type": "plugin_error", "code": 500, "message": str(e)},
            }), flush=True)


if __name__ == "__main__":
    # See sample_hook/plugin.py for why SIGINT is ignored: dmart closes our
    # stdin on shutdown, and that EOF is the signal we want to obey.
    import signal
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    try:
        main()
    except KeyboardInterrupt:
        pass
