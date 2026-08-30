#!/usr/bin/env python
"""把构建暂存的 Mod 目录（dist/mods/<modId>）打包为 .mod（zip），供上传到 mod_server。

用法: python runtime/pack-mod.py [modId ...]   (不传则打包全部)
产物: dist/packages/<modId>-<version>.mod
"""
import json
import os
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "dist", "mods")  # build-mod.py 的构建暂存（含 boot=false 的 Mod）
OUT = os.path.join(ROOT, "dist", "packages")


def pack(mod_id):
    mod_dir = os.path.join(SRC, mod_id)
    manifest_path = os.path.join(mod_dir, "manifest.json")
    if not os.path.isfile(manifest_path):
        print(f"跳过 {mod_id}（未构建，先运行 build-mod.py）")
        return
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)
    version = manifest["version"]

    os.makedirs(OUT, exist_ok=True)
    out_path = os.path.join(OUT, f"{mod_id}-{version}.mod")
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as z:
        for dirpath, _, files in os.walk(mod_dir):
            for name in files:
                if name.endswith(".meta"):
                    continue
                full = os.path.join(dirpath, name)
                z.write(full, os.path.relpath(full, mod_dir))
    print(f">>> 已打包 {mod_id}@{version} → {out_path}")


def main():
    targets = sys.argv[1:]
    if not targets:
        targets = [d for d in os.listdir(SRC)
                   if os.path.isfile(os.path.join(SRC, d, "manifest.json"))]
    for mod_id in targets:
        pack(mod_id)


if __name__ == "__main__":
    main()
