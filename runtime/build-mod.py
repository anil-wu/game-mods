#!/usr/bin/env python
"""构建 mods/ 下所有 Mod（按依赖拓扑排序，支持跨 Mod 编译引用），拷贝到 Unity StreamingAssets/mods/。

用法: python runtime/build-mod.py [mod源工程目录]   (不传则构建全部)
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
TMP = os.path.join(ROOT, ".tmp_modbuild")
FRAMEWORK = ["Game.Mod.Contract", "Game.ECS", "Game.Messaging", "Game.Mod.Runtime"]
UNITY_REFS = ["UnityEngine.CoreModule", "UnityEngine.IMGUIModule", "UnityEngine.InputLegacyModule"]


def load_mods():
    """读取 mods/*/mod.json（Manifest v2：id / modules / dependencies 数组）。"""
    mods = {}
    for path in glob.glob(os.path.join(ROOT, "mods", "*", "mod.json")):
        with open(path, encoding="utf-8") as f:
            m = json.load(f)
        modules = m.get("modules", {})
        entry = modules.get("shared") or m.get("entryDll")  # v2 优先，v1 兜底
        deps = []
        for d in m.get("dependencies", []):
            deps.append(d["id"] if isinstance(d, dict) else d)
        mods[m["id"]] = {
            "dir": os.path.dirname(path),
            "id": m["id"],
            "entry": entry,
            "deps": deps,
        }
    return mods


def topo_sort(mods):
    order, state = [], {}

    def visit(mid, path):
        if state.get(mid) == 1:
            raise SystemExit("循环依赖: " + " -> ".join(path + [mid]))
        if state.get(mid) == 2:
            return
        state[mid] = 1
        for dep in mods[mid]["deps"]:
            if dep in mods:
                visit(dep, path + [mid])
        state[mid] = 2
        order.append(mods[mid])

    for mid in list(mods):
        visit(mid, [])
    return order


def build_mod(mod, mods):
    os.makedirs(TMP, exist_ok=True)

    projrefs = "\n".join(
        f'    <ProjectReference Include="{os.path.join(ROOT, "runtime", "src", p, p + ".csproj")}" />'
        for p in FRAMEWORK
    )

    deprefs = ""
    for dep in mod["deps"]:
        if dep in mods:
            depdll = os.path.join(TARGET, dep, mods[dep]["entry"])
            deprefs += (
                f'    <Reference Include="{dep}">\n'
                f'      <HintPath>{depdll}</HintPath>\n'
                f'      <Private>false</Private>\n'
                f'    </Reference>\n'
            )

    unityrefs = "\n".join(
        f'    <Reference Include="{m}">\n'
        f'      <HintPath>{os.path.join(UNITYENGINE, m + ".dll")}</HintPath>\n'
        f'      <Private>false</Private>\n'
        f'    </Reference>'
        for m in UNITY_REFS
    )

    csproj = f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>{mod["id"]}</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
{projrefs}
{deprefs}
{unityrefs}
    <Compile Include="{os.path.join(mod["dir"], "src", "**", "*.cs")}" />
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
        raise SystemExit(f"构建 {mod['id']} 失败")

    outdir = os.path.join(TARGET, mod["id"])
    os.makedirs(outdir, exist_ok=True)
    shutil.copy(
        os.path.join(TMP, "bin", "Release", "netstandard2.1", mod["id"] + ".dll"),
        os.path.join(outdir, mod["entry"]),
    )
    shutil.copy(os.path.join(mod["dir"], "mod.json"), os.path.join(outdir, "manifest.json"))
    datadir = os.path.join(mod["dir"], "data")
    if os.path.isdir(datadir):
        shutil.copytree(datadir, os.path.join(outdir, "data"), dirs_exist_ok=True)
    print(f">>> 已构建 {mod['id']} → {outdir}")


def main():
    mods = load_mods()
    order = topo_sort(mods)
    target = sys.argv[1] if len(sys.argv) > 1 else None
    if target:
        target_dir = os.path.normpath(os.path.join(ROOT, target))
        order = [m for m in order if os.path.normpath(m["dir"]) == target_dir]
        if not order:
            raise SystemExit(f"未找到 Mod: {target}")
    for mod in order:
        build_mod(mod, mods)
    shutil.rmtree(TMP, ignore_errors=True)


if __name__ == "__main__":
    main()
