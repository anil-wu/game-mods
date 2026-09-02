# com.zombtoy.enemy 契约文档（Rule 19：契约 = 文档）

> 定位：敌人——10 种类型 + 加权生成器 + 追击/接触伤害/远程（Clown 火球）/首领 Titan（地面锁定 AOE）+ 死亡计分。
> 单机 Host。线格式约定同 summary §0（ModCall=DataCodec 元组，Message=字段编号）。
> 敌人实体由本 Mod 创建/销毁；**视图（EnemyView）挂 EntityId**，武器射线命中 collider → 取 EntityId → `enemy:damage`（§8）。

---

## 1. 枚举（编号即契约，各 Mod 重复定义，u32）

```csharp
public enum EnemyKind : byte { Zombunny=0, ZomBear=1, Hellephant=2, GiantZombunny=3, GiantZomBear=4, GiantHellephant=5, Titan=6, Clown=7, MiniClown=8, ZomDuck=9 }
public enum AttackKind : byte { Contact=0, Fireball=1, GroundTarget=2 }   // GroundTarget=Titan 地面锁定
```

## 2. ECS 组件（仅本 Mod 读写）

```csharp
public struct EnemyType     : IComponent { public byte Kind; }                            // EnemyKind
public struct EnemyHealth   : IComponent { public int Current; public int Max; public int ScoreValue; } // 死亡计分值
public struct EnemyFlags    : IComponent { public bool IsDead; public bool IsSinking; public bool BlastImmunity; public float SinkTimer; }
public struct EnemyAttack   : IComponent { public byte Kind; public int Damage; public float Range; public float Interval; public float Cooldown; }
public struct EnemyMovement : IComponent { public float Speed; public float SpeedMul; public float EffectTimer; } // 缓速状态（冰）
public struct EnemyTarget   : IComponent { public uint PlayerEntityId; }                  // 经 player:get_entity 填充
public struct EnemyPosition : IComponent { public float X, Y, Z; }                        // 逻辑位置（视图 NavMesh 同步回写）
public struct EnemySpawner  : IComponent { public bool Enabled; public float Timer; public int ActiveCount; public float SpawnInterval; } // 单实体（生成器状态机）
```

生成器每次生成：`context.Ecs.CreateEntity()` 建敌人实体（归属本 Mod），视图经静态桥实例化 prefab 并绑定 EntityId。

```csharp
public struct EnemyProjectile : IComponent { public byte Kind; public int Damage; public uint Source; public float Speed; public float Lifetime; public float X, Y, Z; public float DirX, DirY, DirZ; } // M2：Clown 火球逻辑投射物（直线无追踪）；Kind=EnemyProjectileKind（Fireball/Rocket）
public struct EnemyBossLock  : IComponent { public bool Tracking; public float Charge; public float X, Z; }            // M2：Titan 地面锁定（准星落点 X/Z + 蓄力进度，仅本 Mod 视图读）
```

## 3. ECS 系统（职责 / 读写组件 / 更新顺序）

| 序 | 系统 | 职责 | 读 | 写 | 跨 Mod / 消息 |
|---|---|---|---|---|---|
| 1 | `EnemySpawnSystem` | 加权生成表 + 每类型冷却/并发上限/startBuffer + 全局并发上限 50 + 间隔衰减（3s→×0.99→下限 0.6s）；仅在 Enabled 且玩家存活时生成；生成即发 ZombieCountChanged | EnemySpawner, EnemyType 表（config） | EnemySpawner | 调 `player:get_position`（存活判定） |
| 2 | `EnemyChaseSystem` | 追击：每帧取玩家位置（`player:get_position`）→ 计算敌人期望朝向/速度（SpeedMul 缓速生效）→ 写 EnemyMovement；视图 NavMesh 按此驱动 | EnemyPosition, EnemyMovement, EnemyFlags | EnemyMovement | 调 `player:get_position` |
| 3 | `EnemyAttackSystem` | 接触伤害：EnemyFlags 存活 + 与玩家距离 ≤ Range + 冷却到 → 调 `player:damage`；只处理 Contact，GroundTarget/Fireball 类型交给 Ranged/Boss | EnemyAttack, EnemyPosition, EnemyFlags | EnemyAttack.Cooldown | 调 `player:damage` |
| 4 | `EnemyRangedSystem` | Clown 火球：射程内冷却 → 生成 EnemyProjectile 逻辑实体（直线飞行、无追踪）+ 视图请求；命中玩家 → 调 `player:damage`；超时/命中销毁实体+视图 | EnemyAttack, EnemyPosition, EnemyFlags, EnemyProjectile | EnemyAttack.Cooldown | 调 `player:damage`（命中时） |
| 5 | `EnemyBossSystem` | Titan：射程内准星（独立 decal，不复刻原版 groundTarget 绑地板 bug）向玩家 XZ lerp 追踪 + 蓄力（蓄力时长 = Interval）→ 落点 AOE（半径 3.0）调 `player:damage`；玩家跑出 AOE 圈可躲 | EnemyFlags, EnemyPosition, EnemyBossLock | EnemyBossLock | 调 `player:damage` |
| 6 | `EnemyHealthSystem` | 死亡后置：IsSinking 下沉计时 → 到点销毁实体 + 通知视图销毁（视图下沉由 EnemyView 动画处理）；IsDead 后每帧确认不再追击 | EnemyFlags, EnemyHealth | EnemyFlags | 通知视图（静态桥） |

