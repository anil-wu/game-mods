#!/usr/bin/env bash
# 构建各 Mod 的 AssetBundle（含 URP→内置材质转换），需先关闭 Unity 编辑器。
set -e
cd "$(dirname "$0")"
UNITY="${UNITY_PATH:-D:/Programs/Unity/2022.3.62f3/Editor/Unity.exe}"
"$UNITY" -batchmode -nographics -quit \
  -executeMethod Game.Runtime.Editor.ModAssetBundleBuilder.Build \
  -projectPath "$(pwd)/Unity" -logFile /tmp/unity-assets.log
grep -E "ModAssetBundleBuilder|Material|error" /tmp/unity-assets.log | tail -20 || true
