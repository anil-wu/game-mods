---
name: game-mods-conventions
description: game-mods 仓库（基于 Mod 概念的 Unity 游戏）的开发规范。涵盖：Git 分支模型（master/develop/feature，功能开发只能在 feature 分支，禁止直接提交 master/develop）、Conventional Commits 提交规范、技术栈约定（Unity 2022.3.62f3 / .NET 8 / Mirror / 轻量托管 ECS）、远程仓库 SSH 推送方式、目录结构与 Mod 领域约定。在本仓库内做任何开发、提交、推送、代码评审时使用。
---

# game-mods 项目规范

## 技术栈

- **运行时**：Unity 2022.3.62f3（`runtime/Unity`），脚本 C# 9.0 / .NET Standard 2.1
- **服务端工具**：.NET 8（`relay_server` / `mod_server`）
- **核心库**：多目标 `netstandard2.1;net8.0`，C# 9.0，零第三方依赖（含自研 JSON）
- **网络**：Mirror（服务端权威）；中转为自研 UDP 哑转发
- **逻辑组织**：轻量托管 ECS（非 Unity DOTS）

## 远程仓库

- 地址：`git@github.com:anil-wu/game-mods`（SSH）
- **HTTPS 443 在本环境不通，提交与推送一律走 SSH**
- 可用 ssh.github.com:443 兜底（`git@ssh.github.com:anil-wu/game-mods`）

## 分支规范

三类分支：

| 分支 | 用途 | 直接提交 |
|---|---|---|
| `master` | 稳定/生产 | ❌ 禁止（受保护） |
| `develop` | 集成 | ❌ 禁止（受保护） |
| `feature/*` | 功能开发 | ✅ 唯一允许 |

**规则：功能开发只能在 `feature/*` 分支进行；本 agent 无权直接提交 `master` / `develop`。**

工作流：`feature/* →(PR)→ develop →(发布)→ master`

- 从 develop 切功能分支：`git checkout -b feature/xxx develop`
- 提交后推送 feature 分支，由维护者评审合并

## 提交规范

遵循 [Conventional Commits v1.0.0](https://www.conventionalcommits.org/en/v1.0.0/)：

```
<type>[optional scope]: <description>
```

type：`feat` / `fix` / `build` / `chore` / `ci` / `docs` / `style` / `refactor` / `perf` / `test`

破坏性变更：type 后加 `!`，或 footer 写 `BREAKING CHANGE:`

## 目录结构

- `design/` —— 设计文档（权威，`mod-architecture.md`）
- `runtime/` —— 运行时：**基础层（框架）+ 核心 Mod（`com.game.core`）**，不含任何游戏特定代码
- `relay_server/` —— 中转服务（UDP 哑转发 + 控制面）
- `mod_server/` —— Mod 注册与下载服务
- `mods/` —— Mod 创作空间（第三方游戏 Mod，根目录以 modId 命名，如 `com.sample.pushbox`）

## 领域约定

- **Mod 包** = `manifest.json`（modId / version / entryDll / files）+ 文件清单（path / type / size / sha256）
- **modId** 反向域名（`com.xx.xx`），是内容/消息/资源的命名空间根
- **Mod 间通信** = 消息（广播 / 定向 / 请求应答）；**逻辑** = ECS（状态在组件，逻辑在系统）
- **runtime 分层** = 基础层（框架）+ 核心 Mod（`com.game.core` 游戏基础、`com.game.protocol` 协议服务）；游戏 Mod 一律放 `mods/` 创作空间
- **实现默认在 Mod** = Framework/核心 Mod 稳定不轻易改；一切实现默认在游戏 Mod 自身，仅当“游戏 Mod 无法实现”（引擎能力 / 跨 Mod 契约）才升级
- **Mod 自带视图** = Mod 可自带 MonoBehaviour 表现层，运行时通用实例化，不感知具体游戏
- 详细设计见 `design/mod-architecture.md`
