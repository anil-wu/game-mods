# QA 报告：网络分层补齐集成验证

> 验证对象：`design/network-layering-impl.md` 五子系统实现（版本协商 §11.6 / 兴趣管理 §11.8 /
> 加密 §11.12 / 流量配额 §11.13 / Relay Provider §11.11），已合入提交 `7b8b650` / `bf239a2` / `3e237e1`（分支 `feature/mod-runtime-v2`）。
> 验证日期：2026-08-30。QA 角色：只验证与报告，不改动被验证代码（除补测试外）。

---

## 1. 验收点清单与逐项结论

| # | 验收点 | 结论 | 证据 |
|---|---|---|---|
| 1 | `bash tests/run.sh` 全绿 | ✅ **159/159** | 见 §2.1（基线 157 + QA 补测 2 全过） |
| 2 | `bash runtime/verify-unity.sh` 通过（编译 + Play 冒烟） | ✅ | 编译通过；Play 冒烟通过 |
| 3 | `dotnet build relay_server/RelayServer.sln` 通过 | ✅ | 0 警告 0 错误 |
| 4 | 版本协商测试覆盖：交集 / 拒连 / 废弃窗口 | ✅ | NegotiationTests 10 项全过，见 §3.1 |
| 5 | 兴趣管理测试覆盖：过滤 / 不过滤 | ✅ | InterestTests 8 项全过，见 §3.2 |
| 6 | 加密测试覆盖：往返 / 密钥不泄露 / 防重放 | ✅ | SecurityTests 7 项全过，见 §3.3 |
| 7 | 流量配额测试覆盖：超限 Throttle / 三级统计 | ✅ | TrafficTests 6 项全过，见 §3.4 |
| 8 | Relay 测试覆盖：房间码 / 转发 / Auto 回退 / 双实现互通 | ✅ | RelayTests 13 项全过，见 §3.5 |
| 9 | 全链路组合测试：Loopback 与 Relay 各一遍 | ✅ | Relay 版（既有）+ **Loopback 版（QA 补 2 项）**，见 §3.6 |
| 10 | 架构合规 Rule 5/13/14/15/16/17/19 | ✅ | 见 §4 |
| 11 | 密钥仅存于 Network.Mod 内，INetworkContext 无密钥成员 | ✅ | 见 §4.8 |
| 12 | 核心库零第三方依赖 | ✅ | 见 §4.9 |
| 13 | 既有业务协议对外 ABI 零变化（§7.3 承诺） | ✅ | 见 §4.10 |

---

## 2. 构建与冒烟

### 2.1 无头测试（`bash tests/run.sh`）

```
=== ALL PASS (159) ===
```

- 基线：任务书声明"当前 157 项"——实测 157/157 全绿 ✓（144 既有零破坏 + 13 项新增 Relay）。
- QA 补测后：**159/159 全绿**（`LayeredPipelineTests` 2 项：Loopback 全链路五层组合 + required 拒连）。
- 覆盖 46 项网络分层新增测试：Negotiation 10 / Interest 8 / Security 7 / Traffic 6 / Relay 13 / LayeredPipeline 2。
- 既有 113 项（契约/ModCall/测试向量/FPS/Replication/UI/网络基础）零破坏——证明"默认开协商"设计
  （Loopback 同步握手 + 后注册协议默认接受）不打断既有 fixture。

### 2.2 Unity 验证（`bash runtime/verify-unity.sh`）

```
>>> 1. 编译检查
   编译通过
>>> 2. Play 模式冒烟测试（加载 Mods）
   Play 冒烟通过
```

前提确认：执行前 Unity 编辑器本体未运行（仅 Hub/Licensing 进程），无并发构建，无 MSBuild 文件锁。

### 2.3 relay_server 构建

```
RelayServer -> .../RelayServer.dll
已成功生成。 0 个警告 0 个错误
```

### 2.4 资产入库核对

`git ls-tree 3e237e1` 确认：4 个框架 DLL（`runtime/Unity/Assets/Plugins/Game.*.dll`）+ 4 个 boot Mod DLL
（`runtime/Unity/Assets/StreamingAssets/mods/*/*.dll`）均已随提交入库；`com.game.network.dll` 46592→58880B（含 Relay 层）。

