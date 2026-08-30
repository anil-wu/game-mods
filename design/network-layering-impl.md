# 网络分层补齐实现设计（§11.6 / §11.8 / §11.11 / §11.12 / §11.13）

> 本文件是 **实现设计**：把权威架构文档 `Unity_Mod_Runtime_架构设计V2.0.md` §11 中尚未落地的五个网络子系统，翻译成本仓库的具体文件、接口、线格式与测试方案，供编码 agent 逐任务实现。
>
> 依据：评估文档 `implementation-assessment.md` §4 路线表第一项"网络分层补齐（看需求）"。本任务不重抄 §11，只写"改哪个文件、加什么类、字节怎么排、测什么"。
>
> 约束（任务书）：遵循 Rule 5/13/14/15/16/17；**不改变现有业务协议的对外 ABI**；核心库保持零第三方依赖（netstandard2.1 + net8.0 双目标）。

---

## 0. 现状盘点

### 0.1 已就位（本期不动）

| 能力 | 位置 | 说明 |
|---|---|---|
| 协议注册表 / 方向校验 / 归属校验 / 12B 帧头 | `mods/com.game.network/src/NetworkRuntimeImpl.cs` | `[ProtocolId u32][Ver u16][Flags u16][Len u32]`（§11.5） |
| 版本废弃窗口（单当前版本） | 同上 `Dispatch` | 帧版本 ≠ 注册版本 → `DroppedInvalid` |
| Loopback（Host 回环，§11.3 硬规则） | `LoopbackTransport.cs` | 内存队列，同步事件 |
| ECS Replication（§11.10） | `ReplicationRuntime.cs` | 全量快照 @15Hz，不属本期 |
| 三级统计雏形 | `NetworkRuntimeImpl.CountSend` + `NetworkStats` | Mod 级字节 / Protocol 级消息数 |
| Relay 服务端（盲转发 + 房间码 + token） | `relay_server/src/RelayServer/` | UDP 节点 + HTTP 分配（/allocate /resolve）+ HMAC token |
| 稳定哈希 ProtocolId / PayloadWriter / PayloadReader | `runtime/src/Game.Mod.Contract/` | Rule 16 / Rule 14 线格式原语 |

### 0.2 缺口（本期五个子系统）

1. **§11.6 协议版本协商**：无握手协议、无交集计算、无 required/optional 分级、无 legacy codec（"当前 + 上一个版本"硬规则）。
2. **§11.8 兴趣管理**：`Broadcast` 无脑 `SendFromServerAll`，无 relevance 过滤钩子。
3. **§11.12 加密**：明文裸帧，无 `INetworkSecurityProvider`，密钥无处安放（必须留在 Network.Mod 内）。
4. **§11.13 流量/配额/限流**：只有累计计数器，无窗口限流、无预算、无 Throttle。
5. **§11.11 Relay Provider**：服务端已就绪，**客户端 `RelayClientTransport` 缺失**，`NetworkPath.Relay` 只是枚举值。

### 0.3 现状约束（决定设计形态的硬事实）

- TestRunner 直接把 `mods/com.game.network/src/**/*.cs` **编译进测试程序集**（`tests/TestRunner/TestRunner.csproj`），无 UnityEngine 引用 → **新代码必须保持 UnityEngine-free、netstandard2.1 可编译**。
- 所有 FPS/Replication 测试 fixture 的流程是：`Load(NetworkMod) → AttachTransport(Loopback) → Start(Host)`，**之后再加载业务 Mod（协议在 Start 之后才注册）**。任何"协商必须在 Start 时完成且只认当时快照"的设计都会打断既有 113 项测试——协商必须容忍**后注册协议**。
- `build-mod.py` 用 `<Compile Include="src/**/*.cs">` 通配编译 → **新增子目录无需改构建脚本**。
- 核心库 `runtime/src` 只被 Network.Mod 和框架内部使用；`INetworkContext`（业务 Mod 所见）是唯一业务 ABI，**不允许动**；`INetworkTransport`/`INetworkRuntime` 只有 Network.Mod 一个实现者，允许最小加法（见 §7）。

---

## 1. 总体管线架构

### 1.1 设计原则

**NetworkRuntimeImpl 只做编排**：生命周期（Start/Stop/AttachTransport）、公开 API 路由（注册/发送/统计）、Dispatch 顶层控制流。五层各自独立类文件、独立状态、可独立测试，互不依赖对方实现细节：

