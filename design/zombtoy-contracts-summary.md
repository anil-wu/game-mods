# Zombtoy 复刻：8 Mod 契约与实施设计汇总

> 权威分析：[`replica-Zombtoy-analysis.md`](replica-Zombtoy-analysis.md) ｜ 总纲：[`zombtoy-implementation-plan.md`](zombtoy-implementation-plan.md)
> 8 份详细契约：`samples/Zombtoy/contracts/com.zombtoy.{player,weapon,enemy,item,score,leaderboard,ui,game}.CONTRACT.md`
> 本文档是 **coder 任务的总入口**：先读本文档 §0（约定）+ 对应 Mod 的 CONTRACT.md，再动手。

---

## 0. 全仓库统一约定（每个 CONTRACT.md 都引用）

| # | 约定 | 依据 |
|---|---|---|
| 0.1 | 单机 Host：逻辑全部注册为 `SystemSide.Server`（无头测试用 `UpdateAll`）；`[NetworkReplicated]` 一律不需要 | 分析 §3.1 |
| 0.2 | **ModCall 参数/返回 = DataCodec 编码的 `object?[]` 纯数据元组**（uint/int/float/bool/string/嵌套数组），字段布局即契约 | Rule 14 / FPS 先例（CapabilityShapes） |
| 0.3 | **Message 载荷 = PayloadWriter 字段编号格式**（u16 LE 编号 + wireType u8 + data；0=Varint(zigzag)/1=Fixed32/2=Fixed64/3=LengthDelimited），append-only、未知字段跳过 | §14.11.1 |
| 0.4 | 枚举（EnemyKind/WeaponKind/ItemKind/GamePhase/SourceKind 等）**只传编号（u32 varint），数字即契约**；各 Mod 各自重复定义同名枚举 | Rule 19 可重复定义 |
| 0.5 | **伤害/治疗等需要同步返回的跨 Mod 操作在能力实现内同步执行并当场发消息**；系统只做周期行为（移动/体力/生成/冷却） | §12.11 / §14.10 |
| 0.6 | 视图（MonoBehaviour）不参与无头测试；视图↔实体映射：**视图挂 `public uint EntityId`**，射线命中 collider → `GetComponentInParent<XxxView>().EntityId` → 写请求组件 → 系统内 ModCall | 任务约束 / PushBox 先例 |
| 0.7 | 伤害源分类 `SourceKind`：0=Default(射线/接触) 1=Blast(爆炸) 2=Ice(冰) —— 爆炸免疫由 enemy 侧在 `enemy:damage` 内裁决 | enemy CONTRACT §4 |
| 0.8 | 数值（HP/伤害/弹药/生成参数）以**配置常量表**形式收敛进各 Mod（PlayerConfig/EnemyConfig/WeaponConfig/ItemConfig），结构固定、数值在资源迁移时按 prefab 校准；改动需升版本 | 机制等价原则 |
| 0.9 | 出生点/生成表等配置**由各 Mod 自持**（Rule 3），与 game Mod Level 场景标记同步（两处 CONTRACT 已注明） | 分析 §4.1 |

---

## 1. 跨 Mod 通道总表（Rule 12 四条通道内，全部二进制）

| 方向 | 通道 | 定义 | 载荷/返回 | 契约章节 |
|---|---|---|---|---|
| weapon → enemy | ModCall | `enemy:damage(target, amount, source, sourceKind?)` | args `[uint,int,uint,uint]` → `[bool killed]` | enemy §4 |
| enemy → player | ModCall | `player:damage(amount, source)` | args `[int,uint]` → `[bool killed]` | player §3 |
| enemy → score | Message | `enemy:EnemyKilled` | field1=enemyType(u32) 2=scoreValue(i32) 3..5=xyz(f32) | enemy §5 |
| player → ui | Message | `player:HealthChanged` | field1=current(i32) 2=max(i32) | player §4 |
| score → ui | Message | `score:ScoreChanged` | field1=current(i32) 2=highScore(i32) 3=kills(i32) | score §4 |
| enemy → ui | Message | `enemy:ZombieCountChanged` | field1=count(i32) | enemy §5 |
| score → leaderboard | ModCall | `leaderboard:submit(score, playerName)` | args `[int,string]` → `[bool accepted]` | leaderboard §2 |
| score → leaderboard | ModCall | `leaderboard:get_top(n)`（实际消费方 = ui.ResultView） | args `[int]` → 行集 `[rank u32,score i32,name string]` | leaderboard §2 |

