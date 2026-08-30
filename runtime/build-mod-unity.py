#!/usr/bin/env python
"""打包复刻案例 Unity 工程（samples/MirrorUnityFPS）里的所有 Mod。

结构（§mod-unity-project.md 复刻形态）：
  samples/MirrorUnityFPS/           一个 Unity 工程
    Assets/Editor/ModPacker.cs      打包工具：扫描 Assets/Mods/*/mod.json 逐个独立打包
    Assets/Mods/<modId>/           每个 mod.json 所在文件夹是一个独立 Mod（Scripts + 资源）

用法: python runtime/build-mod-unity.py
前置: 框架 DLL 已构建（build-unity-dlls.sh）；产物: dist/mods/<modId>/{<modId>.dll,<modId>.bundle,manifest.json}
"""
import json
import os
import shutil
import subprocess

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UNITY = os.environ.get("UNITY_PATH", "D:/Programs/Unity/2022.3.62f3/Editor/Unity.exe")
FRAMEWORK = ["Game.Mod.Contract", "Game.ECS", "Game.Messaging", "Game.Mod.Runtime"]
DIST_MODS = os.path.join(ROOT, "dist", "mods")

SAMPLE = os.path.join(ROOT, "samples", "MirrorUnityFPS")


def ensure_asmdefs():
    """每个 Mod 的 Scripts/ 需有 {modId}.asmdef（独立命名程序集，避免与 Assembly-CSharp 冲突）。
    缺则自动生成——否则 Mod 代码编进 Assembly-CSharp，ModPacker 找不到独立 DLL。"""
    mods_root = os.path.join(SAMPLE, "Assets", "Mods")
    for mod_id in os.listdir(mods_root):
        mod_dir = os.path.join(mods_root, mod_id)
        if not os.path.isfile(os.path.join(mod_dir, "mod.json")):
            continue
        scripts = os.path.join(mod_dir, "Scripts")
        os.makedirs(scripts, exist_ok=True)
        asmdef = os.path.join(scripts, mod_id + ".asmdef")
        if not os.path.exists(asmdef):
            with open(asmdef, "w", encoding="utf-8") as f:
                f.write('{\n  "name": "%s"\n}\n' % mod_id)
            print(f">>> 生成 asmdef {mod_id}")


def sync_framework_dlls():
    """框架 DLL 拷入复刻工程的 Assets/Plugins（Mod 代码编译引用；不入库）。"""
    plugins = os.path.join(SAMPLE, "Assets", "Plugins")
    os.makedirs(plugins, exist_ok=True)
    for proj in FRAMEWORK:
        src = os.path.join(ROOT, "runtime", "src", proj, "bin", "Release", "netstandard2.1", proj + ".dll")
        if os.path.isfile(src):
            shutil.copy(src, os.path.join(plugins, proj + ".dll"))
        else:
            print(f"警告: 框架 DLL 未构建 {proj}（先跑 build-unity-dlls.sh）")


def main():
    if not os.path.isdir(os.path.join(SAMPLE, "Assets", "Mods")):
        print("未找到复刻工程 Assets/Mods")
        return

    ensure_asmdefs()
    sync_framework_dlls()

    env = dict(os.environ)
    env["MOD_OUTPUT_DIR"] = DIST_MODS
    r = subprocess.run(
        [UNITY, "-batchmode", "-nographics", "-quit",
         "-executeMethod", "ModPacker.Pack",
         "-projectPath", SAMPLE,
         "-logFile", os.path.join(ROOT, ".modbuild.log")],
        env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        print(r.stdout[-3000:])
        print(r.stderr[-1500:])
        raise SystemExit("Mod 打包失败（见 .modbuild.log）")
    print(f">>> 打包完成 → {DIST_MODS}")

    # 同步 boot Mod（boot != false）到宿主 StreamingAssets
    streaming = os.path.join(ROOT, "runtime", "Unity", "Assets", "StreamingAssets", "mods")
    for mod_id in os.listdir(DIST_MODS):
        mod_json = os.path.join(SAMPLE, "Assets", "Mods", mod_id, "mod.json")
        if not os.path.isfile(mod_json):
            continue  # 非本复刻工程的 Mod（框架/示例）
        with open(mod_json, encoding="utf-8") as f:
            boot = json.load(f).get("boot", True)
        dst = os.path.join(streaming, mod_id)
        if boot:
            shutil.rmtree(dst, ignore_errors=True)
            shutil.copytree(os.path.join(DIST_MODS, mod_id), dst)
            print(f">>> {mod_id} → {dst}")
        else:
            # boot=false：不进核心启动集（商店分发），清除可能的历史残留
            shutil.rmtree(dst, ignore_errors=True)
            print(f">>> {mod_id} 不进启动集（boot=false）")


if __name__ == "__main__":
    main()
