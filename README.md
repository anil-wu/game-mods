# Mod 概念游戏架构 — 代码仓库

基于 Mod 概念的 Unity 游戏，技术栈：Unity + Mirror（网络）+ 轻量托管 ECS（逻辑组织）。

完整设计文档见 [`design/mod-architecture.md`](design/mod-architecture.md)。

## 分支规范

采用 Git Flow 风格的三类分支，**功能开发只能在 `feature/*` 分支上进行**：

| 分支 | 用途 | 直接提交 |
|---|---|---|
| `master` | 稳定/生产分支，只接收 `develop` 的发布合并 | ❌ 禁止（受保护） |
| `develop` | 集成分支，汇聚各功能分支 | ❌ 禁止（受保护） |
| `feature/*` | 功能开发分支，从 `develop` 切出 | ✅ 唯一允许的开发分支 |

```
feature/* ──(PR 合并)──► develop ──(发布合并)──► master
```

工作流：

1. 从 `develop` 切功能分支：`git checkout -b feature/xxx develop`
2. 在 `feature/xxx` 上开发并提交
3. 推送 `feature/xxx`，提交 Pull Request 到 `develop`
4. `develop` 稳定后由维护者合并到 `master` 发布

## 提交规范

提交信息遵循 [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) 规范：

```
<type>[optional scope]: <description>

[optional body]

[optional footer]
```

| type | 含义 |
|---|---|
| `feat` | 新功能（minor 版本） |
| `fix` | 修复缺陷（patch 版本） |
| `build` | 构建系统或外部依赖变更 |
| `chore` | 杂项（不改源码/测试） |
| `ci` | CI 配置变更 |
| `docs` | 文档变更 |
| `style` | 代码风格（不影响逻辑） |
| `refactor` | 重构（不改变行为） |
| `perf` | 性能优化 |
| `test` | 测试相关 |

示例：

- `feat(contract): 新增 ModManifest 包格式定义`
- `fix(ecs): 修复实体销毁后组件残留`
- `refactor(relay): 重构房间路由表为线程安全`

破坏性变更：在 type 后加 `!`，或在 footer 写 `BREAKING CHANGE:`：

- `feat(api)!: 重构 Mod 加载接口`

## 目录结构

```
├── design/              设计文档
├── runtime/             Mod 运行时工程（Unity 2022.3.62f3 项目 + 框架核心库）
│   ├── Unity/            Unity 工程（引用核心库 DLL）
│   │   ├── Assets/Scripts/     引导代码（GameBootstrap）
│   │   └── Assets/Plugins/     框架核心库 DLL（构建脚本拷贝）
│   ├── src/             框架核心库源码（多目标 netstandard2.1;net8.0）
│   │   ├── Game.Mod.Contract   契约层：manifest / modId / 版本 / 依赖拓扑
│   │   ├── Game.ECS           ECS 世界：Entity / Component / System
│   │   ├── Game.Messaging      消息总线：广播 / 定向 / 请求应答
│   │   └── Game.ModLoader      ModLoader：发现 / 依赖解析 / 加载顺序
│   ├── build-unity-dlls.sh   构建 netstandard2.1 DLL 并拷贝到 Unity
│   └── build-unity-dlls.cmd   （Windows）
├── relay_server/        中转服务工程（UDP 哑转发 + 控制面，.NET 8）
├── mod_server/          Mod 注册与下载服务工程（.NET 8）
└── mods/                Mod 创作工程（示例 Mod 源工程）
```

## 快速开始

### 构建

```bash
# 运行时核心库（netstandard2.1 + net8.0 双目标）
dotnet build runtime/Runtime.sln

# 生成 Unity 用的框架 DLL（拷贝到 runtime/Unity/Assets/Plugins/）
bash runtime/build-unity-dlls.sh      # 或 build-unity-dlls.cmd

# 中转服务
dotnet build relay_server/RelayServer.sln

# Mod 注册/下载服务
dotnet build mod_server/ModServer.sln
```

### Unity 工程

- 工程位置：`runtime/Unity`，版本 **2022.3.62f3**（用 Unity Hub 打开即可）
- 框架核心库以 netstandard2.1 DLL 形式放在 `Assets/Plugins/`（纯逻辑，无 Unity API 依赖）
- `Assets/Scripts/GameBootstrap.cs` 是运行时引导（装配 World/SystemGroup/MessageBus + Mod 发现）
- Mod 源码编译成 entryDll 后放入 `Assets/StreamingAssets/mods/` 供发现加载

### 运行中转服务

```bash
cd relay_server/src/RelayServer
RELAY_PORT=7777 RELAY_PUBLIC_HOST=your.host.com dotnet run
```

- `POST /allocate` 分配房间（返回 joinCode + token）
- `POST /resolve` 解析 joinCode（返回客户端 token）
- `GET /health` 健康检查

### 运行 Mod 注册/下载服务

```bash
cd mod_server/src/ModServer
MOD_PACKAGES_DIR=./packages dotnet run
```

- `POST /api/mods` 上传 .mod 包
- `GET /api/mods` 列出所有 Mod
- `GET /api/mods/{modId}/{version}/download` 下载
- `POST /api/resolve` 解析依赖闭包

## 设计要点

- **Mod 包**：`manifest.json`（modId/version/entryDll/files）+ 文件清单（path/type/size/sha256）
- **modId**：反向域名（`com.xx.xx`），是内容/消息/资源的命名空间根
- **通信**：Mod 之间唯一契约是消息（广播/定向/请求应答）
- **逻辑**：ECS 组织（状态在组件，逻辑在系统），服务端权威
- **网络**：Mirror 三模式（Host/Dedicated/Relay），自建 Relay 为哑 UDP 转发
