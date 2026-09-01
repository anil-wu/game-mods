# com.zombtoy.score 契约文档（Rule 19：契约 = 文档）

> 定位：分数——击杀计分/击杀数/最高分/结算。单机 Host。线格式约定同 summary §0。
> 本 Mod 是**消息消费方**（订阅 enemy:EnemyKilled、player:Died）+ 能力提供方；无周期 System（逻辑在消息 handler + 能力实现内，可无头测试）。
> 结算链路：player:Died → 结算（存最高分、发 score:GameOver、调 leaderboard:submit）→ game 订阅 GameOver 收尾。

---

## 1. ECS 组件（仅本 Mod 读写）

```csharp
public struct ScoreState : IComponent { public int Current; public int HighScore; public int Kills; public bool IsNewHigh; } // 单实体
public struct RunResult  : IComponent { public int FinalScore; public int Kills; public bool IsNewHigh; public bool Finalized; } // 结算快照（供 Result 展示）
```

## 2. 逻辑（无周期系统；消息 handler + 能力实现，全部可无头测试）

- **OnEnemyKilled**（订阅 enemy:EnemyKilled）：`Current += scoreValue`（字段 2，100 普通/500 首领）；`Kills++`；`IsNewHigh = Current > HighScore`（更新 HighScore）；发 `score:ScoreChanged`。
- **OnPlayerDied**（订阅 player:Died）：若未结算 → 写 RunResult（FinalScore/Kills/IsNewHigh/Finalized=true）、发 `score:GameOver`、调 `leaderboard:submit([FinalScore, PlayerName])`。
- 连击（config comboMultiplier=2）：原版仅配置未生效，v1 不实现；后续 append-only 扩展（`score:ScoreChanged` 加字段或新消息）。

## 3. 导出能力（ModCall，owner = com.zombtoy.score）

### score:reset —— 新对局重置（game:start 调用）
- args：`[]`
- 返回：`[ok bool]`
- 语义：Current=0、Kills=0、IsNewHigh=false、RunResult 清空；发 `score:ScoreChanged(0, high, 0)`。（对齐原版：ResetScore 即游戏开始信号。）

### score:get_state —— Result 初始填充
- args：`[]`
- 返回：`[score int, highScore int, kills int, isNewHigh bool]`
- 语义：返回 RunResult（Finalized 时）或 ScoreState。

## 4. 发布/订阅消息

### score:ScoreChanged v1 —— 分数（任务通道 score→ui）
| field | 类型 | 语义 |
|---|---|---|
| 1 | int (varint) | current |
| 2 | int (varint) | highScore |
| 3 | int (varint) | kills |

订阅者：ui（HUD 分数/最高分）。

### score:GameOver v1 —— 结算（game 收尾 + ui Result 打开）
| field | 类型 | 语义 |
|---|---|---|
| 1 | int (varint) | finalScore |
| 2 | int (varint) | kills |
| 3 | bool (varint) | isNewHigh |

订阅者：game（停 enemy/item 生成、发 game:StateChanged(GameOver)）、ui（打开 Result 窗口）。

## 5. 依赖声明（mod.json）

```json
"dependencies": [
  { "id": "com.game.core", "version": ">=1.0.0" },
  { "id": "com.zombtoy.player", "version": ">=0.1.0" },      // 订阅 player:Died
  { "id": "com.zombtoy.enemy", "version": ">=0.1.0" },      // 订阅 enemy:EnemyKilled
  { "id": "com.zombtoy.leaderboard", "version": ">=0.1.0" } // 调 leaderboard:submit
]
```

## 6. 消费的对端能力/消息

| 对端 | 能力/消息 | 用途 |
|---|---|---|
| enemy | `enemy:EnemyKilled`（订阅） | 计分 |
| player | `player:Died`（订阅） | 结算触发 |
| leaderboard | `leaderboard:submit` | 提交最高分（PlayerName 用默认名，见 leaderboard CONTRACT §9） |

## 7. 测试向量（`src/ScoreTestVectors.cs`）

| ContractId | 形状 | 样例 | 消费方解析器 |
|---|---|---|---|
| `score_reset_args` | [] | `[]` | game.GameFlow |
| `score_get_state_row` | [score int, highScore int, kills int, isNewHigh bool] | `[1200, 5000, 12, false]` | ui.ResultView |

消息载荷向量（手工构造）：

| ContractId | 字段表 | 样例 |
|---|---|---|
| `score_changed_msg` | 1=current(int) 2=highScore(int) 3=kills(int) | `[1200, 5000, 12]` |
| `score_gameover_msg` | 1=finalScore(int) 2=kills(int) 3=isNewHigh(bool) | `[1200, 12, true]` |

## 8. 行为参数（ScoreConfig 常量表）

| 参数 | 值 | 来源 |
|---|---|---|
| 普通击杀分 | 由 enemy:EnemyKilled.scoreValue 携带（默认 100） | ScoreManager enemyKillScore=100 |
| 首领击杀分 | 同上（默认 500） | bossKillScore=500 |
| 最高分持久化 | PlayerPrefs key "HighScore"（本端本地持久化） | ScoreManager Load/SaveHighScore |
