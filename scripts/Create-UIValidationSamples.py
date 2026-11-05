"""Generate public, deterministic fixtures; never reads user archives."""
import hashlib
import json
import pathlib
import sys
import zipfile

output = pathlib.Path(sys.argv[1]).resolve()
output.mkdir(parents=True, exist_ok=True)
receipts = []
samples = [("entries-1000.zip", 1000), ("entries-100000.zip", 100000)]
if "--tree" in sys.argv[2:]:
    samples.append(("tree-100000.zip", 100000))
for filename, count in samples:
    path = output / filename
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as archive:
        for index in range(count):
            number = (index * 7919) % count if filename.startswith("tree-") else index
            name = f"项目说明_{number:06d}.md"
            if filename.startswith("tree-") and number % 100:
                name = f"项目{number % 100:02d}/目录{number % 17:02d}/{name}"
            item = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            archive.writestr(item, b"AileArc public validation fixture\n")
    receipts.append({"file": path.name, "entries": count, "bytes": path.stat().st_size,
                     "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
(output / "samples.json").write_text(json.dumps(receipts, indent=2), encoding="utf-8")
print(json.dumps(receipts, indent=2))
