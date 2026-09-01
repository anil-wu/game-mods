# com.zombtoy.ui 契约文档（Rule 19：契约 = 文档）

> 定位：UI——菜单/HUD/结算/设置/暂停。**纯消费方**（订阅各 Mod 消息 + 调用 game/leaderboard 能力），无 ECS 逻辑。
> 窗口必须经 UI.Mod（com.game.ui）的 WindowManager 注册与打开（Rule 9），窗口实例与 prefab 归本 Mod（Rule 3/§10.8）。
> MVP 方案（总纲 §0）：窗口 prefab → `context.Ui.RegisterWindow` 声明，`context.Ui.Open/Close` 控制；WindowManager 绑定做基础版。

---

## 1. 窗口（WindowId = `com.zombtoy.ui:{name}`，注册给 UI.Mod）

| WindowId | 视图 | 打开时机 | 内容 |
|---|---|---|---|
| `ui:menu` | MenuView | game:StateChanged(Menu) | 标题/开始按钮/设置按钮/排行榜入口 |
| `ui:hud` | HudView | game:StateChanged(Playing) | 血条/体力条/分数/僵尸数/弹药/准星/武器名 |
| `ui:result` | ResultView | score:GameOver 或 game:StateChanged(GameOver) | 本局分数/最高分/击杀数/是否新纪录/榜单/再玩一次/回主菜单 |
| `ui:settings` | SettingsView | 菜单内打开 | 灵敏度/键位提示/音量（MVP 可只做灵敏度+音量，对齐原版） |
| `ui:pause` | PauseView | Esc → game:pause | 继续/回主菜单 |

## 2. 视图数据绑定（订阅消息，全部可无头测试为解析器）

| 视图 | 订阅消息 | 解析（TryRead，各自重复定义，Rule 19） | 渲染 |
|---|---|---|---|
| HudView | `player:HealthChanged` | field1=current(int) field2=max(int) | 血条 fillAmount + 红屏闪烁（damage flash） |
| HudView | `player:StaminaChanged` | field1=current(float) field2=max(float) | 体力条（满时隐藏，对齐原版 StaminaSliderWhole） |
| HudView | `score:ScoreChanged` | field1=current(int) field2=highScore(int) field3=kills(int) | "Score: N" |
| HudView | `enemy:ZombieCountChanged` | field1=count(int) | "Zombies: N" |
| HudView | `weapon:AmmoChanged` | field1=slot(uint) 2=inMag(int) 3=reserve(int) 4=maxMag(int) | "inMag/reserve" |
| HudView | `weapon:WeaponSwitched` | field1=slot(uint) | GunText 武器名/字号 |
| ResultView | `score:GameOver` | field1=finalScore(int) 2=kills(int) 3=isNewHigh(bool) | 结算文本 + "New High Score!" |
| ResultView | `leaderboard:TopChanged` | field1=count(uint) field2=rows(bytes 嵌套) | 榜单滚动列表 |
| 全部 | `game:StateChanged` | field1=phase(uint) 2=reason(uint) | 按相位开/关窗口 |

**HUD 初始填充**（窗口打开时拉一次）：`player:get_state`、`enemy:get_count`、`weapon:get_state`、`score:get_state`（Result）。

## 3. 调用/订阅的对端（全部声明依赖）

| 对端 | 通道 | 用途 |
|---|---|---|
| game | `game:start`（ModCall） | 菜单"开始游戏"按钮 |
| game | `game:to_menu`（ModCall） | Result/暂停"回主菜单" |
| game | `game:pause` / `game:resume`（ModCall） | Esc 暂停/继续 |
| game | `game:StateChanged`（订阅） | 相位驱动开/关窗口 |
| score | `score:GameOver`（订阅） | 打开 Result |
| leaderboard | `leaderboard:refresh`（ModCall）+ `leaderboard:TopChanged`（订阅） | Result 打开时刷新榜单 |

其余订阅见 §2 表（player/weapon/enemy/score 消息）。**本 Mod 不发布任何跨 Mod 消息、不导出能力。**

## 4. 依赖声明（mod.json）

```json
"dependencies": [
  { "id": "com.game.core", "version": ">=1.0.0" },
  { "id": "com.game.ui", "version": ">=1.0.0" },        // WindowManager（UI.Mod 基础设施）
  { "id": "com.zombtoy.game", "version": ">=0.1.0" },   // game:start/to_menu/pause/resume + StateChanged
  { "id": "com.zombtoy.player", "version": ">=0.1.0" }, // HealthChanged/StaminaChanged 订阅
  { "id": "com.zombtoy.weapon", "version": ">=0.1.0" }, // AmmoChanged/WeaponSwitched 订阅
  { "id": "com.zombtoy.enemy", "version": ">=0.1.0" },  // ZombieCountChanged 订阅
  { "id": "com.zombtoy.score", "version": ">=0.1.0" },  // ScoreChanged/GameOver 订阅
  { "id": "com.zombtoy.leaderboard", "version": ">=0.1.0" } // refresh + TopChanged 订阅
]
```

## 5. 测试向量（`src/UiTestVectors.cs`）

本 Mod 是**消费方**，不发布数据；但仍提供"消费方解析器"的向量回灌：把各 owner 的向量在其 `All()` 中直接复用即可（TestVectorTests 消费矩阵见 summary §6）。本 Mod 产出以下解析器（供 coder 实现与单测）：

| 解析器 | 解析对象（owner 向量） |
|---|---|
| `HudHealth.TryRead(in PayloadReader)` | player_health_msg |
| `HudStamina.TryRead` | player_stamina_msg |
| `HudScore.TryRead` | score_changed_msg |
| `HudZombies.TryRead` | enemy_count_msg |
| `HudAmmo.TryRead` | weapon_ammo_msg |
| `ResultSummary.TryRead` | score_gameover_msg |
| `LeaderboardRows.TryRead` | leaderboard_top_msg |

## 6. 视图约定（MonoBehaviour，不参与无头测试）

- 全部窗口挂在宿主 Canvas（经 WindowManager 实例化，禁止直接 Instantiate 到全局 Canvas，Rule 10.9）。
- UGUI 组件绑定在窗口 prefab 内完成；ViewModel 数据来自消息解析器（§2）。
- 灵敏度/键位：MVP 最小集（灵敏度→鼠标射线灵敏度系数，经 SettingsView 本地持久化 PlayerPrefs）。
