# 框架实现评估与架构完善记录

> 对照 `Unity_Mod_Runtime_架构设计V2.0.md`（§0–§19 + Rule 1–20）。
> 本文档记录实现现状评估、已完成的架构完善工作、以及后续路线（HybridCLR 已推迟）。

---

## 1. 总体结论

**核心 Mod Runtime 架构（V2.0 的心脏）高度保真落地，Rule 1–20 全部满足。**
最难、最体现架构价值的部分——资源边界、卸载强制回收、能力隔离、无泛型 ABI、二进制通信——均已实现并有测试实证（110/110 全绿 + Unity Play 冒烟通过）。

剩余欠账集中在**网络高级特性**与 **UGC 工业化工具链**（Source Generator），属于"广度与工业化"层面，而非架构正确性层面；随 HybridCLR / UGC 平台化一并推进。

---

## 2. Rule 合规状态（20 条）

| Rule | 内容 | 状态 |
|---|---|---|
| 1 | 一个 Mod 只有一个 IMod（Register/Unregister） | ✅ |
| 2 | Client/Server/Host 是 Context 能力 | ✅ |
| 3 | Mod 自己拥有自己的资源 | ✅ |
| 4 | Mod 自己定义自己的 Network Protocol | ✅ |
| 5 | 所有网络通信经过 Network Mod | ✅ |
| 6 | 通知走 MessageBus；有返回走 ModCall | ✅ |
| 7 | 依赖由 Manifest 声明 + Loader 统一解析 | ✅ |
| 8 | 依赖支持 Client/Server Scope | ✅ |
| 9 | 窗口经 UI.Mod WindowManager | ✅ |
| 10 | 消息经 MessageBus，卸载强制退订 | ✅ |
| 11 | 消息谁定义谁发布，载荷只读纯数据 | ✅ |
| 12 | Mod 完全隔离，四条 Core 中介通道 | ✅ |
| 13 | 无泛型 ABI | ✅ |
| 14 | **跨 Mod 通信全部二进制** | ✅（**本次补全 ModCall**） |
| 15 | 网络 Role×Path 正交 | ✅ |
| 16 | 全局协议 ID 稳定哈希 | ✅ |
| 17 | 联网会话禁热卸载协议 Mod | ✅ |
| 18 | 一个 Mod = Shared/Client/Server 三模块 | ✅ |
| 19 | 契约 = 文档，字节流唯一事实来源 | ✅ |
| 20 | 注册即归属，卸载强制回收 | ✅ |

---

## 3. 本次架构完善：Rule 14 ModCall 二进制化（已完成）

### 背景

ModCall（跨 Mod 能力调用，§12.11）此前用 `Delegate + object + DynamicInvoke`（进程内活对象传递），是 Rule 14 唯一未落实物理保证的核心规则——bytes 不可能藏对象引用，但 object[] 里可以。用户明确要求："字节必须用于 Mod 间数据访问、泛型不能随便用，这是后续 HybridCLR 的基础。"

### 改动

**框架（runtime/src）：**

- `PayloadWriter/Reader`：补 `WriteDouble` / `TryReadDouble`（Fixed64）。
- 新增 `Game.Mod.Contract.Wire.DataCodec`：通用纯数据（`object?[]`）↔ 二进制编解码。
  - 承载：null / bool / int / uint / long / ulong / float / double / string / byte[] / 嵌套 object?[]（string[] 经数组协变）。
  - 自描述（field 1 = kinds 字节表，field 2..N+1 = 值，嵌套递归）；不支持的类型 fail-fast。
  - 定位：简单能力与动态路由（console 命令转发）的便利路径；高频/强类型能力仍可手写字段 Codec（§14.12）。
- `CapabilityHandler` 委托：`PayloadBuffer CapabilityHandler(PayloadReader args)`（二进制入 → 二进制出）。
- `IModManager.Export/Call/InvokeRegistered` 全部改为二进制签名；`ModManager` 路由字节（保留依赖校验 / NoMod / NoCapability / Trace），与消息总线同一套读写原语（Rule 14 一套 Codec）。

**消费方迁移（全部经 DataCodec 边界编解码）：**

- `com.game.modstore`（list / install_and_start）
- `com.fps.player`（5 能力）/ `weapon`（2）/ `npc`（2）/ `game`（1）/ `console`（register + InvokeRegistered 动态路由）
- 测试（`ModCall.Invoke/InvokeBool` 测试辅助）

### 效果

- **Rule 14 物理保证对 ModCall 成立**：Core 只在调用方与导出方之间路由字节，无法藏对象引用——热卸载边界由机制保证（§14.5）。
- **无泛型 ABI 收紧**（Rule 13）：ModCall 全程无泛型实例化，为 HybridCLR AOT 泛型安全打地基。
- 进程内消息、ModCall、网络协议统一一套线格式（字段编号 + 可跳读 + append-only，§14.11）。

### 验证

- 无头测试 **110/110 全绿**（加载真实 Mod 程序集，走二进制 ModCall 路径）。
- Unity 编译零错误；**Play 冒烟通过**（boot mods 经新框架重建后加载正常）。

---

## 4. 剩余缺口与路线（按价值排序，HybridCLR 已推迟）

| 优先级 | 事项 | 说明 |
|---|---|---|
| 看需求 | **网络分层补齐**（§11.6 版本协商 / §11.8 兴趣管理 / §11.9 完整管线 / §11.11 Relay / §11.12 加密） | 仅当要真·互联网联机才需要；单机/局域网 Loopback+Mirror 已够 |
| 看需求 | **Replication 增量/量化**（§11.10 ChangedOnly / 定点化） | 实体数 < 几十时全量快照够用 |
| 推迟 | **Source Generator**（§14.3/§15.3 生成 Codec/包装） | HybridCLR AOT 泛型安全主动机；当前手写 Codec 够用，随 HybridCLR 一并做 |
| 推迟 | **HybridCLR 接入** | 现为字节加载 + 同副本重入（§11.14.1）；用户明确推迟 |
| 推迟 | Dependency Features（§12.5）/ 资源三级 Scope（§6）/ 消息版本订阅（§14.6） | UGC 工业化，随平台化一并做 |

> **说明**：无泛型 ABI（Rule 13）与二进制通信（Rule 14）已就位——这正是 HybridCLR 的两块地基
> （AOT 泛型安全 + 引用泄漏物理防护）。后续接入 HybridCLR 时，ABI 层无需改造，只需处理
> 程序集加载/卸载与泛型补充元数据（§15.2）。
