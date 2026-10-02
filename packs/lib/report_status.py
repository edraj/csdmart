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
def classify(message):
    # Re-running install.sh must not look like a broken install. The import
    # half is idempotent by design ("skipped N existing"), so the API half
    # reports an existing group or user the same way rather than as a failure.
    if "already exist" in message.lower():
        return "already"
    return "failed — " + message[:100]

err = d.get("error") or {}
for block in err.get("info") or []:
    if isinstance(block, dict):
        for f in block.get("failed") or []:
            print(classify(str(f.get("error") or "")))
            raise SystemExit
print(classify(str(err.get("message") or "")))
