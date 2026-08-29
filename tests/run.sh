#!/usr/bin/env bash
# 运行全部无头测试：核心库 + 协议 Mod + 推箱子逻辑（纯 .NET，无需 Unity）
set -e
cd "$(dirname "$0")"
dotnet run --project TestRunner -c Release
