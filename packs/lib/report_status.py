# Shared by the groups and personas phases: print the record's own error, not a
# JSON traceback. `curl -f` hides the body on a 4xx, and dmart puts the useful
# part in error.info[0].failed[].error — a bare "400" sends the operator to the
# server log for something the response already said.
import json, sys
raw = sys.stdin.read()
try:
    d = json.loads(raw)
except Exception:
    print("unreadable response: " + raw[:120].replace("\n", " "))
    raise SystemExit
if d.get("status") == "success":
    print("success")
    raise SystemExit
err = d.get("error") or {}
for block in err.get("info") or []:
    if isinstance(block, dict):
        for f in block.get("failed") or []:
            print("failed — " + str(f.get("error"))[:100])
            raise SystemExit
print("failed — " + str(err.get("message"))[:100])
