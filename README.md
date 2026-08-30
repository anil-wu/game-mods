# Mod 概念游戏架构 — 代码仓库

基于 Mod 概念的 Unity 游戏，技术栈：Unity + Mirror（网络）+ 轻量托管 ECS（逻辑组织）。

**当前架构：V2.0**。权威设计文档见 [`design/Unity_Mod_Runtime_架构设计V2.0.md`](design/Unity_Mod_Runtime_架构设计V2.0.md)
（V1 文档 [`design/mod-architecture.md`](design/mod-architecture.md) 仅作历史参考；迁移计划见 [`design/migration-v2-plan.md`](design/migration-v2-plan.md)）。

> **Core 管运行时，Network Mod 管网络，ECS Runtime 管 ECS 世界，Mod 管自己的业务、资源、引用和对象池；Mod 之间只通过公开 API/Service 协作。**

## 架构速览（V2.0）

```
Core（AOT，稳定 ABI，无泛型 Rule 13）
│
├── ModRuntime      ModManager / ModLoader / ModResolver / ModObject
├── ECS Runtime     World / System（注册归属 ModObject）
├── Message Runtime MessageBus（二进制载荷 + 归属校验 + 强制退订）
├── 抽象接口         IMod / IModContext / INetworkContext / IUiContext / ...
└── Resource / Pool Runtime（Id + 非泛型基础设施）

Framework Mods（基础设施 Mod，加载顺序最前，禁止热卸载 §10.4）
│
├── com.game.network   协议注册表 / 路由 / 方向与版本校验 / 统计（唯一网络出入口）
└── com.game.ui        WindowManager（窗口注册表 / 层级 / CloseAll(modId)）

Gameplay Mods
│
├── com.game.core      游戏基础内容（随运行时分发）
├── com.sample.hello   示例：ECS 组件 + 系统
└── com.sample.pushbox 示例：联机推箱子（协议 + ECS 权威 + 事件）
```

核心规则（V2.0 §16 摘录）：

- **IMod 只有 `Register` / `Unregister`**（Rule 1）；Client/Server/Host 是 Context 能力（`HasClient/HasServer`，Rule 2）
- **无泛型 ABI**：跨 ABI 一律 Id + 非泛型签名（Rule 13）
- **跨 Mod 通信全部是二进制**：消息载荷、ModCall、网络协议共用字段编号线格式（Rule 14）
- **消息 = Event（Pub/Sub 无返回，谁定义谁发布）**；需要返回的跨 Mod 调用走 **ModCall**（`context.Mods.Call`，Rule 6/11）
- **跨 Mod 只有四条 Core 中介通道**：ModCall / Message / UI / Resource（Rule 12）
- **协议 ID = 稳定哈希** `xxHash32("{ModId}:{Path}")`（Rule 16）；Role × Path 正交（Rule 15）；Host 本地客户端走完整回环管线
- **依赖由 Manifest 声明**（DAG + SemVer + Client/Server Scope，Rule 7/8）；联网会话中禁止热卸载协议 Mod（Rule 17）
- **契约 = 文档**：Mod 间不共享契约程序集，字节流是唯一事实来源（Rule 19，示例见 `mods/com.sample.pushbox/CONTRACT.md`）

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

破坏性变更：在 type 后加 `!`，或在 footer 写 `BREAKING CHANGE:`。

## 目录结构

```
├── design/              设计文档（V2.0 为权威）
├── runtime/             Mod 运行时（Core 框架 + 随运行时分发的 Mod，Unity 2022.3.62f3）
│   ├── Unity/            Unity 工程（引用核心库 DLL）
│   │   ├── Assets/Scripts/     基础层引导（ModRuntime / MirrorTransport / UnityLog）
│   │   ├── Assets/Plugins/     框架核心库 DLL（构建脚本拷贝）
│   │   └── Assets/StreamingAssets/mods/   随运行时分发的 Mod 包
│   ├── src/             框架核心库源码（多目标 netstandard2.1;net8.0）
│   │   ├── Game.Mod.Contract   契约层：Id 族 / 版本 / Manifest v2 / 线格式原语 / xxHash32
│   │   ├── Game.ECS           ECS 世界：Entity / Component / System（归属 ModObject）
│   │   ├── Game.Messaging      消息总线 v2：Envelope / 二进制载荷 / 配额 / Trace
│   │   └── Game.Mod.Runtime    Mod 运行时：IMod / IModContext / ModObject / ModManager
│   ├── build-unity-dlls.sh   构建 netstandard2.1 DLL 并拷贝到 Unity
│   └── build-mod.py          构建 mods/ 创作空间 → DLL + manifest 拷到 Unity
├── relay_server/        中转服务工程（UDP 哑转发 + 控制面，.NET 8）
├── mod_server/          Mod 注册与下载服务工程（.NET 8）
├── mods/                Mod 创作空间（每个 Mod 一个目录，以 modId 命名）
│   ├── com.game.network      Network.Mod（框架：协议注册/路由/校验/统计 + Loopback）
│   ├── com.game.ui           UI.Mod（框架：WindowManager）
│   ├── com.game.core         核心 Mod（游戏基础内容）
│   ├── com.game.modstore     Mod 商店（呈现服务器 Mod → 闭包下载安装 → 动态启动）
│   ├── com.sample.hello      示例 Mod（ECS）
│   └── com.sample.pushbox    示例 Mod（联机推箱子 + CONTRACT.md 契约文档）
├── replica_projects/    可复刻的参照工程（只读对标，规范见 design/replica-conventions.md）
├── samples/             复刻案例工程（与参照同名，内含拆分后的 Mod 工程）
└── tests/               无头测试（纯 .NET，无需 Unity）
```

