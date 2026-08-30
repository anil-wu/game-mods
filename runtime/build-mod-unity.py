#!/usr/bin/env python
"""把每个 Mod（独立 Unity 工程）构建为 .mod 包（代码程序集 + 资源包 + manifest）。

用法: python runtime/build-mod-unity.py [modId ...]   (不传则构建全部)
前置: 每个 Mod 工程已含 Packages/manifest.json + Assets/Editor/ModBuilder.cs + 一个 asmdef。
产物: dist/mods/<modId>/ 与 runtime/Unity/Assets/StreamingAssets/mods/<modId>/
"""
import glob
import json
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UNITY = os.environ.get("UNITY_PATH", "D:/Programs/Unity/2022.3.62f3/Editor/Unity.exe")
FRAMEWORK = ["Game.Mod.Contract", "Game.ECS", "Game.Messaging", "Game.Mod.Runtime"]
TEMPLATE = os.path.join(ROOT, "runtime", "mod-template")
DIST_MODS = os.path.join(ROOT, "dist", "mods")
STREAMING = os.path.join(ROOT, "runtime", "Unity", "Assets", "StreamingAssets", "mods")


def discover_mod_projects():
    """发现 Mod：目录含 mod.json 即视为 Mod；build_one 再补 Unity 工程脚手架。"""
    projects = {}
    for pattern in [os.path.join(ROOT, "mods", "*"),
                    os.path.join(ROOT, "samples", "*", "mods", "*")]:
        for d in glob.glob(pattern):
            if os.path.isfile(os.path.join(d, "mod.json")):
                projects[os.path.basename(d)] = d
    return projects


def sync_framework_dlls(mod_dir):
    """框架 DLL 拷入 Mod 工程的 Assets/Plugins（只读引用）。"""
    plugins = os.path.join(mod_dir, "Assets", "Plugins")
    os.makedirs(plugins, exist_ok=True)
    for proj in FRAMEWORK:
        src = os.path.join(ROOT, "runtime", "src", proj, "bin", "Release", "netstandard2.1", proj + ".dll")
        if os.path.isfile(src):
            shutil.copy(src, os.path.join(plugins, proj + ".dll"))
        else:
            print(f"警告: 框架 DLL 未构建 {proj}（先跑 build-unity-dlls.sh）")


def ensure_asmdef(mod_dir, mod_id):
    """生成 {modId}.asmdef（独立命名程序集，避免与宿主 Assembly-CSharp 冲突）。
    noEngineReferences 不设：让 Mod 工程引用全部引擎模块；Plugins 框架 DLL 默认自动引用。"""
    scripts = os.path.join(mod_dir, "Assets", "Scripts")
    os.makedirs(scripts, exist_ok=True)
    asmdef_path = os.path.join(scripts, mod_id + ".asmdef")
    if not os.path.exists(asmdef_path):
        with open(asmdef_path, "w", encoding="utf-8") as f:
            f.write('{\n  "name": "%s"\n}\n' % mod_id)


def build_one(mod_id, mod_dir):
    # 1. 脚手架补齐（缺啥从模板补，不覆盖已有）
    for rel in ["Packages/manifest.json", "ProjectSettings/ProjectVersion.txt",
                "Assets/Editor/ModBuilder.cs"]:
        dst = os.path.join(mod_dir, rel)
        if not os.path.exists(dst):
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy(os.path.join(TEMPLATE, rel), dst)
    ensure_asmdef(mod_dir, mod_id)

    # 2. 同步框架 DLL
    sync_framework_dlls(mod_dir)

    # 3. Unity 批处理构建
    out = os.path.join(DIST_MODS, mod_id)
    env = dict(os.environ)
    env["MOD_OUTPUT_DIR"] = DIST_MODS
    r = subprocess.run(
        [UNITY, "-batchmode", "-nographics", "-quit",
         "-executeMethod", "ModBuilder.Build",
         "-projectPath", mod_dir,
         "-logFile", os.path.join(ROOT, ".modbuild.log")],
        env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        print(r.stdout[-3000:])
        print(r.stderr[-2000:])
        raise SystemExit(f"构建 {mod_id} 失败（见 .modbuild.log）")

    # 4. 同步到宿主 StreamingAssets（boot != false 才拷贝）
    mod_json = json.load(open(os.path.join(mod_dir, "mod.json"), encoding="utf-8"))
    if mod_json.get("boot", True):
        dst = os.path.join(STREAMING, mod_id)
        shutil.rmtree(dst, ignore_errors=True)
        shutil.copytree(out, dst)
        print(f">>> {mod_id} → {dst}")
    else:
        print(f">>> {mod_id} → {out}（boot=false，不进启动集）")


def main():
    projects = discover_mod_projects()
    if not projects:
        print("未发现 Mod 工程（含 Packages/manifest.json 的目录）")
        return
    targets = sys.argv[1:] or list(projects.keys())
    for mod_id in targets:
        if mod_id not in projects:
            print(f"未找到 Mod 工程: {mod_id}")
            continue
        build_one(mod_id, projects[mod_id])


if __name__ == "__main__":
    main()
