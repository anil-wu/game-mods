#!/usr/bin/env bash
# 构建框架核心库（netstandard2.1）并拷贝到 Unity 工程的 Plugins 目录。
set -e
cd "$(dirname "$0")"

PLUGINS_DIR="Unity/Assets/Plugins"
mkdir -p "$PLUGINS_DIR"

for proj in Game.Mod.Contract Game.ECS Game.Messaging Game.Mod.Runtime; do
  echo ">>> 构建 $proj (netstandard2.1)"
  dotnet build "src/$proj/$proj.csproj" -c Release -f netstandard2.1 >/dev/null
  cp "src/$proj/bin/Release/netstandard2.1/$proj.dll" "$PLUGINS_DIR/"
done

echo ">>> 完成，DLL 已拷贝到 $PLUGINS_DIR"
