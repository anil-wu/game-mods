# 框架实现评估与架构完善记录

> 对照 `Unity_Mod_Runtime_架构设计V2.0.md`（§0–§19 + Rule 1–20）。
> 本文档记录实现现状评估、已完成的架构完善工作、以及后续路线（HybridCLR 已推迟）。

---

## 1. 总体结论

**核心 Mod Runtime 架构（V2.0 的心脏）高度保真落地，Rule 1–20 全部满足。**
最难、最体现架构价值的部分——资源边界、卸载强制回收、能力隔离、无泛型 ABI、二进制通信——均已实现并有测试实证（**159/159 全绿** + Unity Play 冒烟通过）。

**网络分层补齐已完成**（§11.6 版本协商 / §11.8 兴趣管理 / §11.11 Relay Provider / §11.12 加密 / §11.13 流量配额，
见 §3C）：五个网络子系统全部落地，跨端（relay_server ↔ Network.Mod）线格式经双实现互通测试锁定（Rule 19）。

剩余欠账集中在 **UGC 工业化工具链**（Source Generator）与 Replication 增量/量化，属于"广度与工业化"层面，
而非架构正确性层面；随 HybridCLR / UGC 平台化一并推进。

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

## 3B. 契约测试向量机制（§14.11.3，已完成）

### 背景

Rule 19（契约=文档，零程序集共享）下，消费方各自**重复定义**解析器——带来解析发散风险
（owner 改字节布局，消费方手写解析器静默失效）。§14.11.3 要求 owner **随版本发布测试向量**作为补偿。

### 改动

- 框架新增 `Game.Mod.Contract.Wire.TestVector`（样例字段 + 规范编码字节 + `DecodeFields`）。
- owner Mod 发布向量：`com.fps.player.PlayerTestVectors`（position_row）、`com.fps.npc.NpcTestVectors`（npc_row）。
- 消费方 CI 校验：`tests/TestRunner/TestVectorTests`——
  - `weapon.PlayerSnapshot` 与 `npc.PlayerSnap` **两个独立消费方**对同一字节解出一致结果（字段级跳读 `[4]=Yaw` 也一致）；
  - `weapon.NpcSnapshot` 解析 npc owner 字节；
  - **漂移负向用例**：owner 违反 append-only 改布局 → 消费方旧解析器检出。

### 验证

- 无头测试 **113/113 全绿**（110 + 3 个测试向量测试）；Unity 编译零错误。
- 第一方在共享测试程序集跑；UGC 场景向量应序列化进包（样例 + 字节 hex）供消费方离线校验（后续）。

---

## 3C. 网络分层补齐（§11.6 / §11.8 / §11.11 / §11.12 / §11.13，已完成）

### 背景

架构文档 §11 中五个尚未落地的网络子系统，按实现设计 `network-layering-impl.md` 分三次提交落地。
核心库最小 ABI 加法（`ClientReady` 事件 / `RegisterInterest` / `RegisterLegacyCodec` / `ConfigureBudget` /
`NetworkBudget` / `NetworkStats` additive 字段），业务 Mod 对外 ABI（`INetworkContext`）**零变化**（§7.3 承诺全部守住）。

### 提交

| 提交 | 内容 |
|---|---|
| `f862831` | docs(network)：网络分层补齐实现设计（五子系统文件/线格式/测试方案） |
| `7b8b650` | feat(network)：协议版本协商（§11.6）+ 兴趣管理（§11.8） |
| `bf239a2` | feat(network)：加密（§11.12）+ 流量统计/配额/限流（§11.13） |
| `3e237e1` | feat(network)：Relay Provider（§11.11）+ relay_server 对接 + 全链路 @ Relay |

### 各层实现要点

1. **版本协商（§11.6）**——`Negotiation/`：hello/result 两条握手协议（字段编号线格式，NetworkRuntimeImpl `Start` 自我注册）；
   每连接状态机（Pending→Negotiated/Rejected）+ 交集计算 + required 判定（缺必需 Mod → reason=2 + 缺失清单；
   核心协议无交集 → reason=1 整体拒连；表现层无交集 → 降级禁用）；**废弃窗口硬规则**：`RegisterLegacyCodec`
   只保留"当前 + 上一个版本"，协商到 legacy 版本时服务器用旧 codec 编解码、帧头写协商版本；后注册协议默认接受
   （保既有 fixture 全绿的关键决策）；未协商连接上的业务帧 → `DroppedNotNegotiated` 防御。
2. **兴趣管理（§11.8）**——`Interest/`：`IInterestManager`（非泛型 `bool IsRelevant(int, ProtocolId, in object)`）；
   Broadcast 候选连接 = 已协商 ∧ 未禁用 ∧ 兴趣放行，**过滤在 Encode 之前**（省带宽）；默认无 manager = 全房间广播
   （现状语义零变化）；`SendToClient` 单播不过兴趣；owner 校验 + `UnregisterAll` 清理（Rule 20）。
