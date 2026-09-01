# com.zombtoy.leaderboard 契约文档（Rule 19：契约 = 文档）

> 定位：高分榜——.NET 8 后端客户端（POST /addScore、GET /getAllScores）。单机 Host。
> 无 ECS 组件/系统（纯服务 Mod）。跨 Mod 只导出能力 + 发布消息。
> **异步约束**：HTTP 是异步的，但 ModCall 是同步的（§12.11）——`submit`/`refresh` 只入队立即返回 `accepted`，实际请求异步执行；完成时发布 `leaderboard:TopChanged` 供 UI 刷新。`get_top` 返回**最近一次缓存**。

---

## 1. 传输抽象（可无头测试的关键）

```csharp
public interface ILeaderboardTransport
{
    bool Submit(int score, string playerName);      // 失败返回 false（网络错误等）
    ScoreEntry[] GetTop(int n);                     // 返回最多 n 条 [score, name]
}
public readonly struct ScoreEntry { public readonly int Score; public readonly string Name; }
```

- 默认实现 `HttpLeaderboardTransport`（Client 模块，HttpClient → ServerUrl；netstandard2.1 兼容写法，Unity 桌面平台可用）。
- 静态桥 `LeaderboardTransport.Set(ILeaderboardTransport)`——本 Mod 内部状态（同 FPS 先例 PlayerMod.World 静态桥），测试注入 Fake 实现，无头测试不依赖真实后端。
- 后端契约（沿用原项目 ZombtoyBackend）：`POST {ServerUrl}/addScore`（body 纯文本分数或 `{"score":"N"}`）→ 存储；`GET {ServerUrl}/getAllScores` → 逗号分隔分数串。复刻后端放 `samples/Zombtoy/Backend/`（M3.2）。

## 2. 导出能力（ModCall，owner = com.zombtoy.leaderboard）

### leaderboard:submit —— 提交分数（任务通道 score→leaderboard）
- args：`[score int, playerName string]`
- 返回：`[accepted bool]`
- 语义：score<=0 → `[false]`；入队异步提交，立即 `[true]`；失败不影响游戏流程（静默记日志）。同分去重由后端处理（v1 不做客户端去重）。

### leaderboard:get_top —— 取榜（任务通道 score→leaderboard；实际消费方为 ui.ResultView）
- args：`[n int]`（1..50，越界钳制）
- 返回：`object[]` 行集（DataCodec 嵌套数组），行 = `[rank uint, score int, name string]`；无缓存 → `[]`。

### leaderboard:refresh —— 异步刷新榜单
- args：`[]`
- 返回：`[ok bool]`
- 语义：入队异步 `GetTop(MaxEntries)` → 更新缓存 → 发布 `leaderboard:TopChanged`；失败发布空表（UI 显示"加载失败"）。

## 3. 发布/订阅消息（谁定义谁发布）

### leaderboard:TopChanged v1
| field | 类型 | 语义 |
|---|---|---|
| 1 | uint (varint) | count |
| 2 | bytes (length-delimited) | rows：DataCodec 编码的嵌套数组，每行 `[rank uint, score int, name string]` |

订阅者：ui（Result 榜单刷新）。

## 4. 依赖声明（mod.json）

```json
"dependencies": [ { "id": "com.game.core", "version": ">=1.0.0" } ]
```

## 5. 消费的对端能力/消息

**无。** 纯能力提供方 + 消息发布方。

## 6. 测试向量（`src/LeaderboardTestVectors.cs`）

| ContractId | 形状 | 样例 | 消费方解析器 |
|---|---|---|---|
| `leaderboard_submit_args` | [score int, playerName string] | `[1200, "player1"]` | score.ScoreSystem |
| `leaderboard_gettop_args` | [n int] | `[10]` | ui.ResultView |
| `leaderboard_row` | [rank uint, score int, name string] | `[1u, 5000, "alice"]` | ui.ResultView（解析 TopChanged.rows 嵌套数组） |

消息载荷向量（手工构造；field2 嵌套数组用 DataCodec 编码）：

| ContractId | 字段表 | 样例 |
|---|---|---|
| `leaderboard_top_msg` | 1=count(uint) 2=rows(bytes) | `[2, [[1u,5000,"alice"],[2u,3000,"bob"]]]` |

## 7. 行为参数（LeaderboardConfig 常量表）

| 参数 | 值 | 来源 |
|---|---|---|
| ServerUrl | `http://localhost:3000`（可配，M3.2 后端地址） | 原版 Leaderboard scoreStorage |
| TimeoutMs | 5000 | 防御性默认 |
| MaxEntries | 20 | getTop 上限 |
| PlayerName | "player"（默认名；v1 无玩家名输入） | 简化决策 |