---

## 3. 各层测试覆盖审查

### 3.1 版本协商（§11.6）——NegotiationTests 10 项 ✅

| 覆盖点 | 测试 | 判定 |
|---|---|---|
| 交集非空 → 共同最高版本 | `Handshake_AcceptsAll_WhenVersionsMatch`（双向 + accepted 版本=1） | ✅ |
| 核心协议无交集 → 整体拒连 reason=1 | `Handshake_Rejects_CoreProtocolNoIntersection`（客户端 Send 抛 NetworkException） | ✅ |
| 表现层无交集 → 降级禁用，连接继续 | `Handshake_DisablesOptionalProtocol`（发抛异常 / 收 drop / DroppedNotNegotiated++） | ✅ |
| 缺必需 Mod → 拒连 reason=2 + 缺失清单 | `Handshake_MissingRequiredMod_Rejects` | ✅ |
| **废弃窗口**：legacy codec（当前+上一版） | `Negotiation_UsesLegacyCodec`（v2 legacy 线格式 v*10 往返 + 帧头版本=协商版本 2 断言 + DroppedInvalid=0） | ✅ |
| legacy 注册 owner 校验 | `RegisterLegacyCodec_WrongOwner_Rejected`（非 owner / 未注册协议均拒绝） | ✅ |
| 未协商连接业务帧 → drop 防御 | `BusinessFrame_BeforeNegotiation_Dropped`（发送抛 + 接收 DroppedNotNegotiated++） | ✅ |
| 测试向量（§14.11.3） | `Handshake_TestVectors_Decode` + `Handshake_Protocols_RoundTrip_ThroughWire` | ✅ |
| 后注册协议默认接受（真实 Mod 加载） | `PostStartRegisteredProtocol_Accepted`（ModManager 加载 com.game.network + Loopback） | ✅ |

**缺口判定**：无。交集/拒连/降级/废弃窗口/后注册/未协商防御全链路覆盖。

### 3.2 兴趣管理（§11.8）——InterestTests 8 项 ✅

| 覆盖点 | 测试 | 判定 |
|---|---|---|
| 默认无 manager = 全发 | `Broadcast_NoInterest_DeliversAll`（双客户端各 1 帧） | ✅ |
| 逐连接过滤 | `Broadcast_InterestFiltersByConnection`（被过滤连接零消息 + 帧数断言） | ✅ |
| **过滤在 Encode 前** | `Interest_EvaluatedBeforeEncode`（Evaluations=候选连接数，被过滤不产帧） | ✅ |
| owner 校验 / 未注册协议拒绝 | `RegisterInterest_WrongOwner_Rejected` + `RegisterInterest_UnknownProtocol_Rejected` | ✅ |
| null = 清除回默认 | `RegisterInterest_Null_ClearsToDefault` | ✅ |
| UnregisterAll 清理（Rule 20） | `UnregisterAll_ClearsInterest`（重注册后无残留兴趣） | ✅ |
| 显式单播不过兴趣 | `SendToClient_IgnoresInterest` | ✅ |

**缺口判定**：无。

### 3.3 加密（§11.12）——SecurityTests 7 项 ✅

| 覆盖点 | 测试 | 判定 |
|---|---|---|
| 加密往返（跨运行时，握手+业务全密文） | `Encrypt_Decrypt_RoundTrip`（DroppedAuth/DroppedReplay=0） | ✅ |
| **密文隐藏 ProtocolId（Relay 不可见）** | `Ciphertext_HidesProtocolId`（信封结构 + 密文中无明文 PID/载荷字节序列） | ✅ |
| 篡改丢弃 | `TamperedCiphertext_Dropped`（翻转 1 字节 → DroppedAuth++，Handler 不触发） | ✅ |
| 错钥拒收 | `WrongKey_Dropped`（hello 认证失败 → 永不协商 → Send 抛异常） | ✅ |
| 防重放 + 乱序容忍 | `Replay_Seq_Dropped`（重放拒绝 + 窗口内乱序接受 + 重复拒绝） | ✅ |
| **密钥不泄露** | `KeyNotExposed_ToModApi`（反射检查 INetworkContext 无 key/secret/crypto/security 成员；密钥只经 SetupSession 进 provider、32B 派生非原始 psk） | ✅ |
| 加密 + 配额共存 | `EncryptionAndTraffic_Coexist_RoundTrip`（加密前后两档统计 + 超限 Throttle） | ✅ |

