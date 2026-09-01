# com.zombtoy.game 契约文档（Rule 19：契约 = 文档）

> 定位：主 Mod / 引导 Mod——依赖全部组件 Mod（装它一个 = 装整个游戏），单宿主场景状态机（Menu → Playing → Paused → GameOver → Result）。
> 职责：相位状态（单实体 GameState）+ 对局编排（start/to_menu/pause/resume）+ 订阅 score:GameOver 收尾。
> **方向约定（避免依赖环）**：本 Mod 只调用组件 Mod 的能力、只发布 `game:StateChanged`；不订阅组件消息以外的任何东西（score:GameOver 除外）；UI 按钮回调由 ui Mod 调本 Mod 能力（ui → game 单向依赖）。

---

## 1. 枚举（编号即契约，u32）

```csharp
public enum GamePhase : byte { Menu=0, Playing=1, Paused=2, GameOver=3, Result=4 }
public enum GameReason : byte { Manual=0, Started=1, Paused=2, Resumed=3, PlayerDied=4, ToMenu=5 }
```

## 2. ECS 组件（仅本 Mod 读写）

```csharp
public struct GameState : IComponent { public byte Phase; public byte Reason; } // 单实体（Register 创建，初始 Menu）
```

## 3. 逻辑（无周期 System；能力实现 = 编排器，可无头测试）

- **game:start**：`score:reset` → `player:reset(出生点)` → `enemy:reset` → `enemy:set_spawning(true)` → `item:reset` → `item:set_spawning(true)` → `weapon:reset` → 写 Phase=Playing → 发 `game:StateChanged(Playing, Started)`。
- **OnScoreGameOver**（订阅 score:GameOver）：`enemy:set_spawning(false)` → `item:set_spawning(false)` → 写 Phase=GameOver → 发 `game:StateChanged(GameOver, PlayerDied)`。（Result 相位由 ui 在收到 GameOver 后自行打开 Result 窗口，本 Mod 不设 Result 相位转移——见 §5 备注。）
- **game:to_menu**：写 Phase=Menu → 发 `game:StateChanged(Menu, ToMenu)`。
- **game:pause**：`enemy:set_spawning(false)` → `item:set_spawning(false)` → 写 Phase=Paused → 发 `game:StateChanged(Paused, Paused)`。（Time.timeScale=0 由 ui PauseView 视图层处理，逻辑相位与 Unity 时间缩放解耦。）
- **game:resume**：`enemy:set_spawning(true)` → `item:set_spawning(true)` → 写 Phase=Playing → 发 `game:StateChanged(Playing, Resumed)`。
- 玩家死亡后不再移动/攻击：由 player 的 IsDead 天然拦截（EnemyAttack 判定 alive、FireSystem 判定存活），无需 game 介入。

## 4. 导出能力（ModCall，owner = com.zombtoy.game）

| 能力 | args | 返回 | 语义 |
|---|---|---|---|
| `game:status` | `[]` | `[phase uint]` | QA/调试 |
| `game:start` | `[]` | `[ok bool]` | 新对局（见 §3） |
| `game:to_menu` | `[]` | `[ok bool]` | 回主菜单 |
| `game:pause` | `[]` | `[ok bool]` | 暂停 |
| `game:resume` | `[]` | `[ok bool]` | 恢复 |

## 5. 发布/订阅消息（谁定义谁发布）

### game:StateChanged v1 —— 相位广播（ui 开/关窗口、调试）
| field | 类型 | 语义 |
|---|---|---|
| 1 | uint (varint) | phase（GamePhase 编号） |
| 2 | uint (varint) | reason（GameReason 编号） |

订阅者：ui（窗口开/关）。备注：Result 相位不单独发——ui 收到 `score:GameOver` 直接开 Result 窗口（GameOver 相位已覆盖收尾语义）；如需"Result 专属相位"（如再玩一次按钮位置），v2 append-only 追加 phase=4。

## 6. 依赖声明（mod.json）

```json
"dependencies": [
  { "id": "com.game.core", "version": ">=1.0.0" },
  { "id": "com.zombtoy.player", "version": ">=0.1.0" },      // player:reset
  { "id": "com.zombtoy.weapon", "version": ">=0.1.0" },      // weapon:reset
  { "id": "com.zombtoy.enemy", "version": ">=0.1.0" },       // enemy:reset / enemy:set_spawning
  { "id": "com.zombtoy.item", "version": ">=0.1.0" },        // item:reset / item:set_spawning
  { "id": "com.zombtoy.score", "version": ">=0.1.0" },       // score:reset / score:GameOver 订阅
  { "id": "com.zombtoy.leaderboard", "version": ">=0.1.0" }  // （可选预留：Result 流程扩展）
]
```

## 7. 消费的对端能力/消息

| 对端 | 能力/消息 | 用途 |
|---|---|---|
| score | `score:reset` / `score:get_state` | 开局清零 / 调试 |
| player | `player:reset` | 出生点重置 |
| enemy | `enemy:reset` / `enemy:set_spawning` | 开局清场 + 相位开关 |
| item | `item:reset` / `item:set_spawning` | 同上 |
| weapon | `weapon:reset` | 弹药复位 |
| score | `score:GameOver`（订阅） | 收尾（停生成 + 发 GameOver 相位） |

## 8. 场景/资源视图（MonoBehaviour，不参与无头测试）

- `GameView`：Register 时从本 Mod 资源域加载 Level1 环境 prefab（`context.Resources`，Rule 3）实例化；持有环境/天空盒/光照（内置管线 Standard 材质）。
- Level 环境资源映射：`replica_projects/Zombtoy/Assets/Level1`（地板/墙/障碍）、`AllSkyFree` 天空盒、`Sci-Fi Pack` 环境模型 → 本 Mod bundle（分析 §4.1）。
- 出生点标记：Level prefab 内的 EnemySpawnPoints/ItemSpawnPoints 仅供视觉参考；**逻辑出生点由 enemy/item Mod 自持配置**（与标记位置同步约束，见 enemy/item CONTRACT §9）。

## 9. 测试向量（`src/GameTestVectors.cs`）

| ContractId | 形状 | 样例 | 消费方解析器 |
|---|---|---|---|
| `game_status_row` | [phase uint] | `[1u]` | QA/调试 |
| `game_start_args` | [] | `[]` | ui.MenuView（回调侧） |
| `game_to_menu_args` | [] | `[]` | ui.ResultView/PauseView |

消息载荷向量（手工构造）：

| ContractId | 字段表 | 样例 |
|---|---|---|
| `game_state_msg` | 1=phase(uint) 2=reason(uint) | `[1u, 1u]`（Playing/Started） |