```
mods/com.game.network/src/
├── NetworkMod.cs                 装配器：new 各层 → 注入 Runtime
├── NetworkRuntimeImpl.cs         编排器（瘦身：注册表/统计代码迁出）
├── ProtocolRegistry.cs           注册表 + legacy codec（从 Impl 抽出）
├── FrameCodec.cs                 12B 帧头编解码 + MaxSize 校验（从 Impl 抽出）
├── LoopbackTransport.cs          （不变）
├── ReplicationRuntime.cs         （不变）
├── Negotiation/
│   ├── NegotiationLayer.cs       握手状态机 + 交集计算 + required 判定
│   ├── HandshakeProtocol.cs      hello/result 两条协议 + 编解码
│   └── HandshakeVectors.cs       测试向量（§14.11.3 模式）
├── Interest/
│   └── InterestManager.cs        IInterestManager 定义 + 注册表 + 过滤
├── Security/
│   ├── INetworkSecurityProvider.cs
│   ├── SecurityLayer.cs          信封封装/解析 + 序号防重放
│   └── AesHmacSecurityProvider.cs 默认实现（零第三方依赖）
├── Traffic/
│   ├── TrafficLayer.cs           配额执行 + Throttle
│   ├── TrafficMonitor.cs         三级窗口统计
│   └── NetworkBudget.cs          预算数据结构
└── Relay/
    ├── RelayClientTransport.cs   INetworkTransport 的 Relay 实现
    ├── RelayWire.cs              客户端侧独立实现的 relay 线格式（Rule 19）
    └── RelayAllocationClient.cs  HTTP 控制面（/allocate /resolve）
```

### 1.2 发送管线（全部路径统一）

```
SendToServer / SendToClient / Broadcast(owner, pid, msg)
  │
  ▼ ① ProtocolRegistry.ValidateSend(owner, pid, direction)   —— 归属/方向/未启动检查（现有逻辑迁出）
  ▼ ② NegotiationLayer.Validate(connection, pid)             —— 已协商且未被 disabled？握手帧放行
  ▼ ③ InterestManager.FilterConnections(Broadcast)           —— 在 Encode 之前过滤（省带宽）
  ▼ ④ FrameCodec.Encode(entry, msg)                          —— 12B 头 + payload；MaxSize 校验（§11.12 UGC 防护）
  ▼ ⑤ TrafficLayer.CheckSend(owner, pid, len)                —— 窗口限流/带宽预算，超限 Throttle
  ▼ ⑥ SecurityLayer.Encrypt(connection, frame)               —— 未配置密钥则直通（明文兼容模式）
  ▼ ⑦ Transport.SendFromClient / SendFromServerTo(connId, frame)
```

`Broadcast` 不再调用 `SendFromServerAll`：候选连接 = `transport.ServerConnections` 中（已协商 ∧ 未 disabled ∧ Interest 放行）的子集，逐连接走 ④–⑦（帧只 Encode 一次，逐连接只多一次加密+发送）。

### 1.3 接收管线

```
Transport.ReceivedOnClient / ReceivedOnServer(connId, wire)
  │
  ▼ ⑥' SecurityLayer.Decrypt(wire)            —— 信封解析 + 认证，失败 drop（DroppedAuth/Replay）
  ▼ ⑤' TrafficLayer.AccountReceive(connId, len) —— 连接级收包预算，超限 drop
  ▼ ②' NegotiationLayer.Route(connId, frame)   —— 握手帧 → 状态机；业务帧未协商 → drop（NotNegotiated）
  ▼ ①' ProtocolRegistry.Validate(connId, frame)—— owner/方向/协商版本/codec 选择（registered 或 legacy）
  ▼ FrameCodec.Decode → Handler
```

### 1.4 与 Loopback / Mirror / Relay 的关系

- 五层全部位于 **transport 之上**，对底层传输零感知。`INetworkTransport` 是三者的共同抽象：**Loopback**（Host 回环，现役）、**Mirror**（Direct Provider 候选，接入时实现同一接口即可，不在本期）、**RelayClientTransport**（本期新增，Path=Relay）。
- `NetworkPath` 仍是声明性配置（现状即如此：传输由 `AttachTransport` 装配）。本期新增 `RelayClientTransport` 后，引导代码按 `config.Path == Relay` 装配它——与现有 Mirror/Loopback 装配方式一致，不改 `NetworkConfig` 语义。
- Host 的本地客户端（Loopback connId=1）**同样走完整管线**：协商、加密（若启用）、流量、兴趣——§11.3 硬规则保持。

---

## 2. 层一：协议版本协商（§11.6）

### 2.1 握手协议（Network.Mod 自有基础设施协议）

两条协议，路径与 ID 遵循 Rule 16（`ProtocolId.Of(ModId, path)` = xxHash32）：

| 协议 | 路径 | Direction | Version | MaxSize |
|---|---|---|---|---|
| hello | `handshake:hello` | ClientToServer | 1 | 4096 |
| result | `handshake:result` | ServerToClient | 1 | 4096 |

> 用两条协议而不是"一条双向协议"，是因为现有 `INetworkProtocol.Direction` 单值 + `Dispatch` 按方向校验的机制不用为此开洞。

**hello 载荷（字段编号线格式，Rule 14 同族）**：

```
field 1 (bytes): 客户端协议表 entries[]
  每项 = ProtocolId u32 LE(4) + Version u16 LE(2)
        + ModIdLen u8 + ModId utf8 + PathLen u8 + Path utf8
```

**result 载荷**：

```
field 1 (u8):    accept（1=接受，0=拒绝）
field 2 (u32):   reason（0=ok / 1=core_no_intersection / 2=missing_required_mod / 3=other）
field 3 (bytes): accepted[]，每项 = ProtocolId u32 + Version u16（协商后版本）
field 4 (bytes): disabled[]，每项 = ProtocolId u32
field 5 (string): 缺失必需 Mod 清单（ModId 逗号分隔；accept=0 时携带）
```

