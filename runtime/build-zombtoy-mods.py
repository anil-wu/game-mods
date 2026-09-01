#!/usr/bin/env python
"""构建 samples/Zombtoy 的 Mod DLL（视图自建视觉后重建，boot=false 仅分发 + 手动同步宿主启动集）。

与 build-mod.py 的区别：
- 源码在 samples/Zombtoy/Assets/Mods/<id>/Scripts/（复刻工程 asmdef 形态），非 mods/<id>/src/；
- 额外引用 UnityEngine.UI.dll（ugui 包，宿主 Library/ScriptAssemblies 编译产物）——菜单/HUD 视图用。

用法: python runtime/build-zombtoy-mods.py [modId ...]   （不传则构建全部 Zombtoy Mod）
产物: dist/mods/<id>/<id>.dll + 同步 runtime/Unity/Assets/StreamingAssets/mods/<id>/<id>.dll（启动集）
"""
import glob
import json
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UNITY_HOME = os.environ.get("UNITY_HOME", "D:/Programs/Unity/2022.3.62f3/Editor")
UNITYENGINE = os.path.join(UNITY_HOME, "Data", "Managed", "UnityEngine")
TARGET = os.path.join(ROOT, "runtime", "Unity", "Assets", "StreamingAssets", "mods")
DIST_MODS = os.path.join(ROOT, "dist", "mods")
TMP = os.path.join(ROOT, ".tmp_zombtoy_build")
FRAMEWORK = ["Game.Mod.Contract", "Game.ECS", "Game.Messaging", "Game.Mod.Runtime"]
UNITY_REFS = ["UnityEngine.CoreModule", "UnityEngine.IMGUIModule", "UnityEngine.InputLegacyModule",
              "UnityEngine.TextRenderingModule", "UnityEngine.AudioModule", "UnityEngine.PhysicsModule",
              "UnityEngine.AnimationModule", "UnityEngine.AIModule", "UnityEngine.TerrainModule",
              "UnityEngine.TerrainPhysicsModule", "UnityEngine.ParticleSystemModule",
              "UnityEngine.UIModule", "UnityEngine.AssetBundleModule",
              "UnityEngine.UnityWebRequestModule", "UnityEngine.UnityWebRequestAssetBundleModule"]
UI_DLL = os.path.join(ROOT, "runtime", "Unity", "Library", "ScriptAssemblies", "UnityEngine.UI.dll")


def build_mod(mod_dir):
    with open(os.path.join(mod_dir, "mod.json"), encoding="utf-8") as f:
        m = json.load(f)
    mid = m["id"]
    entry = m["modules"]["shared"]

    os.makedirs(TMP, exist_ok=True)
    projrefs = "\n".join(
        f'    <ProjectReference Include="{os.path.join(ROOT, "runtime", "src", p, p + ".csproj")}" />'
        for p in FRAMEWORK
    )
    unityrefs = "\n".join(
        f'    <Reference Include="{m}">\n'
        f'      <HintPath>{os.path.join(UNITYENGINE, m + ".dll")}</HintPath>\n'
        f'      <Private>false</Private>\n'
        f'    </Reference>'
        for m in UNITY_REFS
    )
    ui_ref = (f'    <Reference Include="UnityEngine.UI">\n'
              f'      <HintPath>{UI_DLL}</HintPath>\n'
              f'      <Private>false</Private>\n'
              f'    </Reference>') if os.path.isfile(UI_DLL) else ""

    csproj = f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>{mid}</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
{projrefs}
{unityrefs}
{ui_ref}
    <Compile Include="{os.path.join(mod_dir, "Scripts", "**", "*.cs")}" />
  </ItemGroup>
</Project>
"""
    csproj_path = os.path.join(TMP, "mod.csproj")
    with open(csproj_path, "w", encoding="utf-8") as f:
        f.write(csproj)

    r = subprocess.run(
        ["dotnet", "build", csproj_path, "-c", "Release", "-f", "netstandard2.1"],
        cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    if r.returncode != 0:
        print(r.stdout)
        print(r.stderr)
        raise SystemExit(f"构建 {mid} 失败")

    dll = os.path.join(TMP, "bin", "Release", "netstandard2.1", mid + ".dll")

    # 1. 分发暂存（打包/商店来源）
    dist_dir = os.path.join(DIST_MODS, mid)
    os.makedirs(dist_dir, exist_ok=True)
    shutil.copy(dll, os.path.join(dist_dir, entry))

    # 2. 宿主启动集（Zombtoy boot=false，本地验收手动同步；不影响 boot 集规则）
    outdir = os.path.join(TARGET, mid)
    if os.path.isdir(outdir):
        shutil.copy(dll, os.path.join(outdir, entry))
        print(f">>> 已构建 {mid} → {dist_dir} + {outdir}")
    else:
        print(f">>> 已构建 {mid} → {dist_dir}（宿主启动集无此 Mod 目录，未同步）")


def main():
    only = sys.argv[1:]
    mods_root = os.path.join(ROOT, "samples", "Zombtoy", "Assets", "Mods")
    found = 0
    for mod_dir in sorted(glob.glob(os.path.join(mods_root, "*"))):
        if not os.path.isfile(os.path.join(mod_dir, "mod.json")):
            continue
        if only and os.path.basename(mod_dir) not in only:
            continue
        build_mod(mod_dir)
        found += 1
    shutil.rmtree(TMP, ignore_errors=True)
    if found == 0:
        raise SystemExit(f"未构建任何 Mod（参数不匹配: {only}）")


if __name__ == "__main__":
    main()
