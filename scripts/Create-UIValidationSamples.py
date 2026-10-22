"""Generate public, deterministic fixtures; never reads user archives."""
import hashlib
import json
import pathlib
import sys
import zipfile

output = pathlib.Path(sys.argv[1]).resolve()
output.mkdir(parents=True, exist_ok=True)
receipts = []
for count in (1000, 100000):
    path = output / f"entries-{count}.zip"
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as archive:
        for index in range(count):
            item = zipfile.ZipInfo(f"项目说明_{index:06d}.md", date_time=(2026, 1, 1, 0, 0, 0))
            archive.writestr(item, b"AileArc public validation fixture\n")
    receipts.append({"file": path.name, "entries": count, "bytes": path.stat().st_size,
                     "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
(output / "samples.json").write_text(json.dumps(receipts, indent=2), encoding="utf-8")
print(json.dumps(receipts, indent=2))