字段编号线格式保证 hello/result 可跳读、append-only 演化；两协议的 Encode/Decode 用 `PayloadWriter`/`PayloadReader` 实现，实现类放在 `HandshakeProtocol.cs`（`HandshakeHelloProtocol : INetworkProtocol`、`HandshakeResultProtocol : INetworkProtocol`），由 NetworkRuntimeImpl 在 `Start` 时**自我注册**（不依赖 NetworkMod 装配顺序，纯 NetworkRuntimeImpl 实例也能握手——测试直接 new 的 runtime 同样可用）。

### 2.2 协商状态机（每连接）

```
Pending（连接首次出现）
  → 客户端 ClientReady 后发 hello
  → 服务器收到 hello：计算交集与 required 判定 → 回 result
  → 客户端收到 result：
       accept=1 → Negotiated（保存 accepted 版本表 + disabled 集合）
       accept=0 → Rejected（记录 reason；客户端后续 Send 抛 NetworkException 带原因）
```

- 连接生命周期数据 `ConnectionState { bool Negotiated; bool Rejected; uint RejectReason; string Missing; Dictionary<ProtocolId,ushort> Versions; HashSet<ProtocolId> Disabled; }` 存于 NegotiationLayer（key = connectionId；Loopback 服务器侧恒为 `LocalConnectionId=1`；**客户端侧维护单一连接状态**，key 用常量 `0` 表示“到服务器的连接”）。
- 服务器侧的 required 策略：`runtime.Negotiation.RequiredMods: string[]`（引导/测试装配，如 `["com.fps.player"]`）。

### 2.3 交集计算（服务器判定）

对服务器已注册的每个**非握手**协议 P（owner = M，注册版本 = Vr，legacy 版本集合 = L）：

1. 客户端 hello 中有 P（版本 Vc）：
   - `Vc ∈ {Vr} ∪ L` → **共同最高版本 = Vc**，写入 accepted。
   - 否则无交集。
2. 无交集时：
   - `M ∈ RequiredMods`（核心玩法协议）→ **整体拒绝**，reason=1。
   - 否则（表现层协议）→ 写入 disabled（降级忽略，连接继续）。
3. 对每个 required Mod M：客户端 hello 中没有任何 ModId==M 的条目 → **整体拒绝**，reason=2，missing 清单含 M。
4. 客户端声明的服务器不认识的协议 → 忽略（客户端侧按"不在 accepted 即 disabled"自行禁用）。

### 2.4 协商结果应用

- 客户端：`accepted` 中的版本 == 客户端自身注册版本（共同版本取客户端声明值），**无需改码**；`disabled` 集合 → 该连接上发送抛 `NetworkException`、接收 drop 计数。
- 服务器：`Versions[P] = Vc` 覆盖注册版本 → `Dispatch` 版本校验改为对照**协商版本**；编解码器选择：`Vc == Vr` 用注册 codec，否则查 `ProtocolEntry.LegacyCodecs[Vc]`。
- **后注册协议（协商完成后才注册）**：默认立即接受（版本 = 注册版本）。依据：会话内协议 Mod 禁热卸载（§11.14）且双端同构加载（现状所有测试 fixture 都是 Start 后才加载业务 Mod）；异常场景由方向/版本校验兜底。这是让既有 113 项测试在默认开协商下保持全绿的关键决策。
- 未协商连接上的业务帧：drop + `DroppedNotNegotiated`（防御：握手完成前业务流量不可注入）。

### 2.5 Legacy codec（§11.6 废弃窗口硬规则）

```csharp
// INetworkRuntime 新增（唯一实现者 NetworkRuntimeImpl；业务 Mod 不受影响）
void RegisterLegacyCodec(ModId owner, ProtocolId id, ushort version, INetworkProtocol codec);
```

- owner 校验：只有协议 owner 可注册其旧版本 codec。
- 存储：`ProtocolEntry.LegacyCodecs: Dictionary<ushort, INetworkProtocol>`。
- `UnregisterAll(owner)` 一并清理（Rule 20）。
- 本期语义：注册版本即"当前版本"，旧版本**可选**注册（大多数协议只有单版本，无需注册）。

### 2.6 测试向量

`HandshakeVectors.cs` 用 `Game.Mod.Contract.Wire.TestVector` 发布 hello/result 的样例字节（§14.11.3 模式），供调试与未来跨端校验；网络 Mod 是框架基础设施，向量主要价值在回归锁定线格式。

---

## 3. 层二：兴趣管理（§11.8）

### 3.1 接口（非泛型，Rule 13）

```csharp
public interface IInterestManager
{
    /// <summary>Server 侧 Broadcast 过滤：连接 connId 是否应收到 pid 协议这条消息。</summary>
    bool IsRelevant(int connectionId, ProtocolId protocolId, in object message);
}
```

- `message` 是广播 Mod 自己的业务对象，**进程内同线程**回调（与 `INetworkHandler` 同一注册-回调模式，非跨 Mod 传委托/引用——Rule 12 不触线）。
- 注册 API（INetworkRuntime 新增）：`void RegisterInterest(ModId owner, ProtocolId pid, IInterestManager? manager);`——owner 校验，`null` 表示清除回默认（全发）。
- `UnregisterAll(owner)` 清理（Rule 20）。

