#!/usr/bin/env bash
# 获取 Mirror：SSH 浅克隆 → 拷贝源码到 Unity 工程 Assets。
# 背景：本环境 GitHub HTTPS 不通，故用 SSH 克隆；Mirror 源码 33MB 不进仓库。
# 用法：首次使用或 Mirror 缺失时运行 bash runtime/setup-mirror.sh
set -e
cd "$(dirname "$0")/.."

TARGET="runtime/Unity/Assets/Mirror"
TMP="third_party/Mirror"

if [ -d "$TARGET" ]; then
  echo ">>> Mirror 已存在 ($TARGET)，跳过。如需重装请先删除该目录。"
  exit 0
fi

if [ ! -d "$TMP" ]; then
  echo ">>> 浅克隆 Mirror（SSH git@github.com:MirrorNetworking/Mirror.git）..."
  git clone --depth 1 git@github.com:MirrorNetworking/Mirror.git "$TMP"
fi

echo ">>> 拷贝 Mirror 源码到 $TARGET ..."
mkdir -p "$TARGET"
cp -r "$TMP/Assets/Mirror/." "$TARGET/"

echo ">>> 完成。Mirror 源码已放入 $TARGET（.gitignore 已忽略，不进仓库）"