**补充通道（编排所需，同样 Rule 12 内）**：

| 方向 | 通道 | 定义 |
|---|---|---|
| game → player | ModCall | `player:reset(x,y,z)` 新对局重置 |
| game → enemy | ModCall | `enemy:reset` / `enemy:set_spawning(bool)` 开局清场 + 相位开关 |
| game → item | ModCall | `item:reset` / `item:set_spawning(bool)` |
| game → weapon | ModCall | `weapon:reset` 弹药复位 |
| game → score | ModCall | `score:reset` 开局清零 |
| ui → game | ModCall | `game:start` / `game:to_menu` / `game:pause` / `game:resume` |
| game → 全 | Message | `game:StateChanged`（phase, reason）相位广播 |
| score → ui | Message | `score:GameOver`（finalScore, kills, isNewHigh）结算 |
| player → score | Message | `player:Died`（source）结算触发 |
| item → player | ModCall | `player:heal(amount)` 血瓶 |
| item → weapon | ModCall | `weapon:add_ammo(magazines)` 弹药箱 |
| weapon → enemy | ModCall（M2） | `enemy:apply_slow` / `enemy:get_all`（冰弹） |
| enemy → player | ModCall | `player:get_entity` / `player:get_position`（追击目标） |
| leaderboard → ui | Message | `leaderboard:TopChanged`（count, rows 嵌套） |

**通道纪律**：跨 Mod 只有 ModCall（有返回）/ Message（无返回）两通道被使用；UI 通道仅 ui Mod 与 WindowManager（com.game.ui）之间；Resource 通道仅各 Mod 与自己的 bundle。禁止：跨 Mod 引用程序集/类型、静态共享、反射、FindObjectOfType、传委托（Rule 12）。

---

## 2. 依赖 DAG（Manifest 声明，ModResolver 拓扑，无环）

```
com.game.core（框架，pinned）
com.game.ui（框架 WindowManager，pinned）

com.zombtoy.player ───────────────► core
com.zombtoy.leaderboard ──────────► core
com.zombtoy.enemy ──────► player
com.zombtoy.weapon ─────► player, enemy
com.zombtoy.item ───────► player, weapon
com.zombtoy.score ──────► player, enemy, leaderboard
com.zombtoy.game ───────► player, weapon, enemy, item, score, leaderboard
com.zombtoy.ui ─────────► core, ui(框架), game, player, weapon, enemy, score, leaderboard
```

**关键决策（避免环）**：game 只调组件 Mod 能力、不订阅组件消息（score:GameOver 除外）；ui 调 game 能力、game 不调 ui 能力——ui→game 单向，game→ui 不存在。窗口开/关由 ui 订阅 `game:StateChanged` 自行驱动。

## 3. 消息总表（owner = 发布者，谁定义谁发布）

| MessageId | 发布者 | 字段 |
|---|---|---|
| player:HealthChanged v1 | player | 1=current(i32) 2=max(i32) |
| player:StaminaChanged v1 | player | 1=current(f32) 2=max(f32) |
| player:Died v1 | player | 1=source(u32) |
| enemy:EnemyKilled v1 | enemy | 1=enemyType(u32) 2=scoreValue(i32) 3..5=xyz(f32) |
| enemy:ZombieCountChanged v1 | enemy | 1=count(i32) |
| score:ScoreChanged v1 | score | 1=current(i32) 2=highScore(i32) 3=kills(i32) |
| score:GameOver v1 | score | 1=finalScore(i32) 2=kills(i32) 3=isNewHigh(bool) |
| weapon:AmmoChanged v1 | weapon | 1=slot(u32) 2=inMag(i32) 3=reserve(i32) 4=maxMag(i32) |
| weapon:WeaponSwitched v1 | weapon | 1=slot(u32) |
| item:PickedUp v1 | item | 1=kind(u32) 2=amount(i32) |
| game:StateChanged v1 | game | 1=phase(u32) 2=reason(u32) |
| leaderboard:TopChanged v1 | leaderboard | 1=count(u32) 2=rows(bytes 嵌套) |