**扣血在 `enemy:damage` 能力实现内同步执行**（返回 killed 需要当场结果）：扣血 → 若死：置 IsDead/IsSinking、发 `enemy:EnemyKilled`、`enemy:ZombieCountChanged`（-1）、通知视图播放死亡；返回 `[true]`。

## 4. 导出能力（ModCall，owner = com.zombtoy.enemy）

### enemy:damage —— 武器伤害入口（任务通道 weapon→enemy）
- args：`[target uint, amount int, source uint, sourceKind uint]`（sourceKind 可选，默认 0；0=普通/射线/接触，1=爆炸，2=冰）
- 返回：`[killed bool]`
- 语义：实体不存在/已死/amount<=0 → `[false]`；sourceKind==1 且 BlastImmunity → `[false]`（爆炸免疫，火箭筒被 Titan 吸收）；扣血；死 → 发 `enemy:EnemyKilled` + `enemy:ZombieCountChanged` + 视图死亡表现，返回 `[true]`。
- append-only：v1 只有 field 1..3，sourceKind 为 v1.1 追加（老调用方不传即默认 0）。

### enemy:apply_slow —— 冰手枪缓速（M2）
- args：`[target uint, speedMul float, duration float]`
- 返回：`[applied bool]`
- 语义：写 EnemyMovement.SpeedMul/EffectTimer；EnemyChaseSystem 每帧衰减恢复。

### enemy:get_all —— 敌人快照行集（冰弹追踪/调试，M2）
- args：`[]`
- 返回：`object[]` 行集，行 = `[entityId uint, x float, y float, z float, kind uint, alive bool, hp int, maxHp int]`

### enemy:get_count —— HUD 初始填充
- args：`[]`
- 返回：`[count int]`

### enemy:set_spawning —— 开/关生成（game 相位控制）
- args：`[enabled bool]`
- 返回：`[ok bool]`
- 语义：写 EnemySpawner.Enabled；enabled=false 时清空 Timer（暂停）；不销毁现有敌人。

### enemy:reset —— 新对局重置（game:start 调用）
- args：`[]`
- 返回：`[ok bool]`
- 语义：销毁全部敌人实体+视图、ActiveCount=0、间隔=base 3s、发 `enemy:ZombieCountChanged(0)`。

## 5. 发布/订阅消息（谁定义谁发布）

### enemy:EnemyKilled v1 —— 计分入口（任务通道 enemy→score）
| field | 类型 | 语义 |
|---|---|---|
| 1 | uint (varint) | enemyType（EnemyKind 编号） |
| 2 | int (varint) | scoreValue（100 普通 / 500 首领，按 config） |
| 3 | float (fixed32) | x（死亡位置，死亡特效用；可缺省） |
| 4 | float (fixed32) | y |
| 5 | float (fixed32) | z |

订阅者：score（计分 + 击杀数）、ui（可选击杀反馈）。

### enemy:ZombieCountChanged v1 —— 僵尸数（任务通道 enemy→ui）
| field | 类型 | 语义 |
|---|---|---|
| 1 | int (varint) | count |

订阅者：ui（HUD "Zombies: N"）。