### 3.2 Broadcast 过滤流程（§1.2 步骤③）

1. `ValidateSend` 通过后，取 `transport.ServerConnections`。
2. 依次过滤：`NegotiationLayer.Validate`（已协商 ∧ 未 disabled）→ `interest?.IsRelevant(connId, pid, msg)`（未注册 interest → 放行）。
3. 过滤**在 Encode 之前**：被过滤连接不产生任何编解码/加密开销。
4. 对幸存连接集合 Encode 一次帧，逐连接走流量/加密/发送。

### 3.3 边界

- 兴趣只作用于 **Server → Client Broadcast**；`SendToClient`（显式单播）不过兴趣（语义上调用方已选定目标）；`SendToServer` 天然单目标。
- 实体级兴趣（NetworkId/区域）由各游戏 Mod 在自己的 `IInterestManager` 里实现（内部查 ECS/自身状态），Network.Mod 只提供钩子与执行点，不拥有任何游戏概念（§11.1 硬边界）。
- 默认无 manager = 全房间广播（现状语义，零行为变化）。

---

## 4. 层三：加密（§11.12）

### 4.1 INetworkSecurityProvider（非泛型，纯字节）

```csharp
public interface INetworkSecurityProvider
{
    /// <summary>算法名（诊断/日志）。</summary>
    string Name { get; }

    /// <summary>每帧安全开销（安全头 + 认证标签 + 填充），用于配额与预算。</summary>
    int Overhead { get; }

    /// <summary>装入会话密钥（仅 Network.Mod 内部调用；Mod 永远拿不到 Key）。</summary>
    void SetupSession(byte[] sessionKey, ushort keyId);

    /// <summary>加密整帧（含 12B 协议头）→ 密文（IV + ciphertext）。</summary>
    byte[] Encrypt(byte[] frame);

    /// <summary>解密 + 认证；失败返回 null（调用方 drop 计数）。</summary>
    byte[]? Decrypt(byte[] ciphertext);
}
```

### 4.2 默认实现 `AesHmacSecurityProvider`

- 算法：**AES-CBC + HMAC-SHA256（截断 16B），encrypt-then-MAC**；密文 = `IV(16B) || AES-CBC(明文, PKCS7)`；标签覆盖 `KeyId||Seq||CipherLen||密文`。
- 只用 `System.Security.Cryptography`（`Aes.Create` / `HMACSHA256` / `RandomNumberGenerator`），**netstandard2.1 与 net8.0 双目标可用、零第三方依赖**（不用 `AesGcm`——不在 netstandard2.1 表面）。
- `Overhead = 16(IV) + 16(Tag) + 16(padding 上界)` = 48。

### 4.3 SecurityLayer（信封 + 防重放）

```
Transport 载荷（每连接、每方向）：
[Magic u16 = 0x4E54]["NT"][Flags u8][KeyId u16][Seq u64][CipherLen u32][Ciphertext][Tag 16B]
```

- **明文帧 = 现有 12B 帧头（含 ProtocolId） + payload → 整个进加密**：`ProtocolId / ProtocolVersion / 业务数据` 全部在密文内，Relay 只见信封，满足 §11.12 "Relay 不可见"。
- 连接密钥派生：`sessionKey = HMAC-SHA256(psk, "conn:" || connId u32 || role u8)`（截 32B），`keyId` 由 SecurityLayer 分配（本期恒 1，预留给轮换）。**psk 只存在于 Network.Mod 内部**（来自引导配置），任何 Mod API 都不暴露密钥。
- 防重放（原型档）：每方向单调 `Seq` + 滑动窗口（256 位 bitmap）：`seq > floor` 且 `(seq - floor) < 256` 且未见过 → 接受并置位，推进 floor 到最低未置位；否则 `DroppedReplay++`。容忍乱序丢包（Relay/UDP 场景），拒绝重放。
- 启用方式：`runtime.Security.Enable(psk)`（未调用 = 直通明文模式，现有测试零影响）。
- 握手协议同样走加密（配置了密钥则全量加密，包括 hello/result）。

### 4.4 密钥对 Mod 不可见（§11.12 硬要求）

- 密钥仅由 `SecurityLayer` 持有：引导代码把 psk 交给 `runtime.Security.Enable(psk)`，之后密钥经派生进入 provider；业务 Mod 的 API 面（`INetworkContext`）没有任何密钥相关成员。
- 密钥分发（原型）：**PSK**（引导/配置文件，如房间密码、游戏密钥）。ECDH/TLS 化作为后续 Provider 实现，`INetworkSecurityProvider` 接口已预留（`SetupSession` 一次装入，密钥交换不涉及接口变化）。

---

## 5. 层四：流量统计 / 配额 / 限流（§11.13）

### 5.1 数据结构

```csharp
public sealed class NetworkBudget
{
    public int MaxMessageSize = 65536;        // Mod 级单消息硬顶（协议 MaxSize 之外的 Mod 自设上限）
    public int MaxPacketRate = 100;           // 每秒消息数（该 Mod 全部连接合计）
    public int MaxBandwidth = 65536;          // 每秒字节数（逻辑字节，该 Mod 合计）
    public int MaxConnectionBandwidth = 16384;// 单连接每秒字节数（Server 收包防御）
    public int ThrottleSuspendMs = 100;       // 超限后挂起窗口
}
```