**缺口判定**：无。

### 3.4 流量配额（§11.13）——TrafficTests 6 项 ✅

| 覆盖点 | 测试 | 判定 |
|---|---|---|
| Mod 发送速率 + 挂起窗口 Throttle | `ModSendRate_Throttled`（伪时钟：预算内→超限→违规≥3 挂起→窗口重置恢复） | ✅ |
| Mod 带宽预算 | `ModBandwidth_Throttled`（大载荷连发超带宽限流） | ✅ |
| 连接级收包预算（服务器防御） | `ConnectionReceiveBudget_Enforced`（超 MaxConnectionBandwidth → DroppedThrottle） | ✅ |
| 三级统计 | `ThreeLevelStats_Recorded`（Connection/Mod/Protocol + 既有业务口径不破坏） | ✅ |
| null 恢复默认 | `ConfigureBudget_ResetToDefault` | ✅ |
| UnregisterAll 清理（Rule 20） | `UnregisterAll_ClearsBudget` | ✅ |

**缺口判定**：无。

### 3.5 Relay Provider（§11.11）——RelayTests 13 项 ✅

| 覆盖点 | 测试 | 判定 |
|---|---|---|
| **双实现互通（Rule 19）** | `RelayWire_Packet_Interop` / `RelayWire_Bind_Interop` / `RelayWire_Vectors_Decode`（客户端 RelayWire ↔ 服务器 RelayServer.Protocol 同字节解析一致 + 非法报文双方一致拒绝 + 测试向量字段级校验） | ✅ |
| Bind + C2S/S2C 转发 | `Relay_HostBind_ClientBind_DataRoundTrip`（connId 保持客户端自身 id） | ✅ |
| 广播 | `Relay_Broadcast_ToAllClients`（2 客户端 + Host 本地回环全收到） | ✅ |
| connId 映射 | `Relay_ConnId_Mapping`（远端从 2 起顺序分配，Host 侧 = 各客户端 id） | ✅ |
| Leave 退房通知 Host | `Relay_Leave_NotifiesHost`（PeerLeft → 连接表收缩） | ✅ |
| BindFail（无主 / 错 token） | `Relay_BindFail_NoHost_Or_WrongToken` | ✅ |
| **房间码**（控制面→线格式→节点校验全链路） | `Relay_Allocation_Code_Flow`（token 与 roomId 不匹配拒绝） | ✅ |
| **Session 模型** CreateSession/JoinSession | `Relay_CreateSession_JoinSession_RoundTrip`（假控制面 HTTP + 无效房间码失败） | ✅ |
| **Auto 回退（§11.3）** | `Relay_Auto_UsesDirect_WhenAvailable` + `Relay_Auto_FallsBackToRelay`（探测失败 → Session + Relay 转发，业务无感知） | ✅ |
| **全链路 @ Relay** | `Relay_FullPipeline_OverRelay`（协商+加密+兴趣+流量+三级统计 @ Relay 组合） | ✅ |

**缺口判定**：无。心跳（Ping/Pong）路径未入测试——生产后台泵为原型期，PumpOnce 模式不启用心跳，见 §5 观察项 O3。

### 3.6 全链路组合测试（实现设计 §9.6：Loopback 与 Relay 各一遍）

- **@ Relay**：`Relay_FullPipeline_OverRelay`（既有）✅。
- **@ Loopback**：**QA 补充** `LayeredPipelineTests` 2 项：
  1. `FullPipeline_Loopback_AllLayers_Coexist`——真实 Network.Mod（ModManager 加载）+ Loopback：协商（Start 内握手 +
     后注册默认接受）→ 加密（psk 密文往返零丢弃）→ 兴趣（默认全发→过滤→恢复）→ 流量（预算内送达 + 第 6 条 Throttle）→
     三级统计 + 加密两档 + 业务口径（握手帧不计统计、发送统计含限流、接收统计只含送达）。
  2. `FullPipeline_Loopback_RequiredRejects`——required 判定在真实加载 + 回环拓扑下 reason=2 + 缺失清单 + 被拒发送抛异常。

  > 开发中发现并修正一次测试自身断言错误（`MaxPacketRate` 窗口未计先前广播/往返与"统计无条件记录"语义），
  > 非实现缺陷；修正后 159/159 全绿。

