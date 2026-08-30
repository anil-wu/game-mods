# com.game.network 契约文档（Rule 19：契约 = 文档）

> Network.Mod：所有 Mod 共享的网络基础设施（Framework Mod，§10.4）。本文档覆盖
> **Relay 线格式契约**（§11.11/§6.2）——跨端（relay_server ↔ Network.Mod）字段布局的唯一事实来源。
> 消费方（relay_server 或未来其他传输实现）按本契约**各自重复定义解析器**，不共享程序集（Rule 19）。
> 回归锁定：RelayWireVectors 发布测试向量（§14.11.3），消费方 CI 用自己解析器解析同一字节。

---

## 1. 定位

- **协议注册表 / 路由 / 方向校验 / 版本 / 加密 / 统计 / 配额**：全游戏唯一网络入口与出口。
- 业务 Mod 只定义协议（Rule 4）与 CONTRACT.md 的字节布局，永远不引用 Mirror / Transport（Rule 12）。

## 2. Relay 线格式（relay 报文，UDP 数据面）

### 2.1 报文头（6 字节，小端 LE）

```
[Magic u16 = 0x4D52 "MR"][Ver u8 = 1][Type u8][ConnId u16 LE] + payload（≤ 1400B）
```

### 2.2 报文类型（Type）

| Type | 值 | 方向 | 说明 |
|---|---|---|---|
| BindHost | 1 | 节点 ← Host | 房主绑定（payload = Bind 载荷） |
| BindClient | 2 | 节点 ← 客户端 | 客户端绑定（payload = Bind 载荷） |
| BindAck | 3 | 节点 → 双方 | 绑定成功；**报头 ConnId = 绑定者自己的 ConnId**（Host=0，客户端=分配的 id） |
| BindFail | 4 | 节点 → 双方 | 绑定失败（token 无效/过期、房间无主、房间满） |
| PeerJoined | 5 | 节点 → Host | 新客户端入房（报头 ConnId = 新客户端 id，payload = ConnId u16 LE） |
| PeerLeft | 6 | 节点 → Host | 客户端离开（报头 ConnId = 离开的客户端 id，payload = ConnId u16 LE） |
| DataKcp | 7 | 双向 | 可靠流量（KCP 段）——原型期未启用，按 raw 语义转发 |
| DataRaw | 8 | 双向 | 不可靠流量：**加密信封字节**（§11.12：Relay 只见信封，不解析、不参与路由） |
| Ping | 9 | 双向 | 心跳（节点回 Pong） |
| Pong | 10 | 双向 | 心跳回复 |
| Leave | 11 | 双向 → 节点 | 退房：节点移除 peer；客户端离开通知 Host PeerLeft |

### 2.3 Bind 载荷（33 字节，LE）

```
[roomId u64][role u8][expiry u64 ms][token 16B]
role: 0 = Host（BindHost 用）; 1 = Client（BindClient 用）
expiry = token 签发时的过期时刻（须与 token 内 HMAC 签名一致才能过节点校验）
token  = TokenCodec 无状态 HMAC-SHA256(secret, roomId+role+expiry) 截断 16 字节
```

### 2.4 ConnId 分配约定（重要）

| ConnId | 归属 |
|---|---|
| 0 | Host（房主固定） |
| **1** | **Host 本地客户端回环**（§11.3 硬规则：内存回环，不占 Relay 编号） |
| 2 … 65533 | 节点为远端客户端顺序分配（`NextConnId` 从 2 起） |

> 冲突防护：远端客户端 ConnId 从 2 起，避免与 Host 本地客户端 connId=1 冲突。

## 3. 控制面 HTTP（relay_server）

| 端点 | 请求 | 响应 |
|---|---|---|
| POST /allocate | 空 | 管道分隔文本（2xx） |
| POST /resolve | JSON `{"joinCode":"ABCDEF"}` | 同上；404 = 房间码无效 |
| GET /health | 空 | JSON `{"status":"ok",...}`（唯一 JSON 响应） |

响应格式（§6.3）：

```
joinCode|roomId|relayEndpoint(host:port)|tokenB64|expiryMs
```

客户端零依赖解析（netstandard2.1 无 System.Text.Json）；token 为 16 字节 HMAC 的 base64。

## 4. Session 模型（§11.11）

- Host：`CreateSession()` = POST /allocate → 得房间码（对外公布）→ `BindHost`。
- Client：`JoinSession(房间码)` = POST /resolve → 得 token → `BindClient` → 节点找到 Host → 建立转发。
- Relay 节点视角只有一个 Session（roomId）下的连接集合，不认识任何游戏概念。

## 5. 测试向量

`mods/com.game.network/src/Relay/RelayWireVectors.cs`：

- `relay:bind-host` / `relay:bind-client`：Bind 报文样例字节（含 33B 载荷）。
- `relay:data-raw`：DataRaw 报文样例（connId=7，4B 样例信封）。
- `relay:peer-left`：PeerLeft 报文样例。

消费方解析 `TestVector.Bytes` 应解出与 `TestVector.Sample` 一致的字段（字段级容错，跳读未知字段）。
