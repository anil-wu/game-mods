# AGENTS.md — game-mods 仓库 AI 代理工作指南

> 给在本仓库工作的 AI 编码代理。**动手前先读完本文件**，尤其是 §5（架构红线）和 §11（禁止事项）。
> 目标是让你**不破坏架构、能通过验收**地完成任务。

---

## 1. 这是什么项目

基于 **Mod 概念**的 Unity 游戏框架：一套**支持热加载/热卸载、面向 UGC** 的 Mod Runtime（V2.0）。
技术栈：**Unity 2022.3.62f3 + 轻量托管 ECS（非 DOTS）+ Mirror（网络）+ .NET 8（服务端）**。

核心理念（设计文档 §0.1）：

> **各个 Mod 独立** ⇒ 完全隔离 ⇒ 跨 Mod 只能字节通信 ⇒ 契约即文档 ⇒ 可重复定义 ⇒ 可热卸载 ⇒ UGC 生态。

**Core 管运行时，Mod 管自己的业务/资源/协议/池/UI；Mod 之间只通过 Core 中介的公开能力协作。**

---

## 2. 权威文档（按序读）

| 文档 | 内容 |
|---|---|
| [`design/Unity_Mod_Runtime_架构设计V2.0.md`](design/Unity_Mod_Runtime_架构设计V2.0.md) | **架构权威**（§0–§19 + Rule 1–20）。改架构前先读对应§ |
| [`design/implementation-assessment.md`](design/implementation-assessment.md) | **实现现状**：Rule 1–20 逐项合规表 + 已完成工作 + 剩余路线 |
| `mods/<modId>/CONTRACT.md`、`samples/*/Mods/<modId>/CONTRACT.md` | 各 Mod 的**契约文档**（跨 Mod 字段布局的唯一事实来源） |
| [`README.md`](README.md) | 仓库总览、构建、商店、服务 |

> V1 文档（`design/mod-architecture.md`）仅历史参考，不要依据它改代码。

---

## 3. 常用命令（全部在本仓库根目录执行）

### 构建

```bash
dotnet build runtime/Runtime.sln        # 框架核心库（netstandard2.1 + net8.0 双目标）
bash runtime/build-unity-dlls.sh        # 生成 netstandard2.1 DLL → 拷到 runtime/Unity/Assets/Plugins/
python runtime/build-mod.py             # 构建全部 mods/ 与 samples 的 Mod → DLL + manifest 拷到 Unity StreamingAssets/mods/
python runtime/pack-mod.py [modId ...]  # 打包 .mod（zip）→ dist/packages/，供上传 mod_server
```

### 测试与验证（**完工前必须全绿**）

```bash
bash tests/run.sh                       # 无头测试（纯 .NET，无需 Unity）——改框架/Mod 后必跑
bash runtime/verify-unity.sh            # Unity 编译检查 + Play 冒烟（需先关闭 Unity 编辑器）
```

- **改框架（runtime/src）或 Mod 逻辑后**：`tests/run.sh` 必须全绿。
- **改了 Unity 工程/视图/框架 DLL 后**：再跑 `verify-unity.sh`。
- 改了框架 ABI（如 IModManager/IModContext）后：**重建全部 boot mods**（`python runtime/build-mod.py`），否则 Unity 里旧 DLL 报 `Method not found`。

### 服务端

```bash
dotnet build relay_server/RelayServer.sln    # 中转（UDP 哑转发）
dotnet build mod_server/ModServer.sln        # Mod 注册/下载
```

---

## 4. 目录结构（在哪动手）

```
design/            设计文档（V2.0 权威）
runtime/src/       框架核心库源码（4 个 csproj，零第三方依赖）
  Game.Mod.Contract  契约层：Id 族 / 版本 / Manifest v2 / 线格式原语(PayloadWriter/Reader/DataCodec/TestVector) / xxHash32
  Game.ECS           ECS：Entity / Component / System / World
  Game.Messaging     消息总线 v2：Envelope / 二进制载荷 / 配额 / Trace
  Game.Mod.Runtime   Mod 运行时：IMod / IModContext / ModObject / ModManager / 各 Scope
runtime/Unity/     Unity 工程（引用框架 DLL；Assets/Scripts 是通用引导，无游戏特定代码）
mods/              框架 + 示例 Mod 创作空间（com.game.* / com.sample.*）
samples/           复刻案例（如 MirrorUnityFPS，内含 Assets/Mods/<modId>/ 的拆分 Mod）
tests/TestRunner/  无头测试（直接编译 mod 源码，加载真实程序集跑）
mod_server/        Mod 注册/下载服务（.NET 8）
relay_server/      中转服务（.NET 8）
```

