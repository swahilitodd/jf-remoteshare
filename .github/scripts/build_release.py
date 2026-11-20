#!/usr/bin/env python3
"""Build every variant in plugin.yaml into dist/<zip> and write dist/results.json.

Usage: build_release.py [--strict]
  --strict  fail if any variant fails (used for the per-push compile check).
Without it, a failed variant is skipped with a warning; exits non-zero only if nothing built.
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile

import yaml

PROJECT = "Jellyfin.Plugin.RemoteShare"
strict = "--strict" in sys.argv[1:]

meta = yaml.safe_load(open("plugin.yaml", encoding="utf-8"))
base_version = str(meta["version"])
if base_version.count(".") != 2:
    sys.exit("plugin.yaml version must be X.Y.Z")

os.makedirs("dist", exist_ok=True)
results, failed = [], []
for v in meta["variants"]:
    version = f"{base_version}.{v['suffix']}"
    out = f"publish/jf{v['jellyfin']}"
    shutil.rmtree(out, ignore_errors=True)
    cmd = [
        "dotnet", "publish", PROJECT, "-c", "Release", "-f", v["framework"], "-o", out,
        f"-p:Version={version}", f"-p:AssemblyVersion={version}", f"-p:FileVersion={version}",
    ]
    print("::group::" + " ".join(cmd), flush=True)
    rc = subprocess.call(cmd)
    print("::endgroup::", flush=True)
    if rc != 0:
        print(f"::error::Build for Jellyfin {v['jellyfin']} failed (exit {rc}).")
        failed.append(v["jellyfin"])
        continue
    zip_name = f"remote-share_{version}_jellyfin-{v['jellyfin']}.zip"
    zip_path = os.path.join("dist", zip_name)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(os.path.join(out, f"{PROJECT}.dll"), f"{PROJECT}.dll")
    md5 = hashlib.md5(open(zip_path, "rb").read()).hexdigest()
    results.append({"version": version, "targetAbi": v["targetAbi"], "zip": zip_name, "md5": md5,
                    "jellyfin": v["jellyfin"]})

json.dump(results, open("dist/results.json", "w"), indent=2)
print(json.dumps(results, indent=2))
if not results or (strict and failed):
    sys.exit("Build failed for Jellyfin " + ", ".join(failed))