## 6. 依赖声明（mod.json）

```json
"dependencies": [
  { "id": "com.game.core", "version": ">=1.0.0" },
  { "id": "com.zombtoy.player", "version": ">=0.1.0" }
]
```

## 7. 消费的对端能力/消息

| 对端 | 能力/消息 | 用途 |
|---|---|---|
| player | `player:damage` | 接触/远程伤害 |
| player | `player:get_entity` | 追击目标定位（Register 时取一次） |
| player | `player:get_position` | 追击目标点 + 生成存活判定 |

## 8. 视图约定

- `EnemyView`：持有 `public uint EntityId`；NavMesh 追击（按 EnemyMovement 速度/朝向）、死亡下沉动画、受击/死亡粒子、头顶血条（面向相机）。
- `EnemySpawnerView`（或并入 EnemyMod 静态桥）：按 SpawnSystem 通知实例化 prefab → 绑定 EntityId。
- 命中链路：FireView 射线 → `GetComponentInParent<EnemyView>().EntityId` → ShotRequest → FireSystem → `enemy:damage`。

## 9. 行为参数（EnemyConfig 常量表；数值为默认配置，资源迁移时按 prefab 校准，改动升版本）

| kind | HP | ScoreValue | Speed | 攻击 | 备注 |
|---|---|---|---|---|---|
| Zombunny | 100 | 100 | 3.0 | 接触 10 / 0.5s | |
| ZomBear | 200 | 100 | 2.5 | 接触 15 / 0.5s | |
| Hellephant | 300 | 100 | 2.0 | 接触 20 / 0.5s | |
| GiantZombunny | 500 | 100 | 2.2 | 接触 25 / 0.5s | |
| GiantZomBear | 700 | 100 | 1.8 | 接触 30 / 0.5s | |
| GiantHellephant | 900 | 100 | 1.5 | 接触 35 / 0.5s | |
| Titan | 2000 | 500 | 1.2 | 地面锁定 AOE | 首领；BlastImmunity=true |
| Clown | 100 | 100 | 2.8 | 火球 40，射程 25 | 远程 |
| MiniClown | 20 | 50 | 4.0 | 接触 5 | 由 Clown 生成（M2） |
| ZomDuck | 100 | 100 | 4.5 | 接触 10 | 快速 |

**生成器**：baseSpawnTime=3.0s、spawnTimeReduction=×0.99/次、minSpawnTime=0.6s、maxTotalEnemies=50；每类型 weight/minSpawnTime/maxSpawnTime/maxConcurrent/groupSize/startBufferTime 在 `data/enemy_spawn_table`（或 config 常量）中，结构固定、数值可调。

**出生点**：由本 Mod 自持（`EnemyConfig.SpawnPoints` 常量数组，Rule 3 自己的配置）——与 game Mod 的 Level 场景标记位置保持一致（两处 CONTRACT 都注明此同步约束）。

## 10. 测试向量（`src/EnemyTestVectors.cs`）

| ContractId | 形状 | 样例 | 消费方解析器 |
|---|---|---|---|
| `enemy_damage_args` | [target uint, amount int, source uint, sourceKind uint] | `[10u, 30, 5u, 0u]` | weapon.FireSystem.TryReadDamage |
| `enemy_damage_args_blast` | 同上（sourceKind=1） | `[10u, 80, 5u, 1u]` | weapon.ProjectileSystem |
| `enemy_slow_args` | [target uint, speedMul float, duration float] | `[10u, 0.6f, 1.5f]` | weapon（冰弹） |
| `enemy_get_all_row` | [id uint, x float, y float, z float, kind uint, alive bool, hp int, maxHp int] | `[10u, 1f, 0f, 2f, 3u, true, 500, 500]` | weapon.IceBullet |
| `enemy_get_count_row` | [count int] | `[7]` | ui.HudBinders |
| `enemy_set_spawning_args` | [enabled bool] | `[true]` | game.GameFlow |

消息载荷向量（手工构造，owner 消息编码器产出 bytes）：

| ContractId | 字段表 | 样例 |
|---|---|---|
| `enemy_killed_msg` | 1=enemyType(uint) 2=scoreValue(int) 3..5=xyz(float) | `[6u, 500, 1f, 0f, 2f]` |
| `enemy_count_msg` | 1=count(int) | `[7]` |