订阅关系：score←{enemy:EnemyKilled, player:Died}；game←{score:GameOver}；ui←{player×2, enemy:ZombieCountChanged, score×2, weapon×2, game:StateChanged, leaderboard:TopChanged, item:PickedUp(可选)}。

## 4. 组件/系统总览

| Mod | 组件 | 系统（全部 SystemSide.Server） | 说明 |
|---|---|---|---|
| player | Position3/Velocity3/Facing/PlayerHealth/Stamina/PlayerInput/PlayerFlags | InputApply → Movement → Stamina | 伤害/治疗在能力内同步（无系统） |
| enemy | EnemyType/EnemyHealth/EnemyFlags/EnemyAttack/EnemyMovement/EnemyTarget/EnemyPosition/EnemySpawner | Spawn → Chase → Attack → (Ranged M2) → (Boss M2) → Health(死亡清理) | 扣血在 enemy:damage 能力内；生成器 50 上限/加权表/间隔衰减 |
| weapon | WeaponSlot×4/WeaponAmmo/WeaponActive/WeaponOwner/ShotRequest/SwitchRequest/(Projectile M2) | Fire → Reload → Switch → (Projectile M2) | 伤害只在系统内调 enemy:damage |
| item | ItemData/ItemState/ItemPosition/ItemSpawner | Spawn（定时生成） | 拾取在 item:pickup 能力内 |
| score | ScoreState/RunResult | 无周期系统（消息 handler + 能力） | 订阅计分/结算 |
| leaderboard | 无 | 无 | 纯服务 + 传输抽象 |
| ui | 无 | 无 | 纯消费方，窗口×5 |
| game | GameState | 无周期系统（能力编排器 + 订阅） | 相位状态机 |

**更新顺序约定**：`SystemGroup.UpdateAll` 按注册顺序执行；各 Mod 系统间无跨 Mod 数据依赖（都经能力/消息协作），同 Mod 内按上表顺序注册。

## 5. 关键实现链路（coder 必读）

### 5.1 武器命中 → 敌人受伤 → 计分（全链路）
```
FireView（client）: Fire1 → Physics.Raycast(Shootable) → GetComponentInParent<EnemyView>().EntityId
    → 写 ShotRequest { ShooterId, HitEntityId, IsHit, HitXYZ, Kind }
FireSystem（server）: 校验冷却/弹药/换弹/存活 → 扣弹 → ModCall enemy:damage([hit, dmg, shooter, sourceKind])
enemy:damage 能力: 查实体/免疫 → 扣血 → 死: 发 enemy:EnemyKilled + ZombieCountChanged + 视图死亡表现 → 返回 [killed]
score（订阅）: EnemyKilled → 加分 → 发 score:ScoreChanged → ui 刷新
```

### 5.2 敌人接触 → 玩家受伤 → 结算
```
EnemyAttackSystem: 距离≤Range 且冷却到 → ModCall player:damage([dmg, enemyId])
player:damage 能力: 扣血 → 发 HealthChanged → 死: 发 player:Died → 返回 [killed]
score（订阅 Died）: 结算 → 发 score:GameOver → ModCall leaderboard:submit
game（订阅 GameOver）: enemy/item set_spawning(false) → 发 game:StateChanged(GameOver)
ui（订阅 GameOver）: 开 Result 窗口 → 调 leaderboard:refresh → 订阅 TopChanged 刷新榜单
```

