#!/usr/bin/env python3
"""Copy original notices from the exact vendored Rust dependencies into the package."""
import argparse
import json
from pathlib import Path
import shutil
import tomllib

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--vendor", required=True, type=Path)
parser.add_argument("--output", required=True, type=Path)
args = parser.parse_args()
root = args.vendor.resolve(strict=True)
args.output.mkdir(parents=True, exist_ok=True)
inventory = []
for crate in sorted(root.iterdir()):
    manifest = crate / "Cargo.toml"
    if not manifest.is_file():
        continue
    package = tomllib.loads(manifest.read_text(encoding="utf-8"))["package"]
    folder = args.output / (package["name"] + "-" + package["version"])
    folder.mkdir(exist_ok=True)
    candidates = {p for pattern in ("LICENSE*", "LICENCE*", "COPYING*", "NOTICE*") for p in crate.glob(pattern)}
    if package.get("license-file"):
        candidates.add(crate / package["license-file"])
    copied = []
    for notice in sorted(candidates):
        if not notice.is_file():
            continue
        notice.resolve(strict=True).relative_to(crate.resolve(strict=True))
        shutil.copyfile(notice, folder / notice.name)
        copied.append(notice.name)
    inventory.append({"name": package["name"], "version": package["version"],
                      "license": package.get("license"), "repository": package.get("repository"), "notices": copied})
(args.output / "inventory.json").write_text(json.dumps(inventory, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"Collected original notices for {len(inventory)} pinned Rust packages")
missing = [item["name"] for item in inventory if not item["notices"]]
if missing:
    print("Packages without bundled notice files: " + ", ".join(missing))