### 5.2 API（INetworkRuntime 新增）

```csharp
void ConfigureBudget(ModId owner, NetworkBudget? budget);   // null = 恢复默认
```

- 预算按 Mod 配置（自声明，Rule 20 归属 + UnregisterAll 清理）。`mod.json` 的 `networkBudget` 声明式读取列为后续（Core Loader → Network.Mod），本期 API + 默认值即可实现 §11.13 语义。

### 5.3 三级统计（TrafficMonitor，滑动 1 秒窗口）

| 级别 | 维度 | 计数 |
|---|---|---|
| Connection | connId | 收/发字节、收/发速率 |
| Mod | owner | 字节、消息数、速率（逻辑字节 + 加密后传输字节两档） |
| Protocol | pid | 字节、消息数 |

- `NetworkStats` **加字段（additive，不删改既有字段）**：`BytesByConnection: Dictionary<int,long>`、`BytesByProtocol: Dictionary<ProtocolId,long>`、`DroppedThrottle`、`DroppedNotNegotiated`、`DroppedAuth`、`DroppedReplay`。

### 5.4 执行点与 Throttle 语义

- **发送（客户端/服务器双侧）**：`TrafficLayer.CheckSend(owner, pid, len)`——窗口内速率/带宽超预算 → drop 本条消息、`DroppedThrottle++`、Mod 违规计数 +1；窗口内违规 ≥3 → 该 Mod 挂起 `ThrottleSuspendMs`（期间一切发送直接 drop 计数）。这是 §11.13 对"`Update() → Send()` 60×N 洪水"的防护。
- **接收（服务器侧为主）**：`TrafficLayer.AccountReceive(connId, len)`——单连接收包字节超 `MaxConnectionBandwidth` → drop + 计数（恶意/失控客户端拖垮服务器的防线）。
- 统计在 `AccountReceive` 无条件记录（可观测性不因限流丢失）；执行只发生在超限时。

---

## 6. 层五：Relay Provider（§11.11）

### 6.1 RelayClientTransport（`INetworkTransport` 实现）

```csharp
public sealed class RelayClientTransport : INetworkTransport
{
    public sealed class Config
    {
        public string RelayHost = "";      // relay_server 地址
        public int RelayPort = 7777;
        public ulong RoomId;               // 会话号
        public byte[] Token = null!;       // 绑定凭据（/allocate 或 /resolve 签发）
        public bool IsHost;                // true → BindHost；false → BindClient
        public TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    }
}
```

- `StartServer`（Host 侧）：发 `BindHost` → 等 `BindAck`；`StartClient`（Client 侧）：发 `BindClient` → 等 `BindAck`（自己的 connId 在 ack 报头里）。
- `SendFromClient(frame)`：封装 `DataRaw` 报文（connId = 自身 connId）发送。
- `SendFromServerTo(connId, frame)`：`DataRaw`，报文 connId = 目标客户端（Relay 按此定向转发，见 `UdpRelayNode.HandleData`）。
- `SendFromServerAll`：遍历 `ServerConnections` 逐连接发送。
- 接收：`DataRaw` → Host 侧 `ReceivedOnServer(packet.ConnId, frame)`（connId = 来源客户端，Relay 保留客户端自身 id）、Client 侧 `ReceivedOnClient(frame)`；`PeerJoined` → 加入 `ServerConnections`；`PeerLeft` → 移除。
- 心跳 `Ping/Pong`；`Stop` 发 `Leave` 退房。
- **线程模型**：生产 = 后台泵线程（`UdpClient.ReceiveAsync` 循环 + 事件直调；跨线程 marshal 留给 Unity 引导接入期，本期不做）；**测试 = `PumpOnce()` 同步驱动**（处理当前全部可达 datagram，确定性无 flake）。
- 客户端角色 `BindAck` 成功后触发 `ClientReady`（见 §7 核心库唯一改动）→ NetworkRuntimeImpl 发起握手。

### 6.2 RelayWire.cs（Rule 19：契约 = 文档，双方各自实现）

`relay_server` 是 relay 线格式的 **owner**，Network.Mod 是**消费方**——按"可重复定义"模式在 Mod 内独立实现解析器（约 120 行），**不引用 relay_server 程序集**：

```
报文：  [Magic u16 LE = 0x4D52]["MR"][Ver u8=1][Type u8][ConnId u16 LE] + payload（≤1400B）
Bind：  roomId u64 LE + role u8 + expiry u64 ms LE + token 16B = 33B
类型：  BindHost=1 BindClient=2 BindAck=3 BindFail=4 PeerJoined=5 PeerLeft=6
        DataKcp=7 DataRaw=8 Ping=9 Pong=10 Leave=11（新增）
```

提供：`Packet.Encode/TryDecode`、`BindRequest` 编码、`BindAck/BindFail/PeerJoined/PeerLeft/Ping/Pong` 解析。两端互通性由 RelayTests 在共享测试程序集里证明（两端源码都编进 TestRunner）。

