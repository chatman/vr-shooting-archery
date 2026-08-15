"""Submit a ComfyUI API workflow and wait for it to finish.

Polls over plain HTTP rather than using the websocket, so it needs nothing beyond the
stdlib (ComfyUI's venv has no websocket-client). Progress comes from ComfyUI's own
/internal/logs/raw console buffer, which is also where LoRA key warnings and tracebacks
show up -- worth seeing on a run this long (FLUX sampling, then 12 MoGe passes).
"""

import json
import re
import sys
import time
import urllib.request
import uuid

SERVER = "127.0.0.1:8188"
ANSI = re.compile(r"\x1b\[[0-9;]*m")


def get(path):
    return json.loads(urllib.request.urlopen("http://{}{}".format(SERVER, path)).read())


def log_tail(since_index):
    entries = get("/internal/logs/raw")["entries"]
    new = entries[since_index:]
    return len(entries), [ANSI.sub("", e["m"]).rstrip("\n") for e in new]


def apply_overrides(workflow, args):
    """--set NODE.input=value, so seeds and prompts can be swept without editing the JSON.

    Values are parsed as JSON when possible so ints/floats/bools stay typed; ComfyUI
    validates input types and would reject a stringified seed.
    """
    for a in args:
        target, _, raw = a.partition("=")
        node, _, field = target.partition(".")
        try:
            value = json.loads(raw)
        except json.JSONDecodeError:
            value = raw
        workflow[node]["inputs"][field] = value
    return workflow


def main():
    workflow = json.load(open(sys.argv[1]))
    workflow = apply_overrides(workflow, [a[6:] for a in sys.argv[2:] if a.startswith("--set=")])

    # --drop lets the panorama stage run alone out of the same graph (ComfyUI only
    # executes what an output node depends on), so seed sweeps skip the meshing cost.
    for a in sys.argv[2:]:
        if a.startswith("--drop="):
            for node in a[len("--drop="):].split(","):
                workflow.pop(node.strip(), None)

    client_id = str(uuid.uuid4())

    # Start from the current end of the log so we only print this run's output.
    log_index = len(get("/internal/logs/raw")["entries"])

    body = json.dumps({"prompt": workflow, "client_id": client_id}).encode()
    req = urllib.request.Request(
        "http://{}/prompt".format(SERVER), data=body,
        headers={"Content-Type": "application/json"},
    )
    try:
        resp = json.loads(urllib.request.urlopen(req).read())
    except urllib.error.HTTPError as e:
        print("SUBMIT FAILED:\n" + e.read().decode())
        return 1

    prompt_id = resp["prompt_id"]
    print("queued prompt_id={}\n".format(prompt_id), flush=True)

    while True:
        log_index, lines = log_tail(log_index)
        for line in lines:
            if line.strip():
                print("  | " + line, flush=True)

        hist = get("/history/{}".format(prompt_id))
        if prompt_id in hist:
            entry = hist[prompt_id]
            status = entry.get("status", {})
            print("\n=== status: {} ===".format(status.get("status_str")))

            if status.get("status_str") == "error":
                for m in status.get("messages", []):
                    if m[0] == "execution_error":
                        d = m[1]
                        print("node {} ({}): {}".format(
                            d.get("node_id"), d.get("node_type"), d.get("exception_message")))
                        print("\n".join(d.get("traceback", [])))
                return 1

            print("=== OUTPUTS ===")
            for node_id, out in entry.get("outputs", {}).items():
                for key, items in out.items():
                    for item in items or []:
                        if isinstance(item, dict) and item.get("filename"):
                            print("  [{}] {} -> {}/{}".format(
                                node_id, key, item.get("subfolder", ""), item["filename"]))
            return 0

        time.sleep(3)


if __name__ == "__main__":
    sys.exit(main())
