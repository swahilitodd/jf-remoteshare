#!/usr/bin/env python3
"""Add or replace one version entry in the Jellyfin repository manifest.json.

Usage: update_manifest.py <plugin.yaml> <manifest.json> <version> <targetAbi> <zip-url> <md5> <timestamp>
"""
import json
import sys

import yaml

plugin_yaml, manifest_path, version, target_abi, source_url, checksum, timestamp = sys.argv[1:8]

with open(plugin_yaml, encoding="utf-8") as f:
    meta = yaml.safe_load(f)

try:
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)
except (FileNotFoundError, json.JSONDecodeError):
    manifest = []

plugin = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
if plugin is None:
    plugin = {"guid": meta["guid"], "versions": []}
    manifest.append(plugin)

plugin.update(
    {
        "name": meta["name"],
        "description": " ".join(str(meta.get("description", "")).split()),
        "overview": meta.get("overview", ""),
        "owner": meta.get("owner", ""),
        "category": meta.get("category", "General"),
    }
)

entry = {
    "version": version,
    "changelog": meta.get("changelog", ""),
    "targetAbi": target_abi,
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": timestamp,
}
versions = [v for v in plugin.get("versions", []) if v.get("version") != version]
versions.append(entry)
versions.sort(key=lambda v: tuple(int(x) for x in v["version"].split(".")), reverse=True)
plugin["versions"] = versions

with open(manifest_path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")

print(f"manifest.json: {meta['name']} {version} (targetAbi {target_abi}) -> {source_url}")