**分层铁律**：`runtime` 只放框架（稳定、不含游戏逻辑）；一切游戏实现默认放 `mods/` 或 `samples/<Case>/Mods/` 的具体 Mod 里。

---

## 5. 架构红线（Rule 1–20，不可违反）

> 全表见设计文档 §16 / 评估文档 §2。下面是**代理最容易踩的**：

1. **IMod 只有 `Register`/`Unregister`**（Rule 1）。不加 `Start/Update/Tick`。
2. **Mod 完全隔离（Rule 12）**：跨 Mod **只有四条 Core 中介通道**——`ModCall`（`context.Mods.Call`）/ `Message` / `UI` / `Resource`。**禁止**：引用对方程序集/类型、静态共享、反射、全局发现（`FindObjectOfType`）、直接传委托。
3. **跨 Mod 通信一律二进制（Rule 14）**：消息 / ModCall / 协议共用字段编号线格式（`PayloadWriter/Reader`）。**bytes 里不许藏对象引用**；跨边界只传纯数据（基元/string/byte[]/嵌套数组）。ModCall 用 `DataCodec` 编解码。
4. **无泛型 ABI（Rule 13）**：跨 ABI 公开接口一律 **Id + 非泛型签名**；类型安全由具体包装提供，不由泛型。（这是 HybridCLR/AOT 的地基，别图省事引入新泛型实例化。）
5. **契约即文档，可重复定义（Rule 19）**：跨 Mod **不共享数据结构程序集**；消费方按 owner 的 `CONTRACT.md` **各自写自己的解析器**（如 `XxxSnapshot.TryRead`），结构相同、定义独立。owner 改布局要发**测试向量**（`XxxTestVectors`，§14.11.3）。
6. **消息 = Event（Pub/Sub 无返回，谁定义谁发布）**；**要返回的走 ModCall**（Rule 6/11）。高频每帧数据走 **ECS Replication**，不走消息（Rule 13.6/§11.10）。
7. **注册即归属（Rule 20）**：Mod 的一切注册（系统/订阅/协议/窗口/服务/能力/池/实体/视图）归 `ModObject`，卸载时 **Core 强制回收**。`Register` 做的事，`Unregister` 对称撤销。
8. **依赖在 Manifest 声明**（DAG + SemVer + Client/Server Scope，Rule 7/8）；未声明依赖的 ModCall 直接被拒。
9. **资源走自己的 `context.Resources`**（Rule 3）；**协议自己定义**（Rule 4），协议 ID = `xxHash32("{ModId}:{Path}")`（Rule 16）。

---

## 6. 代码规范

- **核心库**（`runtime/src`）：`netstandard2.1;net8.0` 双目标，**C# 9.0**，**零第三方依赖**（连 JSON 都是自研）。
  - 注意 netstandard2.1 的限制：无 `BitConverter.UInt64BitsToDouble`、无新式 API——用兼容写法。
- **Mod 代码**：只引用框架 ABI（`Game.Mod.*`）+ 本 Mod 自身；**不引用其他业务 Mod**。
- **Unity 侧**（`runtime/Unity/Assets/Scripts`）：通用引导，**无游戏特定代码**；游戏视图放各 Mod 的 `*View.cs`。
- 注释用中文，关键处标注设计文档§号（如 `// Rule 14 二进制`、`// §9.2 实体 owner 化`）。

---

## 7. Git 规范（严格遵守）

- **分支**：功能开发**只能在 `feature/*`**；**禁止直接提交/推送 `master`、`develop`**（受保护）。
  - 当前开发分支：`feature/mod-runtime-v2`。
- **提交信息**：**Conventional Commits**——`feat/fix/build/chore/ci/docs/style/refactor/perf/test [scope]: 描述`。破坏性变更加 `!` 或 footer `BREAKING CHANGE:`。
- **推送走 SSH**：`git@github.com:anil-wu/game-mods`（本环境 GitHub HTTPS 443 不通；可用 `git@ssh.github.com:...` 兜底）。

