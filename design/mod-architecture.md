# 基于 Mod 概念的游戏架构设计文档

> 技术栈：Unity + Mirror（网络） + 轻量托管 ECS（逻辑组织）
> 文档状态：设计定稿（v1.0）
> 本文档按 **自顶向下、逐层递进** 的顺序组织：从架构愿景与原则出发，逐层下钻到契约格式、运行时框架、网络与安全。

---

## 目录

- [第 0 章 文档说明](#第-0-章-文档说明)
- [第 1 章 顶层设计：愿景与原则](#第-1-章-顶层设计愿景与原则)
- [第 2 章 什么是 Mod](#第-2-章-什么是-mod)
- [第 3 章 Mod 生命周期](#第-3-章-mod-生命周期)
- [第 4 章 契约层：包格式与清单](#第-4-章-契约层包格式与清单)
- [第 5 章 命名空间层：全局统一约定](#第-5-章-命名空间层全局统一约定)
- [第 6 章 依赖与版本层](#第-6-章-依赖与版本层)
- [第 7 章 构建工具层](#第-7-章-构建工具层)
- [第 8 章 存储与分发层](#第-8-章-存储与分发层)
- [第 9 章 运行时框架层](#第-9-章-运行时框架层)
- [第 10 章 网络层](#第-10-章-网络层)
- [第 11 章 安全与隔离层](#第-11-章-安全与隔离层)
- [第 12 章 附录](#第-12-章-附录)

---

# 第 0 章 文档说明

## 0.1 目的

本文档定义一款以 **Mod 为核心概念** 的游戏的完整架构。游戏本体（Core Mod）与第三方 Mod 使用同一套机制构建、分发与运行。

## 0.2 阅读指南

本文档自上而下分为若干层，每层建立在上一层的基础之上：

| 层 | 章节 | 内容 |
|---|---|---|
| 愿景层 | 1 | 为什么这样设计，设计原则 |
| 概念层 | 2~3 | Mod 是什么，它的生命周期 |
| 契约层 | 4~6 | 包格式、命名空间、依赖与版本 |
| 工具层 | 7~8 | 构建与存储分发 |
| 运行时层 | 9 | ModLoader、ECS、消息、资源、存档 |
| 网络层 | 10 | Mirror 集成、部署模式、Relay、协议 |
| 安全层 | 11 | 隔离、权限、防护 |
| 附录 | 12 | 术语表、决策记录 |

## 0.3 术语约定

| 术语 | 含义 |
|---|---|
| **Mod** | 一个自包含、带版本、带清单描述的分发包，是分发/版本/加载/沙箱的原子单位 |
| **Core Mod** | 游戏本体，也以 Mod 形式存在（`com.game.core`） |
| **框架（Framework）** | 游戏内承载 Mod 运行的引擎层，随游戏分发，极少变化 |
| **modId** | Mod 的唯一标识，反向域名格式（`com.xx.xx`） |

---

# 第 1 章 顶层设计：愿景与原则

## 1.1 核心理念

> **不要写"游戏"，要写一个"能跑 Mod 的框架"，游戏内容本身是第一个 Mod。**

```
┌─────────────────────────────────────────────────────┐
│  第三方 Mod A   第三方 Mod B   玩家自制 Mod           │  ← 内容层
├─────────────────────────────────────────────────────┤
│  Core Mod（游戏本体：物品/单位/规则）                 │  ← 官方内容也是 Mod
├─────────────────────────────────────────────────────┤
│  Framework（ModLoader / ECS World / 消息总线 / 网络） │  ← 框架层（极少变化）
├─────────────────────────────────────────────────────┤
│  Unity Runtime + Bootstrap                            │  ← 引擎层
└─────────────────────────────────────────────────────┘
```

**Runtime 组成**：`runtime` 只包含 **基础层（Framework）+ 核心 Mod（`com.game.core`）**，随游戏一起分发。具体游戏（如推箱子）是第三方 Mod，在 `mods/` 创作空间实现，运行时通过通用机制加载，不感知任何具体游戏。

## 1.2 设计原则

1. **吃自己的狗粮（Dogfooding）**：Core Mod 与第三方 Mod 走完全相同的构建、分发、加载管线。框架自身通过自身暴露的 API 实现游戏内容，强制架构解耦。
2. **单一事实来源（Single Source of Truth）**：`.mod` 不可变包是唯一的真实来源。源工程、安装目录、运行实例都不是——只有包文件贯穿构建→存储→运行。
3. **数据驱动**：状态只存在于数据（组件/数据文件）中，逻辑只存在于系统/代码中。数据与逻辑严格分离。
4. **命名空间唯一**：`modId` 反向域名作为全局命名空间根，贯穿内容、消息、资源、依赖。
5. **最小契约**：manifest 只承载身份与必要元数据，其余全部靠约定；能靠约定解决的绝不写进格式。

## 1.3 全局分层架构

```
┌────────────────────────────── 契约面 ──────────────────────────────┐
│ modId / 版本 / 文件清单 / 依赖 / 内容ID / 消息ID / 资源路径         │
├────────────────────────────── 运行时面 ────────────────────────────┤
│ ModLoader(五阶段) → World(ECS)                                     │
│   ├─ 组件(状态)   ──► 存档快照 / 网络复制                           │
│   ├─ 系统(逻辑)   ──► 端侧执行 / 调度组                             │
│   └─ 消息总线(通信) ──► 本地投递 / 网络透明                          │
├────────────────────────────── 网络面 ──────────────────────────────┤
│ Mirror + 服务端权威 → Host / Dedicated / Relay 三模式               │
└───────────────────────────────────────────────────────────────────┘
```

## 1.4 三层边界：Framework / 核心 Mod / 游戏 Mod

**最高原则**：

1. **Framework 与核心 Mod 稳定**，不轻易改变。
2. **默认：一切实现都在游戏 Mod 自身**。
3. **例外：仅当游戏 Mod 无法实现时**，才下沉到核心 Mod 或 Framework。

### 判断口诀

- **默认 → 游戏 Mod**：能在 Mod 内实现的自包含实现。
- **“游戏 Mod 无法实现”才升级**：
  - 需要**引擎级能力**（热更新、域重载、网络协议、安全沙箱、资源隔离）→ Framework
  - 需要**跨 Mod 契约**（多个 Mod 必须共享同一类型才能互操作，单个 Mod 无法单方面建立）→ 核心 Mod

### 三层定位

| 层 | 定位 | 归属内容 | 稳定性 |
|---|---|---|---|
| **Framework** | 游戏无关的引擎 | 只有 Mod 无法实现的引擎能力（Mod 生命周期 / ECS / 消息 / 契约 / 网络 / 安全隔离） | 最稳定，极少变 |
| **核心 Mod**（官方核心 Mod 集合） | 游戏的地基 | 只放跨玩法 Mod 必须共享的契约，不放“方便复用”的工具；可拆分为多个 | 稳定，慎变 |
| **游戏 Mod**（mods/） | 默认实现地 | 一切能在 Mod 内实现的自包含实现 | 最自由，常变 |

**反例警示**：生命值、伤害、阵营、单位、波次等——若单个 Mod 就能自包含实现，则**留在游戏 Mod**，不要急于下沉到核心 Mod。只有当第二个 Mod 必须与之互操作时，才升级为核心 Mod 契约。

### 核心 Mod 示例

- `com.game.core` —— 游戏基础（承载跨玩法共享的基础组件 / 系统 / 内容）
- `com.game.protocol` —— 协议服务（二进制桥，分**服务端部分** / **客户端部分**，共享 ProtocolCore）：注册协议解析器（编解码器）、上行（消息 → 序列化 → 二进制 → 服务端）、下行（二进制 → 解析 → 分发给对应 Mod）

### 需求拆分与可行性分析

1. **默认在游戏 Mod 实现**。
2. 只有确认“游戏 Mod 无法实现”（引擎能力 / 跨 Mod 契约），才升级。
3. 评估每层现状与改动成本：Framework（最高，需版本兼容）> 核心 Mod（中）> 游戏 Mod（最低）。

---

# 第 2 章 什么是 Mod

## 2.1 Mod 的本质

**Mod = 一个自包含、带版本、带清单描述的分发包。**

它向外界承诺三样东西：

1. **它注册什么** —— 内容定义（实体模板、数据）
2. **它处理什么** —— 消息契约（可处理的消息类型）
3. **它声明什么** —— 依赖、端侧、权限

## 2.2 Mod 的解剖

一个 Mod 包内的逻辑构成：

```
mymod.mod                        (zip 归档)
├── manifest.json                # 清单: 身份 + 文件清单
├── mymod.dll                    # 逻辑入口 (type=code)
├── data/                        # 配置文件 (type=data)
│   └── items.json
└── assets/                      # 资源 (type=asset)
    └── hero.png
```

## 2.3 Mod 身份

Mod 的身份由 `modId` 唯一确定（详见第 5 章）。`modId` 发布后永不改变，版本演进通过 `version` 字段表达。

---

# 第 3 章 Mod 生命周期

Mod 的一生经历五个环节，形成一个闭环：

```
 ① 定义        ② 制作           ③ 构建          ④ 存储          ⑤ 运行
┌────────┐   ┌────────┐   ┌────────┐   ┌────────┐   ┌────────┐
│身份+契约│ → │源工程   │ → │.mod 包 │ → │已装包   │ → │运行实例 │
└────────┘   │(SDK)   │   │(不可变)│   │+状态   │   │(有状态) │
             └────────┘   └────────┘   └────────┘   └────────┘
```

| 环节 | 章节 | 产物 |
|---|---|---|
| ① 定义 | 2、4、5 | mod.json 规范 |
| ② 制作 | 7 | 源工程 |
| ③ 构建 | 7 | `.mod` 包 |
| ④ 存储 | 8 | 已装包 + installed.json |
| ⑤ 运行 | 9、10 | 运行实例（内存对象） |

**贯穿全链的三条主线**：

1. `manifest.json` —— 身份与契约，每个环节的"身份证"
2. `.mod` 不可变包 —— 构建产物 = 分发单位 = 存储单位 = 运行输入
3. 三层版本体系 —— API 版本 / 数据 Schema 版本 / 消息协议版本

---

# 第 4 章 契约层：包格式与清单

## 4.1 包结构（.mod）

`.mod` 是 zip 归档，包内根目录即逻辑结构：

```
mymod-1.2.0.mod
├── manifest.json
├── mymod.dll
├── data/items.json
└── assets/hero.png
```

**不可变性**：`.mod` 包只读、不可变、自包含、可哈希。这是去重、回滚、联机校验、断点续传的前提。

## 4.2 manifest.json 规范

```json
{
  "modId": "com.mycompany.mymod",
  "version": "1.2.0",
  "entryDll": "mymod.dll",
  "dependencies": {
    "com.game.core": ">=1.0.0"
  },
  "files": [
    { "path": "mymod.dll",       "type": "code",  "size": 24576,  "sha256": "8f3a..." },
    { "path": "data/items.json", "type": "data",  "size": 340,    "sha256": "c1d2..." },
    { "path": "assets/hero.png", "type": "asset", "size": 102400, "sha256": "99ab..." }
  ]
}
```

### 字段定义

| 字段 | 必填 | 说明 |
|---|---|---|
| `modId` | ✅ | 反向域名标识，`com.xx.xx` |
| `version` | ✅ | semver 语义化版本 |
| `entryDll` | ✅ | 逻辑入口程序集文件名 |
| `dependencies` | ❌ | 依赖声明（缺省 = 无依赖），见第 6 章 |
| `files` | ✅ | 文件清单，见 4.3 |

**最小化原则**：manifest 核心字段保持这五个不再膨胀。未来需要的字段（apiVersion、sides、permissions）作为**可选字段**追加，缺失取默认值。

## 4.3 文件清单（files[]）

文件清单是 manifest 中最关键的部分——四个字段之外的复杂性全部由它吸收：

| 属性 | 说明 |
|---|---|
| `path` | 包内相对路径，也是资源寻址路径（见 5.3） |
| `type` | 文件类型，决定加载管线 |
| `size` | 字节数 |
| `sha256` | 内容哈希 |

### 文件清单的四个用途

| 用途 | 机制 |
|---|---|
| 完整性校验 | 安装/加载时对 hash，防损坏与篡改 |
| 版本判定 | 两份清单逐项相同 = 内容一致（联机校验直接比对清单） |
| 增量更新 | 对比旧清单，只下载变化的文件 |
| 去重缓存 | 相同 hash 的文件全局只存一份 |

## 4.4 文件类型（type）

| type | 含义 | 加载器动作 | 端侧 |
|---|---|---|---|
| `code` | 程序集 (.dll) | 反射加载 → 扫描 IMod 入口 | 按文件名约定分端（`*.server.dll`） |
| `data` | 数据/配置文件 (JSON等) | 按 Schema 解析 → 注册内容定义 | 双端 |
| `asset` | 资源 (贴图/模型/音频/bundle) | 交给资产管线挂载 | 以客户端为主 |

`type` 的三个价值：

1. **确定性路由** —— 不猜扩展名，看 type 走对应管线
2. **扫描期即校验** —— 读清单时即可断言"所有 code 必须能加载为程序集、所有 data 必须能过 Schema"，加载前快速失败
3. **分端过滤的依据** —— 独立服务器先看 type（`asset` 直接跳过）

---

# 第 5 章 命名空间层：全局统一约定

## 5.1 modId 规范

```
com.mycompany.mymod
```

- 小写，分段 `[a-z0-9]+`，点分隔，通常三段起（com.组织.项目）
- 全局唯一靠**域名/组织归属**保证，不需要中央注册表
- 发布后**永不改变**

## 5.2 modId 是命名空间根

`modId` 是这个 Mod 注册的一切的命名空间前缀。官方 Core Mod 占保留域名（如 `com.game.core`）。

## 5.3 三类命名空间统一

| 契约 | 形式 | 示例 |
|---|---|---|
| 内容引用 | `modId:localId` | `com.other.weapons:iron_ingot` |
| 消息通信 | `modId:msgName` | `com.other.weapons:query_price` |
| 资源寻址 | `modId:资源路径` | `com.other.mod:assets/hero.png` |

**`modId:` 前缀是贯穿全链的唯一语法。**

## 5.4 资源寻址规则

```
本Mod资源:   "assets/hero.png"
外部Mod资源: "com.other.mod:assets/hero.png"
```

解析规则：**含 `:` 且前缀是已加载 Mod 的 modId → 外部资源；否则 → 本 Mod 资源**。包内路径不含 `:`（zip 内无盘符），无歧义。

资源路径与 manifest `files[].path` **直接对应**，不引入第二套路径体系。仅 `type=asset` 的文件可被资源路径寻址；`type=data` 走 Registry，`type=code` 走程序集加载。

---

# 第 6 章 依赖与版本层

## 6.1 dependencies 字段

```json
"dependencies": {
  "com.game.core": ">=1.0.0",
  "com.other.weapons": "^2.1.0"
}
```

### 版本范围（semver 语义）

| 写法 | 含义 |
|---|---|
| `^2.1.0` | 兼容：2.x，≥2.1.0 |
| `~2.1.0` | 补丁：2.1.x |
| `>=1.0.0` / `<2.0.0` | 区间 |
| `2.1.0` | 精确 |

## 6.2 依赖的四层影响

| 环节 | 依赖的作用 |
|---|---|
| 构建 | A 代码引用了 B 类型 → 编译时需 B 的 dll 作引用 |
| 安装 | 解析依赖 → 缺则下载/升级，冲突则报告 |
| 加载 | 拓扑排序定加载顺序，环检测，版本校验 |
| 运行 | Registry / 资源 / 消息的跨 Mod 引用解析 |

## 6.3 版本模型：逻辑 Mod vs 版本实例

**核心决策：运行时多个版本可以同时存在**（解决依赖版本冲突的根本方案）。

```
逻辑Mod      com.other.weapons            ← 身份, 永不变
版本实例    com.other.weapons@2.1.0      ← 实际加载单元
版本实例    com.other.weapons@1.5.0      ← 可同时加载
```

每个 Mod 在加载时把依赖**绑定**到具体版本实例：

```
A 的依赖绑定: { weapons → @2.1.0 }
C 的依赖绑定: { weapons → @1.5.0 }
```

### 内容共存 vs 代码共存

| 层 | 能否共存 | 约束 |
|---|---|---|
| 内容共存（data/assets） | 任何运行时都行 | 只需作用域解析 |
| 代码共存（程序集） | 依赖运行时 | 见下表 |

| 运行时 | 多版本程序集共存 |
|---|---|
| Mono（Unity默认） | ❌ 单 AppDomain，同名程序集冲突；仅可用"版本化程序集名"绕过 |
| CoreCLR（Unity 6+） | ✅ AssemblyLoadContext 天然支持 |
| IL2CPP | ❌ AOT，无动态加载 |

### 分期建议

1. **v1**：单版本激活 + 冲突报告，但 Registry 键、存档格式、握手协议**预留版本维度**
2. **v2**：真·多版本共存，激活作用域解析 + ALC 隔离（要求 CoreCLR）

## 6.4 加载顺序：拓扑排序与环检测

```csharp
// 依赖图: 边 A→B 表示 A 依赖 B (B 必须先加载)
var order = TopoSort(mods, m => m.Dependencies.Keys);
// 结果: [core, weapons, mymod, ...]  B 永远在 A 之前

// 环检测: A→B→A → 排序失败, 报"循环依赖", 明确列出环
```

加载前逐个校验：

```csharp
foreach (var (depId, range) in mod.Dependencies)
{
    if (!loaded.TryGet(depId, out var dep))
        Disable(mod, $"缺少依赖 {depId}");
    else if (!range.SatisfiedBy(dep.Version))
        Disable(mod, $"依赖 {depId} 版本 {dep.Version} 不满足 {range}");
}
```

依赖加载失败 → **该 Mod 连带禁用**（级联），错误信息列出完整链条。

## 6.5 依赖作用域

跨 Mod 引用（内容/资源/消息）一律通过**依赖作用域**解析：

```
A 写 "com.other.mod:assets/hero.png"
  → 查 A 的依赖绑定 → 从 weapons@2.1.0 的包里取 assets/hero.png
  → A 未依赖 com.other.mod → 报错: "资源引用了未声明的依赖"
```

策略：**外部资源/内容访问 = 依赖作用域**。必须声明依赖才能引用，既保证版本判定有依据，也防止绕过依赖偷用资源。

---

# 第 7 章 构建工具层

## 7.1 源工程结构

核心原则：**源工程结构 1:1 映射到包结构**，构建工具看到目录就知道怎么打包。

**命名约定：源工程根目录以 modId 命名**（与 `mod.json` 的 `modId` 字段一致，如 `com.mycompany.mymod`）。

```
com.mycompany.mymod/            # 源工程根目录 = modId
├── mod.json                    # 源清单 (modId + version + entryDll)
├── src/                        # C# 源码 → 编译成 entryDll
│   ├── ModEntry.cs             # IMod 实现
│   └── ...
├── data/                       # 配置文件 → 原样进包, type=data
│   └── items.json
└── assets/                     # 资源 → type=asset
    └── hero.png
```

**目录就是类型声明**：`src/→code`、`data/→data`、`assets/→asset`。

## 7.2 源清单 vs 包清单

同一个 schema，区别只在构建工具补上了 `files`：

```jsonc
// 源工程 mod.json (作者手写, 就三字段)
{ "modId": "com.mycompany.mymod", "version": "1.2.0", "entryDll": "mymod.dll" }

// 构建后 manifest.json (工具生成, 多了 files)
{ "modId": "...", "version": "...", "entryDll": "...",
  "files": [ /* path/type/size/sha256, 工具自动算 */ ] }
```

作者永远不手写 hash。

## 7.3 编译方式（关键决策）

| 方案 | 做法 | 取舍 |
|---|---|---|
| **A. 纯 dotnet 编译（推荐）** | SDK 发 reference assemblies（UnityEngine.dll、Framework.API.dll），modbuild 内置 Roslyn 编译 | 无 Unity 依赖、CI 友好、秒级构建 |
| B. Unity batchmode 编译 | mod 是 Unity 子工程 | 构建重、绑定 Unity 版本 |

选 A：Mod 生态的健康度取决于构建门槛，作者不该为编译一个 Mod 去装完整引擎。reference assemblies 只是编译期桩，运行时由游戏注入真实实现。

**补充**：当 Mod 自带 MonoBehaviour 视图（表现层）时，编译需额外引用 UnityEngine 程序集（`CoreModule` / `IMGUIModule` / `InputLegacyModule` 等模块 DLL，取自 Unity 安装目录 `Editor/Data/Managed/UnityEngine/`）。纯逻辑 Mod 无需引用。

## 7.4 modbuild CLI

```
modbuild new      com.xx.yy       脚手架生成源工程
modbuild build    <path>          构建 → mymod-<version>.mod
modbuild validate <path>          只校验不打包 (lint)
modbuild inspect  <file.mod>      查看包内清单/内容
```

## 7.5 构建流水线

```
modbuild build mymod/
  ① 读 mod.json (三字段)
  ② 编译 src/ → mymod.dll
       - Roslyn + reference assemblies + Framework.API.dll
       - 运行 Source Generators (序列化代码生成)
  ③ 校验: data/ 过 Schema, assets/ 过格式检查
  ④ 组装: mymod.dll(type=code) + data/*(type=data) + assets/*(type=asset)
  ⑤ 生成 manifest.json: 三字段 + files[] (自动算 size/sha256)
  ⑥ 确定性打包 → mymod-1.2.0.mod
        - zip 固定时间戳 + 文件排序 → 同输入必得同 hash
```

确定性打包是联机校验、增量更新、去重缓存的前提。

## 7.6 分期

- **v1**：`assets/` 原样拷进包，运行时加载。纯数据/代码 Mod 不碰 Unity 即可构建。
- **v2**：需要复杂资源时走 Addressables 打包 bundle（此步才需 Unity），包格式不变。

---

# 第 8 章 存储与分发层

## 8.1 本地存储布局

```
game/mods/
├── core/1.0.0.mod          # 官方 Core Mod(随游戏分发, 始终存在)
├── mymod/1.2.0.mod         # 不可变包, 只读
├── mymod/1.1.0.mod         # 旧版本保留 → 支持回滚
└── installed.json          # 安装状态(唯一可变文件)
```

**关键点**：`.mod` 包只读不可变，唯一的可变状态是 `installed.json`——启停、锁版本、加载顺序全在这里。"装了什么"和"状态如何"彻底分离。

## 8.2 installed.json

```json
{
  "enabled": ["core", "mymod"],
  "pinnedVersion": { "mymod": "1.2.0" },
  "loadOrder": { "mymod": 10 }
}
```

## 8.3 分发渠道

| 渠道 | 说明 | 适用 |
|---|---|---|
| Steam Workshop | id + published file，自动下载/更新 | PC 主力 |
| 自建 Registry | HTTP 服务：Mod 索引 + 版本元数据 + 增量更新 | 全平台、可控 |
| 手动安装 | 拖入 `.mod` 文件 | 兜底 |

## 8.4 安装即依赖解析

装 Mod → 读依赖 → 递归下载 → 版本约束求解 → 冲突检测。**一个 modId 磁盘上可存多个版本，但默认运行时只激活一个**（v1），冲突时报告让用户抉择（v1）；多版本共存激活在 v2。

---

# 第 9 章 运行时框架层

## 9.1 ModLoader 加载五阶段

```
Bootstrap (引擎层)
  │
  ▼
Phase 0 发现: 扫描 mods/ → 读 installed.json 定启停/顺序 → 解析 manifest.json
  │           校验(版本/依赖/权限) → 冲突检测 → 失败项禁用并报告
  ▼
Phase 1 加载程序集: 按端过滤(shared+server / shared+client) → 加载 dll
  │
  ▼
Phase 2 注册内容: 所有 Mod 平等地往 Registry/World 放定义 (此阶段只注册, 不读取)
  │
  ▼
Phase 3 冻结+解析: 注册表冻结 → 解析交叉引用(内容/资源/依赖绑定)
  │
  ▼
Phase 4 启动: 触发 OnEnable → 进入游戏循环
```

**Phase2/3 分离的意义**：注册阶段大家平等放内容，冻结后再统一解析引用，避免加载顺序导致的"引用了还没注册的东西"。

### Mod 状态机

```
Loaded → Registered → Enabled → Active
              (可运行时降级/禁用, 用于调试与故障隔离)
```

### Mod 视图（自带表现层）

Mod 可自带 MonoBehaviour 视图（表现层），与入口同属一个 Mod 程序集，通过入口暴露的静态桥访问框架服务（World / Systems / Messages）。加载完成后，运行时**通用地**扫描 Mod 程序集并实例化其中的 MonoBehaviour——运行时无需感知任何具体 Mod。游戏循环由 Mod 视图驱动：输入 → 系统 tick → 读取状态 → 渲染。

## 9.2 ECS 世界（逻辑组织核心）

### 9.2.1 选型：轻量托管 ECS

| | Unity DOTS (Entities) | 轻量托管 ECS（推荐） |
|---|---|---|
| 组件 | 仅 unmanaged struct | struct，约束松 |
| 系统 | struct + Burst + 代码生成 | 普通 class，无代码生成 |
| 外部 DLL 定义组件/系统 | ❌ 极难 | ✅ 天然支持 |
| 运行时 | 依赖 IL2CPP + Burst | Mono/IL2CPP/CoreCLR 都行 |

**结论：自研或采用 LeoEcs 风格托管 ECS。** DOTS 的 AOT/代码生成/struct 约束对 Mod 生态是致命的。

### 9.2.2 世界模型

```
World     —— 框架提供的全局运行时服务 (每个游戏会话一个)
Entity    —— 纯 ID (uint, 跨网络稳定)
Component —— 纯数据结构, 状态的全部载体
System    —— 逻辑, 按 Query 找实体集合跑逻辑, 声明端侧
```

**核心纪律：状态只在组件里，逻辑只在系统里。**

### 9.2.3 Mod 逻辑组织

```csharp
// 组件 = 纯数据
public struct Health : IComponent {
    public int Value;
    public int Max;
}
public struct Poisoned : IComponent {
    public float Dps;
    public float Duration;
}

// 系统 = 逻辑
public class PoisonSystem : ISystem {
    public void Update(SystemContext ctx) {
        foreach (var (e, poison, health) in ctx.Query<Poisoned, Health>()) {
            health.Value -= (int)(poison.Dps * ctx.DeltaTime);
            if (health.Value <= 0)
                ctx.World.Add(e, new Dead());
        }
    }
}

// OnLoad 注册
void OnLoad(IModContext ctx) {
    ctx.World.RegisterComponent<Health>();
    ctx.World.RegisterSystem<PoisonSystem>(SystemSide.Server);
}
```

### 9.2.4 端侧声明

| 端侧 | 含义 |
|---|---|
| `Server` | 服务端权威逻辑（伤害计算、掉落、AI） |
| `Client` | 客户端表现逻辑（特效、音效、UI） |
| `Shared` | 双端确定性逻辑（慎用） |

**纪律：一切状态变更只发生在服务端系统**，客户端系统只做表现和发请求。

### 9.2.5 跨 Mod 组合（杀手特性）

```csharp
// Core Mod 定义 Health + 创建实体, 完全不知道 mymod 的存在
// mymod 给 core 的实体加组件 —— 不需要 core 做任何配合
ctx.World.Add(targetEntity, new Poisoned { Dps = 5, Duration = 10 });
```

Mod 通过"给已有实体加组件 + 挂系统"扩展别人，而非继承或事件。天然解耦、天然可叠加。

### 9.2.6 系统调度

```
SystemGroup 有序执行, 支持依赖声明:
  [Input] → [Simulation(Server)] → [Sync] → [Presentation(Client)]
                    │
          Mod 系统按注册顺序 + 显式 Before/After 排进组
```

## 9.3 消息总线（Mod 间通信）

**核心决策：Mod 之间的唯一通信契约是消息，不是 API 引用。**

### 9.3.1 三类消息模式

```
① 广播 Event       A 发 → 谁订阅谁收
② 定向 Directed    A 发 → 指定 Mod B
③ 请求-应答        A 问 → B 回结果 (定向消息 + 关联ID)
```

### 9.3.2 寻址

消息按**逻辑 modId 寻址**，具体版本由依赖绑定解析：

```
A 发送目标: "com.other.weapons"
  → 查 A 的依赖绑定 → 投递到 A 绑定的 weapons@2.1.0
```

### 9.3.3 消息契约 vs API 引用

| 旧模型（直接调用） | 消息模型 |
|---|---|
| A 引用 B 的程序集、调 B 的类 | A 只声明"发这个类型的消息" |
| 编译期耦合 | 松耦合，契约 = 消息 schema |
| B 没装 → 缺引用崩溃 | B 没装 → 投递失败，优雅降级 |

### 9.3.4 API

```csharp
// Mod B OnLoad
ctx.Messages.Handle<QueryPriceMsg>(OnQueryPrice);       // 定向+请求应答
ctx.Messages.Subscribe<OnItemChanged>(OnItemChanged);   // 广播订阅

PriceReply OnQueryPrice(QueryPriceMsg msg) { ... }

// Mod A 使用方
ctx.Messages.Request<QueryPriceMsg, PriceReply>(
    "com.other.weapons",                        // 逻辑寻址
    new QueryPriceMsg { ItemId = id },
    reply => ShowPrice(reply.Price),            // 成功
    error => Fallback());                       // B不在/超时 → 降级
```

### 9.3.5 本地/网络透明

```
A 发 QueryPriceMsg 给 B:
  框架判断: B 在本进程? → 直接投递到 B 的 handler
                       : → 序列化进信封 → 网络 → 对端投递

Mod 代码同一行, 不感知 B 在哪一端
```

"Mod 间通信" 和 "客户端/服务端通信" 是同一种东西的两个位置。

### 9.3.6 消息 schema 版本化

多版本共存下消息带 schema 版本，宽容解析（长度前缀 + 追加字段跳过）：

```
B@2.1 处理 query_price@2   B@1.5 处理 query_price@1
A 发 @2 格式 → B@1.5 收到: 未知尾部字段跳过
```

### 9.3.7 失败处理

| 场景 | 行为 |
|---|---|
| 目标 Mod 未加载 | 请求-应答：立即失败回调；定向/广播：丢弃 + 日志 |
| handler 抛异常 | 隔离，只降级该 Mod |
| schema 不符 | 宽容解析，不崩 |

## 9.4 资源加载

```csharp
ctx.Assets.Load<Texture2D>("assets/hero.png");
ctx.Assets.Load<Texture2D>("com.other.shared:assets/hero.png");
```

资源引用主要出现在 `data` 定义里，加载 Phase3 时解析为"具体版本实例包内的真实文件"：

```json
{
  "id": "com.mycompany.mymod:hero",
  "icon": "assets/hero.png",
  "model": "com.other.shared:assets/hero.fbx"
}
```

资源缺失 → 返回错误/占位，不抛崩溃。

## 9.5 存档系统

### 9.5.1 核心：组件快照

ECS 使存档天然简洁——状态在组件里，组件是纯数据：

```
存档 = 实体列表 + 每实体组件数据快照
```

### 9.5.2 绑定快照（多版本共存必需）

```json
{
  "bindings": {
    "com.mycompany.mymod": { "com.other.weapons": "2.1.0" },
    "com.thirdparty.c":     { "com.other.weapons": "1.5.0" }
  }
}
```

存档里存的 `com.other.weapons:iron_ingot` 必须附带绑定快照，否则无法判定解析到哪个版本。

### 9.5.3 纪律

- 存档头部记录 **Mod 列表快照**（id + version + 绑定）
- 持久化引用走字符串 ID + 映射表，**禁止数组索引引用**
- 读档时：缺 Mod → 警告并列出；内容移除 → 提供 migration 规则（旧 ID → 新 ID 或默认物）
- 存档是**服务端特权**，只发生在服务端

---

# 第 10 章 网络层

## 10.1 总体

- 网络库：**Mirror**（状态同步 + 服务端权威）
- 服务端权威：一切状态变更只发生在服务端，客户端只做表现和发请求

## 10.2 部署模式

```
                    ┌──────────── 统一的部分 ────────────┐
                    │  Mod握手 → Registry同步 → 游戏循环  │
                    └─────────────────▲─────────────────┘
        ┌─────────────────┬───────────┴──────────┬──────────────────┐
        │   Host 模式      │   Dedicated 模式      │   Relay 中转模式   │
        ├─────────────────┼─────────────────────┼──────────────────┤
        │ StartHost()     │ StartServer()        │ 分配Relay房间      │
        │ 房主=服务器      │ Headless无渲染       │ → StartHost/Client │
        │ 存档在房主机器   │ 存档在服务器          │ JoinCode进房      │
        │ NAT是死穴→退Relay│ 需端口开放           │ 无需端口转发       │
        └─────────────────┴─────────────────────┴──────────────────┘
```

### 会话抽象

```csharp
public enum SessionMode { Host, Dedicated, Relay }

public abstract class JoinMethod { }
public class DirectJoin : JoinMethod { public string Ip; public ushort Port; }
public class LanJoin    : JoinMethod { }
public class RelayJoin  : JoinMethod { public string JoinCode; }

public interface INetSession
{
    void StartHost(JoinMethod method);
    void StartDedicated(DedicatedConfig cfg);
    void Join(JoinMethod method);
}
```

启动时根据 `JoinMethod` 类型换掉 NetworkManager 上的 Transport 组件再 Start，上层无感。

## 10.3 服务端权威的两条铁律（Host 模式陷阱）

Host 模式 = 同进程跑 Server + Client，房主走 `localConnection`，可零延迟摸到服务器内存。代码在 Host 下测试正常、上独立服务器全崩是经典灾难。铁律：

1. **状态变更只有一条路径**：哪怕房主输入，也走 `输入 → 服务端校验 → 状态变更 → 同步` 完整链路，禁止本地捷径
2. **Mod 代码物理分层**：server 程序集禁止引用渲染 API，用程序集拆分让编译器强制，而非靠自觉

## 10.4 自建 Relay

**定位**：Relay 只服务 Host 模式的 NAT 兜底（Dedicated 有公网 IP 直连）。它是基础设施，不是游戏逻辑。

### 10.4.1 控制面 / 数据面分离

```
┌─────────────────────┐         ┌──────────────────────────┐
│  Allocation Service  │         │   Relay Node (可多节点)    │
│  (控制面, HTTP)      │         │   (数据面, UDP转发)        │
│  · 分配房间/JoinCode │─签发token─►  校验token(无状态HMAC)    │
│  · 解析Code→节点地址  │         │  · 房间路由表(内存)        │
│  · 节点选择/容量调度   │         │  · 不透明转发, 不解析游戏协议│
└─────────▲───────────┘         └───────▲──────────▲───────┘
          │ POST /allocate /resolve     │ 房主UDP │ 客户端UDP
          └─────────────────────────────┴─────────┘
```

**核心决策：Relay 是哑的。** 只按房间路由 UDP 数据报，完全不理解 Mirror/KCP/游戏协议。好处：与游戏版本解耦、安全（载荷不透明）、可复用、可水平扩展。

v1 可把两服务跑同一进程，但代码结构按分离设计。

### 10.4.2 JoinCode 流程

```
房主: POST /allocate → { joinCode, relayNode, token }
      → RelayTransport 连 relayNode → BindHost(token)
      → 隧道上启动 KCP + Mirror Host
      → UI 显示 joinCode

客户端: 输入 joinCode → POST /resolve → { relayNode, token }
        → RelayTransport 连 relayNode → BindClient(token)
        → KCP 握手 → Mirror Client 连接
        → 统一流程: Mod握手 → Registry同步 → 进游戏
```

### 10.4.3 协议分层

```
Mirror 消息
  └─ KCP (可靠有序, 端到端: 房主↔客户端)      ← 可靠性在这里
       └─ Relay隧道信封 (房间路由头, 不可靠)   ← Relay只看这一层
            └─ UDP → Relay Node → UDP
```

KCP 在隧道之上端到端跑，Relay 不终结 KCP——游戏代码感知不到自己在直连还是中转。

### 10.4.4 Relay 协议信封

```
Envelope: [magic u16][ver u8][type u8][token 16B][connId u16][payload...]

控制消息:
  BindHost(token)           → Ack / Fail(reason)
  BindClient(token)         → Ack(分配的connId) / Fail
  PeerJoined(connId)        → 推给房主
  PeerLeft(connId)          → 推给房主
  Data(connId, payload)     → 房主↔客户端双向
  Ping/Pong                 → NAT保活(10~15s) + 延迟测量

Token (无状态HMAC):
  token = HMAC-SHA256({roomId, role, expiry}, 节点共享密钥) → 截断16字节
```

Relay 节点内存状态：

```csharp
class Room {
    IPEndPoint Host;
    Dictionary<ushort, IPEndPoint> Clients;
    DateTime LastActivity;   // 清扫线程: 闲置>60s关房
}
```

### 10.4.5 QoS 双通道

```
隧道报文 type=DATA_KCP  → 载荷是 KCP 段    (可靠流量)
隧道报文 type=DATA_RAW  → 载荷是裸游戏报文 (不可靠流量, 不进 KCP)
```

Relay 对两者一视同仁哑转发——QoS 双通道不需要 Relay 做任何改动。

### 10.4.6 技术栈与成本

- **.NET 8 控制台程序**（不是 Headless Unity），信封协议代码做成 netstandard 类库双端共享
- 部署：Docker / systemd / Windows Service
- 成本直觉：4人房 30Hz 每客户端 5~20 KB/s → 单房约 60 KB/s → **主要成本是带宽，不是机器**

### 10.4.7 路线图

```
v1 (能玩):   单进程(控制面+数据面合一) + UDP隧道 + 单节点 + 限流
v2 (省钱):   UDP打洞优先, Relay做介绍人+兜底  ← 带宽成本大头在这省
v3 (规模化): 多节点 + 就近接入 + 端到端加密
```

## 10.5 协议复用层（Mod 自定义协议）

### 10.5.1 核心决策：收敛到固定信封

```
Mod 一律禁止直接碰 NetworkServer/NetworkClient 的 RegisterHandler/Send
只能走框架的 ctx.Net API → 协议复用层 → 固定信封消息
```

两个硬理由：

1. **Mirror 消息 ID 按运行时注册顺序分配**——Mod 各自 RegisterHandler 会因加载顺序不同导致双端 ID 错位
2. **Weaver 只覆盖工程内程序集**——外部 Mod DLL 不会被织入

### 10.5.2 信封消息

```csharp
// Game.Framework.Net 程序集 —— 全工程唯一 Mod 协议消息类型
public struct ModMsgBatch : NetworkMessage
{
    public byte[] Payload;
    // 内部布局: [count u8]
    //   每条: [modNetId u16][msgNetId u16][payloadLen u16][payload bytes]
}
```

QoS 发送时选 channel：

```csharp
NetworkServer.SendToAll(batch, Channels.Reliable);      // QoS.Reliable
NetworkServer.SendToAll(batch, Channels.Unreliable);    // QoS.UnreliableSequenced
```

### 10.5.3 序列化：Source Generator 干掉 Weaver

```csharp
[NetMessage(QoS.Reliable)]
public partial struct CastSpellMsg
{
    public ushort SpellNetId;
    public Vector3 Target;
}
```

SDK 内置 Roslyn 源生成器，Mod 编译期生成序列化代码——不依赖 IL 织入，IL2CPP 友好，生成代码可调试可审查。

### 10.5.4 收发路径

```csharp
// 发送侧 (tick末合包)
void Flush()
{
    if (_reliable.Count > 0)
        NetworkClient.Send(new ModMsgBatch { Payload = _reliable.Pack() }, Channels.Reliable);
    if (_unreliable.Count > 0)
        NetworkClient.Send(new ModMsgBatch { Payload = _unreliable.Pack() }, Channels.Unreliable);
}

// 接收侧 (三层防护)
void OnServerBatch(NetworkConnectionToClient conn, ModMsgBatch batch)
{
    foreach (var env in EnvelopeReader.Read(batch.Payload))
    {
        if (!RateLimiter.Allow(conn, env.ModNetId, env.PayloadLen)) continue;   // 1.限速
        if (!_routes.TryGet(env.ModNetId, env.MsgNetId, out var route)) continue; // 2.未知消息丢弃
        try { route.Invoke(conn, env.Payload); }                                 // 3.异常隔离
        catch (Exception e) { ModIsolation.Report(env.ModNetId, e); }
    }
}
```

### 10.5.5 分包约束

```
不可靠 batch: 必须 < MTU (~1200B 安全线) → 装不下拆多个 batch
可靠 batch:   上限按 transport (KCP 可靠消息上限, 约百KB级)
单条 payload: 硬上限 8KB (可配)
```

### 10.5.6 协议核心 Mod（com.game.protocol）—— 二进制桥

协议复用层作为**核心 Mod** 落地（而非 Framework），因为它属于「跨玩法 Mod 共享的契约」。纯传输层，无游戏逻辑：

```
                协议 Mod（com.game.protocol）—— 纯传输层，无游戏逻辑
   ┌─────────────────────────────────────────────────────────────────────┐
   │     ProtocolClient（客户端部分）           ProtocolServer（服务端部分）   │
   │     ┌────────────────────────────┐      ┌────────────────────────────┐│
   │     │ 客户端解析器注册表           │      │ 服务端解析器注册表           ││
   │     │ msgId → (owner, codec)    │      │ msgId → (owner, codec)    ││
   │     └────────────────────────────┘      └────────────────────────────┘│
   └─────────────────────────────────────────────────────────────────────┘

① 上行（客户端 Mod → 服务端 Mod）：
   客户端Mod ──消息──► Client ──序列化──► [msgId|payload] ──SendToServer──► 网络
   服务端Mod ◄──分发── Server ◄──解析(服务端注册表) ◄──Receive ◄────────────┘

② 下行（服务端 Mod → 客户端 Mod，单发 / 广播）：
   服务端Mod ──消息──► Server ──序列化──► [msgId|payload] ──SendToAll/One──► 网络
   客户端Mod ◄──分发── Client ◄──解析(客户端注册表) ◄──Receive ◄────────────┘
```

| # | 职责 | API |
|---|---|---|
| ① | 接受游戏 Mod 客户端消息 → 序列化 → 二进制 → 服务端 | `Client.Send` + `SendToServer` |
| ② | 接受客户端二进制 → 按服务端注册表找解析器 → 解析 → 分发对应服务端 Mod | `Server.Receive` |
| ③ | 接受游戏 Mod 服务端消息 → 序列化 → 二进制 → 客户端（单发/广播） | `Server.Send` + `SendToAll`/`SendToOne` |
| ④ | 接受服务端二进制 → 按客户端注册表找解析器 → 解析 → 分发对应客户端 Mod | `Client.Receive` |

**要点**：客户端与服务端各自维护独立解析器注册表；`SendToServer` / `SendToAll` / `SendToOne` 是网络钩子（待 Mirror 接入注入）；协议 Mod 只做二进制 ↔ 消息的传输与路由，不含任何游戏逻辑。

## 10.6 握手流程

```
1. Transport 连接建立 (因模式而异: Direct/LAN/Relay)
2. ModAuthenticator: Mod 清单校验 (id+version+绑定集)
3. RegistrySync: 服务端下发 内容网络ID表 (string↔ushort 映射)
4. ProtocolSync: 服务端下发 消息网络ID表 (modNetId,msgNetId ↔ 类型)
   - 客户端校验版本 + 建立映射 → ACK
   - ★ ACK 前客户端不调用 NetworkClient.Ready()
     → 服务端不会 spawn 玩家对象 → 不可能"对象到了但协议表没建好"
5. Ready → Mirror 正常 spawn → 进入游戏
```

**房主本地客户端自动通过 Mod 握手**（同进程同一份 Mod，天然一致）。

## 10.7 ECS 状态同步 = 组件复制

**最大的红利**：状态同步退化为组件复制，由框架统一做。

```
服务端: 系统跑逻辑 → 组件标记脏 → 框架按复制策略同步给客户端
客户端: 收到组件数据 → 更新本地实体 → 表现系统读组件驱动 GameObject
```

Mod 自定义协议退居二线，只管"事件/动作"这类非状态消息。logic/view 拆分在 ECS 里是"实体+组件数据"与"表现系统读组件"的天然分工。

### 动态网络对象（确定性 assetId）

```csharp
// "com.mymod:fire_mage" → 双端必然一致的 GUID
public static Guid ContentIdToAssetId(NamespacedId id)
{
    using var md5 = MD5.Create();
    return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(id.ToString())));
}
```

Mod 内容一律运行时 Spawn，禁用场景内嵌 NetworkIdentity。

## 10.8 分工总表

| 内容 | 承载方式 |
|---|---|
| 对象生成/销毁 | Mirror 原生 Spawn（确定性 assetId） |
| 位置/朝向同步 | NetworkTransform 或框架快照组件 |
| 框架级组件状态 | SyncVar（Framework 程序集内，正常织入） |
| Mod 状态同步 | 组件复制（ECS） |
| Mod 语义消息/RPC | ModMsgBatch 信封 |
| 握手/认证/协商 | 框架级 Mirror 消息（固定类型，固定注册顺序） |

---

# 第 11 章 安全与隔离层

## 11.1 代码隔离

1. **程序集物理分层**：shared / server / client 分离，server 程序集禁止引用渲染 API（编译期强制）
2. **异常隔离**：Mod 处理器抛异常 → catch → 该 Mod 降级状态（后续消息丢弃并告警），绝不拖垮 Host
3. **多版本程序集隔离**：v2 用 ALC（AssemblyLoadContext）隔离不同版本实例

## 11.2 资源配额

- 入站：每连接 × 每 modNetId 令牌桶（msgRate / byteRate 可配）
- 出站：每 Mod 带宽统计，超阈值告警
- 单消息 payload 上限（默认 8KB）
- 违规：警告 → 断连，全程日志指向具体 Mod

## 11.3 权限声明

带外网络（Mod 连自己的服务器）走权限声明制：

```json
{ "permissions": ["net.external"], "externalHosts": ["api.mymod.com"] }
```

安装/进房时向玩家明示。一期只提供 HTTP(S) 客户端，不开原始 socket。

## 11.4 Relay 防滥用（自建必做）

1. Allocate 要凭证（平台会话票据 / 账号系统），匿名接口会被薅成公共代理
2. Bind 前零状态：校验 token 前不分配内存
3. 包长上限（1400B），超限丢弃
4. 每房间带宽配额，超限踢房
5. 载荷不透明 → Relay 无法审计内容，anti-cheat 留在服务端权威逻辑

---

# 第 12 章 附录

## 12.1 术语表

| 术语 | 含义 |
|---|---|
| Mod | 自包含、带版本、带清单的分发包 |
| Core Mod | 游戏本体 Mod（`com.game.core`） |
| Framework | 承载 Mod 运行的引擎层 |
| modId | 反向域名唯一标识 |
| .mod 包 | 不可变的 Mod 分发产物 |
| 版本实例 | (modId, version) 构成的运行单元 |
| 依赖绑定 | Mod 加载时把依赖解析到具体版本实例 |
| Registry | 内容注册表（命名空间 ID → 定义） |
| World | ECS 全局运行时服务 |
| Entity / Component / System | ECS 三要素 |
| 消息总线 | Mod 间通信的唯一契约 |
| 协议复用层 | Mod 协议收敛到固定 Mirror 信封消息 |
| Relay | 自建 UDP 哑转发中转服务 |
| 组件复制 | ECS 状态同步机制 |

## 12.2 关键决策记录

| # | 决策 | 理由 | 影响 |
|---|---|---|---|
| 1 | Mod 包仅 manifest + 文件 + 文件清单 | 最小契约 | 全链 |
| 2 | modId 反向域名 | 无中央注册、命名空间根 | 内容/消息/资源统一 |
| 3 | 轻量托管 ECS（非 DOTS） | 外部 DLL 可定义组件/系统 | 逻辑组织 |
| 4 | Mod 间通信 = 消息 | 松耦合、优雅降级、网络透明 | 通信 |
| 5 | 多版本运行时共存（v2） | 解决依赖版本冲突 | Registry/存档/网络/程序集 |
| 6 | Mirror + 服务端权威 | 状态同步简单可靠 | 网络 |
| 7 | 自建 Relay（哑转发 + KCP 端到端） | 可控、解耦、可扩展 | Host 模式 NAT |
| 8 | Mod 协议收敛到固定信封 | Mirror 消息 ID 顺序依赖 + Weaver 范围 | 自定义协议 |
| 9 | Source Generator 替代 Weaver | 外部 DLL 序列化 | 序列化 |

## 12.3 开放问题

1. **代码共存运行时方向**：CoreCLR（Unity 6+）/ Mono + 版本化程序集名 / 先只做内容共存——需拍板
2. **游戏类型**：沙盒 / 模拟经营 / RPG / 卡牌——影响 Registry 与同步细节
3. **联机形态**：好友合作 2~4 人（P2P）还是专用服务器为主
4. **Mod 面向**：仅团队内部解耦，还是开放玩家社区（决定沙箱/SDK/审核力度）
5. **消息中间件**（Mod 拦截其他 Mod 消息）：一期不开放，观察社区需求

## 12.4 实现路线图

```
P0  契约定稿: manifest/FileEntry/VersionRange/命名空间 类型定义
P1  最小 ECS World 原型 + ModLoader 五阶段
P2  ModMsgBatch 信封 + 消息总线 + Source Generator 原型
P3  Relay 协议包 + 节点原型 (双机联调)
P4  构建工具 modbuild + 存储/installed.json
P5  存档系统 (组件快照 + 绑定快照)
P6  SDK (源工程模板 + reference assemblies)
```
