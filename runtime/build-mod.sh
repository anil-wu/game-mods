#!/usr/bin/env bash
# 构建 Mod（跨 Mod 依赖 + 拓扑排序），逻辑见 build-mod.py
set -e
exec python "$(dirname "$0")/build-mod.py" "$@"