---

## 8. Unity 工程要点

- 版本 **2022.3.62f3**；工程在 `runtime/Unity`。
- **Mirror 不进仓库**：首次用先 `bash runtime/setup-mirror.sh`（SSH 浅克隆 → `Assets/Mirror/`）。
- 框架以 netstandard2.1 DLL 放 `Assets/Plugins/`（纯逻辑、无 Unity API 依赖）。
- **启动集边界**：本地 `StreamingAssets/mods/` 只放 `core/network/ui/modstore` 四个 boot Mod；**游戏/示例 Mod 一律 `boot:false`**（商店/打包分发），别把它们拷进启动集。
- **渲染管线**：内置管线（非 URP）+ Standard 材质（原 URP 资产已确定性转换）。宿主引导建主相机 + 方向光。
- **验证工具**（`Assets/Editor/`）：`verify-unity.sh`（编译+Play 冒烟）、`EditorBridge`（HTTP 命令桥，端口 8123：`/health /console /screenshot /bounds /shaders /quit`，供批处理外实时驱动编辑器）、`ScreenshotProbe`（像素分析判洋红/内容）。

---

## 9. 测试

- 无头测试在 `tests/TestRunner/`（纯 .NET，经 `RegisterManifest + Load` 加载真实 Mod 程序集，跑真实 ModCall/ECS/网络逻辑）。
- **加测试**：新建 `XxxTests.cs`，静态类 + `[Test]` 方法，会被 `tests/run.sh` 自动发现。跨 Mod 调用用 `ModCall.Invoke/InvokeBool` 辅助（二进制）。
- **改架构必须补测试**；验收 = `tests/run.sh` 全绿 + 相关 `verify-unity.sh` 通过。

---

## 10. 新增一个 Mod 的流程

1. 在 `mods/<modId>/`（或 `samples/<Case>/Mods/<modId>/`）建目录，modId 用反向域名（`com.xx.xx`）。
2. 写 `mod.json`（Manifest v2：`id/version/entry/modules{shared,client,server}/dependencies/...`，游戏 Mod 设 `boot:false`）。
3. 写 `CONTRACT.md`：声明本 Mod 对外能力/消息/协议的**字节流字段布局**（消费方据此自解析）。
4. 实现 `IMod`：`Register` 里只注册能力（Ecs/Network/Services/Ui/Messages/ModCall），`Unregister` 对称撤销。
5. 跨 Mod 数据用 `DataCodec`/字段 Codec 编解码；消费方按对方 `CONTRACT.md` 写自己的 `XxxSnapshot.TryRead`；owner 加 `XxxTestVectors`。
6. 构建 + 测试：`python runtime/build-mod.py` + `bash tests/run.sh`。

---

## 11. 禁止事项（红线，碰了就破坏架构）

- ❌ 跨 Mod 直接引用程序集/类型、共享静态、反射、`FindObjectOfType`、传委托。
- ❌ 跨 Mod 边界传对象引用 / Unity Object / 自定义 DTO 类型（只能传纯数据字节）。
- ❌ 在跨 ABI 接口引入泛型方法/泛型参数。
- ❌ 给 `IMod` 加 `Start/Update/Tick` 等生命周期方法。
- ❌ Mod 绕过 WindowManager 直接 Instantiate 到全局 Canvas；绕过 Network.Mod 直接发网络数据。
- ❌ 把游戏/示例 Mod 放进 Unity 启动集（`StreamingAssets/mods/` 只放 4 个 boot Mod）。
- ❌ 直接提交/推送 `master`、`develop`；用 HTTPS 推远程。
- ❌ 在核心库引入第三方依赖（保持零依赖）。
- ❌ 不跑 `tests/run.sh` 就宣称完成。

---

## 12. 当前状态速查

- **Rule 1–20 全部满足**（评估文档 §2 逐项表）；无头测试 **113/113 全绿**。
- 近期完成：Rule 14 ModCall 二进制化（`DataCodec`）、契约测试向量机制（`TestVector`）。
- 推迟项（别主动做，见评估文档 §4）：网络分层补齐（要联机时）、Replication 增量、Source Generator、HybridCLR。