---

## 4. 架构合规检查

| Rule | 检查内容 | 证据 | 结论 |
|---|---|---|---|
| **Rule 5** 唯一网络出入口 | 业务 Mod 无任何直接网络依赖 | `grep -rln "Mirror\|UdpClient\|TcpClient\|Socket" mods/com.game.modstore/src/ mods/com.fps*/src/` → 空；业务 Mod 仅经 `context.Network`（INetworkContext） | ✅ |
| **Rule 13** 无泛型 ABI | 新增接口全部非泛型 | `INetworkContext` / `INetworkRuntime` / `INetworkTransport` / `IInterestManager` / `INetworkSecurityProvider` 均无泛型方法/参数；核心库 Network.cs 无 `<T` | ✅ |
| **Rule 14** 二进制 | 跨 Mod 通信一律二进制线格式 | 握手 hello/result、Relay 报文、加密信封全部字段编号/定长二进制；`PayloadWriter/Reader` 编解码；无对象引用跨边界 | ✅ |
| **Rule 15** Role×Path 正交 | 两个独立枚举 | `NetworkRole { Client, Host, Service }` 与 `NetworkPath { Auto, Direct, Relay }` 分离（§11.3）；AutoPathResolver 先 Direct 失败回退 Relay；Host 本地客户端走回环完整管线（connId=1，Relay 远端从 2 起防冲突） | ✅ |
| **Rule 16** 稳定哈希 | 全局协议 ID = xxHash32 | `ProtocolId.Of(mod, path) = xxHash32("{mod}:{path}")`（runtime/src/Game.Mod.Contract/Ids.cs）；握手协议 ID 同规则；`ProtocolId_Stable_And_OrderIndependent` 测试回归 | ✅ |
| **Rule 17** 会话禁热卸载协议 Mod | Network.Mod 禁热卸载 | `com.game.network/mod.json` `"pinned": true`；ModManager 对 pinned Mod 的 Unload 抛 `ModStateException`；协议按 OwnerMod 一次性清理（Rule 20） | ✅ |
| **Rule 19** RelayWire 文档契约 | 契约 = 文档，双方独立实现 | `mods/com.game.network/CONTRACT.md` 发布 Relay 线格式契约（报文头/Type/Bind 载荷/ConnId 约定/控制面）；客户端 `RelayWire` 独立实现不引用 relay_server 程序集；双实现互通由 RelayTests 3 项锁定（同字节解析一致 + 非法报文一致拒绝 + 测试向量字段级校验） | ✅ |
| **§11.12** 密钥隔离 | 密钥仅存 Network.Mod 内 | `INetworkContext` 反射检查无 key/secret/crypto/security 成员；psk 仅 `SecurityLayer.Enable(psk)` 进入，经 HMAC-SHA256 派生 32B 后 `SetupSession` 注入 provider（provider 拿不到原始 psk）；测试 `KeyNotExposed_ToModApi` 实证 | ✅ |
| **§11.1** 硬边界 | Network.Mod 不拥有 Gameplay Protocol | NetworkRuntimeImpl 只认识 ProtocolId/Version/Encode/Decode/Handler/OwnerMod；兴趣钩子由业务 Mod 自实现 | ✅ |
| 零第三方依赖 | 核心库与 Network.Mod | 全部 csproj 无 `<PackageReference>`（自研 JSON/Payload 原语 + `System.Security.Cryptography`） | ✅ |
| §7.3 ABI 不变承诺 | `INetworkContext`/协议接口/12B 帧头/既有协议零改动 | 核心库仅 additive（NetworkStats 新字段、INetworkRuntime 新成员、INetworkTransport +ClientReady）；既有 113 项测试零破坏证明 | ✅ |