### 5.3 新对局（Menu 点开始）
```
ui.MenuView → ModCall game:start
game:start → score:reset → player:reset(出生点) → enemy:reset → enemy:set_spawning(true)
           → item:reset → item:set_spawning(true) → weapon:reset → 发 game:StateChanged(Playing, Started)
ui（订阅）: 关 menu、开 hud
```

### 5.4 实体↔视图映射（通用模式，0.6）
- 视图组件统一 `public uint EntityId`；敌人/道具/投射物由各自 Mod 生成时绑定。
- 命中判定只发生在视图（物理）；**跨 Mod 伤害调用只发生在系统/能力内**（可无头测试）。

## 6. 测试向量与一致性校验（§14.11.3）

- owner 侧：每 Mod `src/XxxTestVectors.cs` → `public static TestVector[] All()`。
  - DataCodec 形状（ModCall args/rows）：`TestVector.Of(contractId, sample)`（owner encoder 产出规范字节）。
  - 消息载荷（字段编号形状）：手工 `new TestVector(contractId, sample, bytes)`，bytes 由 owner 消息编码器产出。
- 消费侧：`tests/TestRunner/ZombtoyTestVectorTests.cs`——各消费方解析器（ui.HudHealth.TryRead 等）解析 owner Bytes → 与 Sample 一致即"可重复定义未漂移"；另含漂移负向用例（改布局应被检出）。
- 消费矩阵（owner → 消费方解析器）：
  - player_damage_args → enemy.EnemyAttackSystem；player_get_state_row → ui.HudBinders / enemy.EnemyChaseSystem；player_health_msg → ui.HudHealth
  - enemy_damage_args → weapon.FireSystem；enemy_killed_msg → score.ScoreSystem；enemy_count_msg → ui.HudZombies
  - weapon_add_ammo_args → item.ItemPickup；weapon_ammo_msg → ui.HudAmmo
  - score_gameover_msg → game.GameFlow / ui.ResultSummary；score_changed_msg → ui.HudScore
  - leaderboard_row / leaderboard_top_msg → ui.LeaderboardRows
  - game_state_msg → ui（窗口路由）
  - 详见各 CONTRACT.md §7/§10。

## 7. 实现顺序建议（供 M0.2 起逐任务排期）

1. **M0.2 工程骨架**：`samples/Zombtoy/` 工程（复制 FPS 的 Packages/ProjectSettings/ModPacker）+ 8 个 `mods/<modId>/`（mod.json + asmdef + src 空壳 + CONTRACT.md 落位）。
2. **无头测试先行（每 Mod 先逻辑后视图）**：
   - 第一批（M1）：player（移动/体力/伤害能力）→ enemy（生成器加权表/追击/接触伤害）→ weapon（FireSystem 射线伤害链路）——按依赖序，先叶子后调用方；
   - 第二批（M2）：item（拾取）→ score（计分/结算）→ weapon 投射物族 + Titan；
   - 第三批（M3）：leaderboard（Fake 传输测试）→ game（相位编排）→ ui（解析器单测 + 视图）。
3. **视图后接**：每 Mod 逻辑验收后接 `*View.cs`（EntityId 绑定、NavMesh/射线/UGUI），`verify-unity.sh` 冒烟。
4. **每阶段验收**：`tests/run.sh` 全绿（含 Zombtoy 无头测试）+ `verify-unity.sh` + 案例 README 对标清单。

## 8. 已知差异与风险（继承总纲 §4）

- 不搬运原版 bug：Rocket.cs 的 `using UnityEditor`、Titan groundTarget 绑错（用独立准星 decal）。
- URP→内置 Standard 转换在 ModPacker；`_BaseColor` 红映射按实际纹理校验。
- UI↔UGUI 绑定（WindowManager 基础版）与 Unity 资源后端是框架侧前置缺口（M0.4），本契约按"窗口声明 + 资源经 context.Resources"设计，不依赖其完成度即可开工逻辑。
- 数值以资源迁移校准为准；任何改动需同步 CONTRACT 版本与测试向量。
