"""Summarize complete process-tree runs; no warm sample is labelled cold start."""
import json
import pathlib
import statistics
import sys

root = pathlib.Path(sys.argv[1])
rows = []
for path in sorted(root.glob("*.json")):
    report = json.loads(path.read_text(encoding="utf-8-sig"))
    if "Samples" not in report:
        continue
    samples = report["Samples"]
    steady = samples[-5:]
    rows.append({
        "host": report["HostKind"], "archive": report["Archive"], "label": report["Label"],
        "steadyPrivateMiB": round(statistics.mean(s["PrivateBytes"] for s in steady) / 2**20, 1),
        "sampledPeakPrivateMiB": round(max(s["PrivateBytes"] for s in samples) / 2**20, 1),
        "steadyWorkingSetMiB": round(statistics.mean(s["WorkingSet"] for s in steady) / 2**20, 1),
        "lastProcessCount": len(samples[-1]["Processes"]), "source": path.name,
    })
print(json.dumps(rows, indent=2))