### 6.3 RelayAllocationClient（控制面）

- `HttpClient` POST `/allocate`（Host 建房间 → `joinCode + token`）、`/resolve`（Client 凭房间码 → `token`）。
- 响应格式：**管道分隔文本**（`joinCode|roomId|relayHost:port|tokenB64|expiryMs`）——客户端零依赖解析（netstandard2.1 无 System.Text.Json），人类可读。relay_server 的 `Program.cs` 同步调整（JSON 仅保留在 /health）。
- 测试不依赖 HTTP：直接注入 `AllocationService.Allocate()/TryResolve()` 产物构造 `Config`。

### 6.4 relay_server 改动（小）

| 文件 | 改动 |
|---|---|
| `src/RelayServer/Protocol/MessageType.cs` | `+ Leave = 11` |
| `src/RelayServer/Relay/UdpRelayNode.cs` | `HandleLeave`（移除 peer + 通知 Host `PeerLeft`）；客户端 idle 清扫 → `PeerLeft` 通知 Host |
| `src/RelayServer/Relay/Room.cs` | `+ RemoveClient(ushort connId)`；`Host` 判空容错 |
| `src/RelayServer/Program.cs` | `/allocate /resolve` 响应改管道分隔文本 |
| `Allocation/JoinCode.cs`、`Allocation/AllocationService.cs`、`Protocol/TokenCodec.cs` | 不变 |

### 6.5 Session 模型（房间码）

Host：`CreateSession()` = `/allocate` → 得房间码（如 `ABCD-1234`）→ 对外公布。Client：`JoinSession(code)` = `/resolve` → 得 token → `BindClient` → 握手。Relay 节点视角只有一个 Session 下的连接集合，不认识任何游戏概念（§11.11）。

---

## 7. 接口与 ABI 影响汇总

### 7.1 新增接口（全部非泛型、纯二进制，Rule 13/14）

| 接口/类型 | 位置 | 说明 |
|---|---|---|
| `IInterestManager` | Network.Mod `Interest/` | `bool IsRelevant(int, ProtocolId, in object)` |
| `INetworkSecurityProvider` | Network.Mod `Security/` | 见 §4.1 |
| `HandshakeHelloProtocol` / `HandshakeResultProtocol` | Network.Mod `Negotiation/` | 握手协议（字段编号线格式） |
| `NetworkBudget` / `TrafficMonitor` / `TrafficLayer` | Network.Mod `Traffic/` | 见 §5 |
| `RelayClientTransport` / `RelayWire` / `RelayAllocationClient` | Network.Mod `Relay/` | 见 §6 |

### 7.2 核心库改动（唯一且最小）

`runtime/src/Game.Mod.Runtime/Abstractions/Network.cs`：

1. `INetworkTransport` **增加一个事件**：`event System.Action? ClientReady;`——客户端角色传输就绪（Loopback 在 `StartClient` 成功后同步触发；Relay 在 `BindAck` 后触发）。NetworkRuntimeImpl 订阅 → 发起握手。
2. `INetworkRuntime` **增加成员**（唯一实现者 NetworkRuntimeImpl，业务 Mod 走 `INetworkContext` 不受影响）：
   - `void RegisterInterest(ModId owner, ProtocolId pid, IInterestManager? manager);`
   - `void RegisterLegacyCodec(ModId owner, ProtocolId id, ushort version, INetworkProtocol codec);`
   - `void ConfigureBudget(ModId owner, NetworkBudget? budget);`
   - 层访问器：`NegotiationLayer Negotiation { get; }`、`SecurityLayer Security { get; }`（供引导装配 required mods / psk）。
3. `NetworkStats` 加字段（additive，见 §5.3）。

> 因此需要：`bash runtime/build-unity-dlls.sh` 重建框架 DLL + `python runtime/build-mod.py` 重建 Mod + `bash runtime/verify-unity.sh` 回归。

### 7.3 明确不动的 ABI（对外不变承诺）

- `INetworkContext`（业务 Mod 唯一网络入口）、`INetworkProtocol/Handler/Writer/Reader`、`NetworkDirection`、12B 帧头格式、复制/销毁协议、`LoopbackTransport` 行为、`NetworkStats` 既有字段——**全部不变**。
- 既有业务协议（com.fps.*、com.sample.pushbox）零改动。

---

## 8. 逐文件改动计划

### 8.1 mods/com.game.network/src/（新增/修改）