3. **加密（§11.12）**——`Security/`：`INetworkSecurityProvider`（算法可替换）+ 默认 `AesHmacSecurityProvider`
   （AES-CBC + HMAC-SHA256 截断 16B，encrypt-then-MAC，仅 netstandard2.1 表面 API，零第三方依赖）；`SecurityLayer`
   信封 `[Magic][Flags][KeyId][Seq u64][CipherLen][密文][Tag]`——**明文帧（含 ProtocolId）整体进加密，Relay 只见信封**；
   每方向滑动窗口防重放（256 位 bitmap，容忍乱序丢包）；**密钥只存在于 SecurityLayer**：引导 `Enable(psk)` 后经
   `HMAC-SHA256(psk, "conn:"||connId||role)` 派生进 provider，`INetworkContext` 无任何密钥成员（§11.12 硬要求）；
   未 Enable = 明文兼容模式（显式启用，无隐式行为变化）。
4. **流量统计/配额/限流（§11.13）**——`Traffic/`：Connection/Mod/Protocol 三级 1s 窗口统计 + `NetworkStats`
   新字段（additive）；`NetworkBudget`（maxMessageSize/maxPacketRate/maxBandwidth/maxConnectionBandwidth/
   throttleSuspendMs，按 Mod 配置，null=恢复默认）；发送侧速率/带宽预算 + 服务器侧连接级收包预算
   （恶意/失控客户端防线）；窗口内违规 ≥3 → 挂起 ThrottleSuspendMs；**统计无条件记录、超限才执行**；
   框架基础设施（com.game.network 握手/复制协议）免配额；Mod 级逻辑/传输两档（加密前后）。
5. **Relay Provider（§11.11）**——`Relay/`：`RelayClientTransport`（INetworkTransport 实现：BindHost/BindClient、
   本地客户端内存回环 connId=1、后台泵线程 + 测试 PumpOnce 同步驱动、Ping/Pong 心跳、Stop 发 Leave）；
   `RelayWire` 线格式**客户端侧独立实现**（Rule 19 可重复定义，不引用 relay_server 程序集，报文 ≤1400B，
   Type 含新增 Leave=11）；`RelayAllocationClient` 控制面 HTTP（/allocate /resolve /health，管道分隔文本零依赖解析）；
   `RelaySessions`（CreateSession 房间码 / JoinSession）+ `AutoPathResolver`（先 Direct 失败回退 Relay，§11.3）；
   relay_server 小改（Leave、HandleLeave、PeerLeft 通知、RemoveClient/IsFull、connId 从 2 起、管道分隔响应）。

### 验证

- 无头测试 **159/159 全绿**（113 既有 + 新增 46：Negotiation 10 / Interest 8 / Security 7 / Traffic 6 / Relay 13 /
  LayeredPipeline 2——全链路 @ Loopback 与 @ Relay 各一遍，五层组合不破坏彼此）。
- `dotnet build relay_server/RelayServer.sln` 0 警告 0 错误；`bash runtime/verify-unity.sh` 编译 + Play 冒烟通过
  （boot Mods 经新框架重建后加载正常）。
- 架构合规复核（QA `design/qa-network-layering.md`）：Rule 5（业务 Mod 零直接网络依赖）/ 13（无泛型 ABI）/
  14（二进制）/ 15（Role×Path 正交）/ 16（xxHash32 稳定哈希）/ 17（Network.Mod pinned 禁热卸载）/
  19（RelayWire 文档契约 + 双实现互通测试）全部守住；密钥仅存 Network.Mod 内（INetworkContext 反射检查无密钥成员）。
- 遗留观察项（非阻断，见 QA 报告）：混合版本客户端同房间时 Broadcast 帧按首个候选连接版本编码（设计 §1.2 明示折衷）；
  Relay 心跳路径未入测试（生产后台泵原型期，PumpOnce 模式不启用心跳）。

---

## 4. 剩余缺口与路线（按价值排序，HybridCLR 已推迟）

| 优先级 | 事项 | 说明 |
|---|---|---|
| ~~看需求~~ | ~~网络分层补齐（§11.6 / §11.8 / §11.11 / §11.12 / §11.13）~~ | **已完成**（见 §3C）：协商/兴趣/加密/流量/Relay 五子系统落地，159/159 全绿 + Unity 冒烟通过 |
| 看需求 | **Replication 增量/量化**（§11.10 ChangedOnly / 定点化） | 实体数 < 几十时全量快照够用 |
| 推迟 | **Source Generator**（§14.3/§15.3 生成 Codec/包装） | HybridCLR AOT 泛型安全主动机；当前手写 Codec 够用，随 HybridCLR 一并做 |
| 推迟 | **HybridCLR 接入** | 现为字节加载 + 同副本重入（§11.14.1）；用户明确推迟 |
| 推迟 | Dependency Features（§12.5）/ 资源三级 Scope（§6）/ 消息版本订阅（§14.6） | UGC 工业化，随平台化一并做 |
| ~~推迟~~ | ~~契约测试向量（§14.11.3）~~ | **已完成**（见 §3B） |

> **说明**：无泛型 ABI（Rule 13）与二进制通信（Rule 14）已就位——这正是 HybridCLR 的两块地基
> （AOT 泛型安全 + 引用泄漏物理防护）。后续接入 HybridCLR 时，ABI 层无需改造，只需处理
> 程序集加载/卸载与泛型补充元数据（§15.2）。