## 快速开始

### 构建

```bash
# 运行时核心库（netstandard2.1 + net8.0 双目标）
dotnet build runtime/Runtime.sln

# 生成 Unity 用的框架 DLL（拷贝到 runtime/Unity/Assets/Plugins/）
bash runtime/build-unity-dlls.sh      # 或 build-unity-dlls.cmd

# 构建全部 Mod → DLL + manifest（拷贝到 runtime/Unity/Assets/StreamingAssets/mods/）
python runtime/build-mod.py           # 或 bash runtime/build-mod.sh

# 打包 Mod 为 .mod（zip），供上传到 mod_server
python runtime/pack-mod.py [modId ...]   # 产物: dist/packages/*.mod

# 运行全部无头测试（契约/消息/ECS/Mod运行时/网络/UI/推箱子端到端，纯 .NET 无需 Unity）
bash tests/run.sh

# 中转服务
dotnet build relay_server/RelayServer.sln

# Mod 注册/下载服务
dotnet build mod_server/ModServer.sln
```

### Unity 工程（runtime = Core 框架 + 随运行时分发的 Mod）

- 工程位置：`runtime/Unity`，版本 **2022.3.62f3**（用 Unity Hub 打开即可）
- **Mirror 依赖**：本环境 GitHub HTTPS 不通，Mirror 源码不进仓库，**首次使用先运行 `bash runtime/setup-mirror.sh`**（SSH 浅克隆 + 拷贝到 `Assets/Mirror/`）
- 框架核心库以 netstandard2.1 DLL 形式放在 `Assets/Plugins/`（纯逻辑，无 Unity API 依赖）
- `Assets/Scripts/ModRuntime.cs` 是通用运行时引导（Host 角色装配 Core 能力 + 加载 Mods + 接线网络 Provider），**不含任何游戏特定代码**
- 所有 Mod（框架 Mod 与游戏 Mod）编译为 DLL 放入 `Assets/StreamingAssets/mods/`，运行时按 Manifest v2 依赖拓扑加载
- Mod 可自带 MonoBehaviour 视图（表现层），运行时经 `ModManager.ModLoaded` 通用实例化
- 自动验证（需先关闭 Unity 编辑器）：`bash runtime/verify-unity.sh`（编译检查 + Play 模式冒烟）

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

### Mod 商店（com.game.modstore）

运行时内呈现 mod_server 上的 Mod 列表 → 按依赖闭包下载安装到本地 mods 目录 → 动态启动：

```bash
# 1. 启动 mod_server（固定 5000 端口，与商店默认地址一致）
cd mod_server/src/ModServer
MOD_PACKAGES_DIR=../../dist/server_packages ASPNETCORE_URLS=http://127.0.0.1:5000 dotnet run -c Release --no-launch-profile

# 2. 打包并上传 Mod
python runtime/pack-mod.py
curl -F "file=@dist/packages/com.sample.hello-0.1.0.mod" http://127.0.0.1:5000/api/mods

# 3. Unity Play：右上角商店面板 → 刷新列表 → 安装并启动
```

- **演示设计**：`com.sample.hello` 与 `com.sample.pushbox` 均不在本地启动集（仅从商店分发）——
  本地启动集仅 core / network / ui / modstore 四个。点击"安装并启动"后可观察
  下载 → 安装（`persistentDataPath/mods/`）→ 动态加载的完整 UGC 闭环（pushbox 安装后立即可玩）
- 能力导出（ModCall）：`modstore:list` / `modstore:install_and_start`，其他 Mod 可直接调用
- 契约与行为约定见 `mods/com.game.modstore/CONTRACT.md`
- 会话限制：联网会话中不要启动注册了新协议的 Mod（§11.14）

## 样例复刻

以真实开源工程验证框架能力，规范见 [`design/replica-conventions.md`](design/replica-conventions.md)：

1. Unity 版本统一 **2022.3.62f3**（与原工程版本无关）；
2. 参照工程放 `replica_projects/`（只读）；
3. 复刻工程放 `samples/`，与参照同名（如 `samples/MirrorUnityFPS/`）；
4. 复刻目标文件夹下必须放置**拆分后的 Mod 工程**（`samples/<Case>/mods/com.xxx.*`，Manifest v2 + CONTRACT.md）。

当前案例：[`MirrorUnityFPS` 可行性分析](design/replica-MirrorUnityFPS-analysis.md)。

## 设计要点

- **一个 Mod = 一个独立能力单元**：自己的代码（Shared/Client/Server 模块）+ 自己的 ECS + 自己的网络协议 + 自己的 UI + 自己的资源 + 自己的对象池
- **ModObject 是资源边界**：Unload ModObject = 全部资源释放 + Pool 清理 + 注册注销 + 引用解除（§7 卸载管线由 Core 强制执行，不依赖 Mod 自觉）
- **modId**：反向域名（`com.xx.xx`），是内容/消息/资源/协议/窗口/池的命名空间根
- **Manifest v2**：`id / version / entry / modules{shared,client,server} / dependencies[{id,version,optional,scope,features}] / network.protocols / files`
- **网络**：Network.Mod 是唯一出入口（Rule 5）；协议 ID 稳定哈希（Rule 16）；方向由 API 形态强制（禁止 Client→Client）；Relay 为哑转发，云端只做 Session/Discovery
- **ECS**：World 属于 Runtime，Mod 只注册 Components / Systems（归属 ModObject，卸载移除）
- **服务端权威**：网络消息是输入，ECS 是权威状态，结果经广播/复制同步回客户端