| 文件 | 动作 | 内容 |
|---|---|---|
| `NetworkMod.cs` | 修改 | `Register` 装配 Registry/Negotiation/Security/Traffic 到 runtime；注册握手协议；日志补各层状态 |
| `NetworkRuntimeImpl.cs` | 修改 | **瘦身为编排器**：注册表→ProtocolRegistry、帧编解码→FrameCodec、统计→TrafficMonitor；发送/接收管线按 §1.2/§1.3 编排；订阅 `ClientReady`；自我注册握手协议 |
| `ProtocolRegistry.cs` | 新增 | ProtocolEntry（+LegacyCodecs）、注册/冲突检测/owner 清理/方向与归属校验/协商版本校验与 codec 选择 |
| `FrameCodec.cs` | 新增 | 12B 帧头读写、MaxSize 校验（从 Impl 迁出） |
| `Negotiation/NegotiationLayer.cs` | 新增 | ConnectionState、握手状态机、hello 发送/result 处理、交集计算、required 判定、`Validate`/`Route` 钩子、后注册协议默认接受 |
| `Negotiation/HandshakeProtocol.cs` | 新增 | 两条握手协议 + hello/result 编解码 |
| `Negotiation/HandshakeVectors.cs` | 新增 | TestVector 样例 |
| `Interest/InterestManager.cs` | 新增 | IInterestManager、按 (owner,pid) 注册表、FilterConnections |
| `Security/INetworkSecurityProvider.cs` | 新增 | 接口（§4.1） |
| `Security/SecurityLayer.cs` | 新增 | 信封封装/解析、连接密钥派生、滑动窗口防重放、`Enable(psk)` |
| `Security/AesHmacSecurityProvider.cs` | 新增 | 默认实现（§4.2） |
| `Traffic/TrafficLayer.cs` | 新增 | CheckSend/AccountReceive、预算执行、Throttle 挂起 |
| `Traffic/TrafficMonitor.cs` | 新增 | 三级窗口统计 + NetworkStats 填充 |
| `Traffic/NetworkBudget.cs` | 新增 | 预算结构 |
| `Relay/RelayClientTransport.cs` | 新增 | INetworkTransport 实现（§6.1） |
| `Relay/RelayWire.cs` | 新增 | relay 线格式客户端解析（§6.2） |
| `Relay/RelayAllocationClient.cs` | 新增 | HTTP 控制面（§6.3） |
| `LoopbackTransport.cs` / `ReplicationRuntime.cs` | 不变 | — |

### 8.2 relay_server/src/

见 §6.4（4 个文件小改，其余不变）。

### 8.3 tests/TestRunner/

| 文件 | 动作 | 内容 |
|---|---|---|
| `TestRunner.csproj` | 修改 | `<Compile Include="../../relay_server/src/RelayServer/**/*.cs" Exclude="Program.cs;Properties/**" />`（两端线格式同程序集互通测试；UdpRelayNode/Allocation 零依赖可独立编译） |
| `NegotiationTests.cs` | 新增 | 层一测试（§9.1） |
| `InterestTests.cs` | 新增 | 层二测试（§9.2） |
| `SecurityTests.cs` | 新增 | 层三测试（§9.3） |
| `TrafficTests.cs` | 新增 | 层四测试（§9.4） |
| `RelayTests.cs` | 新增 | 层五测试（§9.5） |
| `LayeredPipelineTests.cs` | 新增 | 全链路集成（协商+加密+兴趣+流量 @ Loopback 与 @ Relay 各一遍） |
| 既有 `NetworkTests/ReplicationTests/Fps*` | 不变 | 必须保持全绿（默认开协商由同步 Loopback 握手 + 后注册接受保证） |

---

## 9. 测试策略（无头测试，tests/run.sh 自动发现）

### 9.1 NegotiationTests（层一）

1. `Handshake_AcceptsAll_WhenVersionsMatch`——Host 回环，同版本全接受，业务帧正常往返。
2. `Handshake_Rejects_CoreProtocolNoIntersection`——required Mod 协议版本不匹配 → result accept=0、reason=1，客户端 Send 抛 `NetworkException`。
3. `Handshake_DisablesOptionalProtocol`——可选协议无交集 → accept=1、该协议进入 disabled；发它抛异常、收它 drop 计数。
4. `Handshake_MissingRequiredMod_Rejects`——客户端 hello 缺 required Mod 全部协议 → reason=2 + missing 清单正确。
5. `Negotiation_UsesLegacyCodec`——服务器注册 v3 + legacy v2 codec，客户端声明 v2 → 协商版本 2，服务器用 legacy codec 编解码，双向帧版本 2。
6. `BusinessFrame_BeforeNegotiation_Dropped`——未协商连接直接注入业务帧 → `DroppedNotNegotiated++` 且不触发 Handler。
7. `Handshake_TestVectors_Decode`——`HandshakeVectors` 样例字节被 `PayloadReader` 解出预期字段（§14.11.3）。
8. `PostStartRegisteredProtocol_Accepted`——Start 后注册的协议无需重握手即可收发（回归既有 fixture 行为）。

### 9.2 InterestTests（层二）

1. `Broadcast_NoInterest_DeliversAll`——默认全发（现状语义回归）。
2. `Broadcast_InterestFiltersByConnection`——manager 按 connId 过滤，被过滤连接零消息、Handler 零触发。
3. `Interest_EvaluatedBeforeEncode`——（实现细节断言：被过滤连接不增加任何编解码计数）。
4. `RegisterInterest_WrongOwner_Rejected`——owner 校验。
5. `UnregisterAll_ClearsInterest`——Rule 20。
6. `SendToClient_IgnoresInterest`——显式单播不过滤。

### 9.3 SecurityTests（层三）

