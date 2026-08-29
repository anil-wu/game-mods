#!/usr/bin/env bash
# Unity 自动验证：编译检查 + Play 模式冒烟测试（需先关闭 Unity 编辑器）
set -e
cd "$(dirname "$0")"
UNITY="${UNITY_PATH:-D:/Programs/Unity/2022.3.62f3/Editor/Unity.exe}"
PROJECT="$(pwd)/Unity"

echo ">>> 1. 编译检查"
"$UNITY" -batchmode -nographics -quit -projectPath "$PROJECT" -logFile /tmp/unity-compile.log
if grep -q "error CS" /tmp/unity-compile.log; then
  echo "编译错误："
  grep "error CS" /tmp/unity-compile.log
  exit 1
fi
echo "   编译通过"

echo ">>> 2. Play 模式冒烟测试（加载 Mods）"
"$UNITY" -batchmode -nographics -executeMethod Game.Runtime.Editor.AutoPlay.Run -projectPath "$PROJECT" -logFile /tmp/unity-play.log
if grep -q "\[AutoPlay\] OK" /tmp/unity-play.log; then
  echo "   Play 冒烟通过"
else
  echo "   Play 冒烟失败："
  grep "\[AutoPlay\]" /tmp/unity-play.log || true
  exit 1
fi