---

## 5. 缺陷与观察项清单（按严重程度）

### 无阻断性缺陷（blocking defect: 无）

### 观察项（非阻断，建议后续处理）

| # | 级别 | 描述 | 复现/依据 | 建议 |
|---|---|---|---|---|
| O1 | 中 | **混合版本客户端同房间时 Broadcast 丢帧**：`NetworkRuntimeImpl.Broadcast` 帧只按 `candidates[0]` 的协商版本编码一次；若客户端 A 协商 v3、客户端 B 协商 v2（服务器注册 v3 + legacy v2），B 收到帧头版本 v3 ≠ 期望 v2 → `DroppedInvalid`。协商只保证"单连接交集"，不保证"房间内版本一致" | 代码路径：Broadcast → `SelectSendCodec(candidates[0], ...)` → 逐连接 `SendFromServerTo`；B 侧 `Dispatch` 版本校验失败。设计 §1.2 注释已明示此折衷（"多连接版本不一致由协商保证收敛到共同版本"），非本次回归 | 升级过渡期建议：Broadcast 前按协商版本分组编码（每版本一帧）；或文档明示"同房间客户端须同版本"。列入 §4 路线 |
| O2 | 低 | `SecurityLayer.Decrypt` 对自定义 provider 的 `Decrypt` 调用无外层 try-catch（默认 `AesHmacSecurityProvider` 内部已捕获 `CryptographicException`）；未来接入第三方 Provider（如 ECDH/TLS 化）若抛非预期异常，接收管线会冒泡崩溃 | 代码审查：OnServerFrame → DecryptC2S → provider.Decrypt 无兜底 | 建议在 SecurityLayer.Decrypt 外层加 try-catch → DroppedAuth 计数（纵深防御，成本一行） |
| O3 | 低 | Relay 心跳（Ping/Pong）路径未入测试；生产后台泵线程为原型期（marshal 留引导期），测试统一 `PumpOnce` 同步驱动 | RelayTests 13 项无心跳用例；`RelayClientTransport` 心跳逻辑存在但仅在 `BackgroundPump=true` 路径 | 联机接入期补心跳 + 闲置清扫端到端测试 |
| O4 | 低 | `LayeredPipelineTests` 依赖 `NetworkMod.Current` 单例（真实 Mod 加载），与既有 `PostStartRegisteredProtocol_Accepted` 同型；若未来并行测试需隔离 | 测试结构 | 保持现状即可（现有框架顺序执行） |

### 测试期间发现的非缺陷（记录）

- `FullPipeline_Loopback_AllLayers_Coexist` 首跑失败（`MessagesSent` 期望 5 实际 6）：**测试自身断言错误**——
  `RecordSendLogical` 对被 Throttle 的消息也计数（§5.4"统计无条件记录"），发送统计含限流、接收统计只含送达。
  修正断言（6/5）后通过；实现行为符合设计，非缺陷。

---

## 6. 补测内容（本 QA 交付）

| 文件 | 内容 |
|---|---|
| `tests/TestRunner/LayeredPipelineTests.cs`（新增） | Loopback 全链路五层组合（协商+加密+兴趣+流量+三级统计）+ required 拒连 2 项；补实现设计 §9.6 的 Loopback 半边 |

---

## 7. 总体结论

**✅ 通过（有条件通过中的"条件"仅为观察项，均不阻断）**

- 全部 13 项验收点通过；`tests/run.sh` 159/159 全绿；`verify-unity.sh` 编译 + Play 冒烟通过；relay_server 构建 0 错误。
- 五个子系统实现与实现设计 `network-layering-impl.md` 逐条吻合；测试覆盖审查无实质缺口（补 Loopback 全链路 2 项后
  Loopback/Relay 两端各一遍齐全）。
- 架构合规 Rule 5/13/14/15/16/17/19 + §11.12 密钥隔离 + 零第三方依赖 + §7.3 ABI 不变承诺全部守住。
- 缺陷清单：**无阻断性缺陷**；4 项观察项（O1 为设计明示折衷，O2/O3 为纵深防御与测试覆盖建议），已建议后续处理。