1. `Encrypt_Decrypt_RoundTrip`——启用 psk 后业务帧往返（Loopback）。
2. `Ciphertext_HidesProtocolId`——密文中**不含**明文帧头（ProtocolId 字节序列在密文内不可见）——直接断言 Relay 视角只能看到信封（§11.12）。
3. `TamperedCiphertext_Dropped`——翻转密文 1 字节 → `Decrypt` null → `DroppedAuth++`、Handler 不触发。
4. `WrongKey_Dropped`——两端不同 psk → 握手帧/业务帧全部认证失败。
5. `Replay_Seq_Dropped`——重放同一 Seq 报文 → `DroppedReplay++`；乱序窗口内报文正常。
6. `KeyNotExposed_ToModApi`——`INetworkContext` 无密钥成员（编译期保证）+ 密钥仅存于 SecurityLayer（单元级断言 provider 只经 SetupSession 获得密钥）。

### 9.4 TrafficTests（层四）

1. `ModSendRate_Throttled`——低于预算正常；超出 → drop + `DroppedThrottle++`；连续违规 → 挂起窗口内全部发送被 drop。
2. `ModBandwidth_Throttled`——大载荷连发超带宽 → 限流。
3. `ConnectionReceiveBudget_Enforced`——服务器侧单连接收包超预算 → drop。
4. `ThreeLevelStats_Recorded`——Connection/Mod/Protocol 三级计数与 `NetworkStats` 新字段一致。
5. `ConfigureBudget_ResetToDefault`——null 恢复默认。
6. `UnregisterAll_ClearsBudget`——Rule 20。

### 9.5 RelayTests（层五，进程内 UDP 节点）

1. `Relay_HostBind_ClientBind_DataRoundTrip`——`UdpRelayNode`（临时端口）+ `RelayClientTransport` × 2：C2S/S2C 数据经 Relay 完整往返。
2. `Relay_Broadcast_ToAllClients`——Host `SendFromServerAll` 到达全部客户端（多客户端场景）。
3. `Relay_ConnId_Mapping`——Host 侧 `ReceivedOnServer` 的 connId = 各客户端分配 id。
4. `Relay_Leave_NotifiesHost`——客户端 `Stop`（Leave）→ Host 收到 `PeerLeft`、`ServerConnections` 收缩。
5. `Relay_Allocation_Code_Flow`——`AllocationService` 签发 → `RelayWire` 编码 Bind 报文 → 节点校验通过（两端线格式互通性实证）。
6. `RelayWire_Packet_Interop`——`RelayWire.Packet` 与 `RelayServer.Protocol.Packet` 对同一字节解析一致（双实现无漂移）。

### 9.6 LayeredPipelineTests（全链路）

Loopback 与 Relay 各一遍：协商（含 required 判定）→ 加密（psk）→ 兴趣过滤 → 限流（预算内）→ 业务帧往返 + 三级统计正确。证明五层组合不破坏彼此。

---

## 10. 验收标准

1. `bash tests/run.sh` 全绿：既有 113 项 + 新增约 35–40 项。
2. `dotnet build relay_server/RelayServer.sln` 通过。
3. `bash runtime/build-unity-dlls.sh` + `python runtime/build-mod.py` + `bash runtime/verify-unity.sh` 通过（核心库接口加法 + Network.Mod 新增源码均需回归 Unity 编译与 Play 冒烟）。
4. 既有业务协议对外 ABI 零变化（§7.3 清单逐项确认）。
5. 核心库仍零第三方依赖（`runtime/src` 无新增包引用）。

## 11. 风险与边界

| 风险 | 对策 |
|---|---|
| 默认开协商破坏既有测试 | Loopback 同步握手（`Start` 内完成）+ 后注册协议默认接受（§2.4） |
| Relay 异步/线程不确定性 | 测试一律 `PumpOnce()` 同步驱动；生产泵线程 marshal 列为引导期工作 |
| AES 在 Unity Mono 可用性 | 只用 netstandard2.1 表面 API（Aes/HMACSHA256/RandomNumberGenerator），不用 AesGcm |
| 加密后报文变大 | 配额分"逻辑字节/传输字节"两档统计；预算按逻辑字节执行（§5.3） |
| 明文兼容模式的安全错觉 | 未配置 psk 时保持现状（明文），但 `SecurityLayer` 存在即拦截未加密帧？——不：设计为显式 `Enable` 才启用，避免隐式行为变化；生产引导必须显式配置 |
| Relay 控制面 HTTP 依赖 | 测试注入 AllocationService 产物绕过 HTTP；Unity 侧 HttpClient（netstandard2.1 可用） |

## 12. 实施顺序建议（任务切分）

1. **骨架**：ProtocolRegistry / FrameCodec 从 NetworkRuntimeImpl 迁出（纯重构，测试保持全绿）。
2. **层一 协商**：HandshakeProtocol + NegotiationLayer + LegacyCodec。
3. **层四 流量**：TrafficMonitor/Layer（依赖最少，先落地统计）。
4. **层二 兴趣**：InterestManager + Broadcast 改造。
5. **层三 加密**：SecurityLayer + Provider。
6. **层五 Relay**：RelayWire → RelayClientTransport → relay_server 小改 → 集成测试。

每步提交一次（Conventional Commits，`feat(network)` 前缀），`tests/run.sh` 保持全绿再进下一步。
