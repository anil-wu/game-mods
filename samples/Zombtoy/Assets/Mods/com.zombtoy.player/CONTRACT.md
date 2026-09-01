# com.zombtoy.player 契约文档（Rule 19：契约 = 文档）

> 定位：玩家——移动/转身/生命/体力/冲刺/死亡/相机。单机 Host（HasClient+HasServer）。
> 本 Mod **不消费**任何跨 Mod 能力/消息，只被其他 Mod 调用/订阅（依赖最少的叶子 Mod）。
> 线格式约定（全仓库统一，见 `design/zombtoy-contracts-summary.md` §0）：
> - 字段编号（u16 LE）+ wireType（u8）+ data；0=Varint（有符号 zigzag），1=Fixed32，2=Fixed64，3=LengthDelimited。append-only，未知字段跳过。
> - **ModCall 参数/返回一律 DataCodec**（Rule 14）：`object?[]` 纯数据元组（字段布局即契约，同 FPS 先例），bytes 不藏引用。
> - **Message 载荷用 PayloadWriter 字段编号格式**。
> - 枚举只传编号（u32 varint），数字即契约；各 Mod 各自重复定义同名枚举（Rule 19 可重复定义）。

---

## 1. ECS 组件（仅本 Mod 读写，其他 Mod 禁止触碰 World）

```csharp
// 玩家主实体（Register 时创建，常驻；game:start 经 player:reset 重置，见 §3）
public struct Position3     : IComponent { public float X, Y, Z; }        // 世界坐标（逻辑权威；视图同步 Transform）
public struct Velocity3     : IComponent { public float X, Y, Z; }        // 期望速度（逻辑；视图驱动 Rigidbody.MovePosition）
public struct Facing        : IComponent { public float Yaw; }            // 朝向（度，绕 Y；鼠标射线打 Floor 得到）
public struct PlayerHealth  : IComponent { public int Current; public int Max; }   // 生命，Current<=0 = 死亡
public struct Stamina       : IComponent { public float Current; public float Max; } // 体力 [0,1]
public struct PlayerInput   : IComponent { public float MoveX; public float MoveZ; public bool Sprint; } // 视图写入（WASD+Shift）
public struct PlayerFlags   : IComponent { public bool IsDead; }          // 死亡标记（Enemy 追击/Item 拾取的判断依据）
```

无复制标签（单机，Rule：`[NetworkReplicated]` 不需要）。组件类型在 `Register` 里 `context.Ecs.RegisterComponent` 注册，实体 `context.Ecs.CreateEntity()` 创建（归属本 ModObject，卸载强制回收 §9.2）。

## 2. ECS 系统（职责 / 读写组件 / 更新顺序）

| 序 | 系统 | 职责 | 读 | 写 | 发布 |
|---|---|---|---|---|---|
| 1 | `PlayerInputApplySystem` | 输入合成速度：MoveX/Z 归一化 × 速度；Sprint 且体力>0 → ×1.2 | PlayerInput, PlayerFlags, Stamina | Velocity3 | — |
| 2 | `PlayerMovementSystem` | 积分位移：`Position += Velocity * dt`（逻辑权威；视图每帧读 Position3 同步 Transform） | Velocity3 | Position3 | — |
| 3 | `PlayerStaminaSystem` | 冲刺耗体力 0.5/s，非冲刺恢复 0.15/s（≤Max 封顶）；体力耗尽强制取消冲刺（写回 PlayerInput.Sprint=false） | PlayerInput, PlayerFlags | Stamina, PlayerInput | `player:StaminaChanged`（变化时） |

注册：`SystemSide.Server`（Host 单机 = 全逻辑端；无头测试用 `UpdateAll`）。

**伤害/治疗不走系统**：`player:damage` / `player:heal` 在能力实现内**同步**修改 PlayerHealth 并发布消息、同步返回 bool（调用方需要当场拿到结果，ModCall 语义，§12.11）。系统只做周期行为（移动/体力）。

## 3. 导出能力（ModCall，owner = com.zombtoy.player）

> args/返回均为 DataCodec 编码的 `object?[]`；元素类型严格按下表（`uint`=UInt，`int`=Int，`float`=Float，`bool`=Bool）。

### player:damage —— 敌人接触伤害入口
- args：`[amount int, source uint]`（source=伤害来源实体，0=环境）
- 返回：`[killed bool]`
- 语义：IsDead 或 amount<=0 → 返回 `[false]` 不处理；扣血 `Current=max(0, Current-amount)`；发布 `player:HealthChanged`；Current==0 → 置 IsDead、发布 `player:Died`、返回 `[true]`。

### player:heal —— 道具血瓶入口
- args：`[amount int]`
- 返回：`[applied bool]`
- 语义：IsDead 或 amount<=0 → `[false]`；满血时**仍返回 [true] 但不扣减**（对齐原版：血瓶照常消耗，治疗量 0 溢出丢弃）；`Current=min(Max, Current+amount)`；发布 `player:HealthChanged`。

### player:reset —— 新对局重置（game:start 调用）
- args：`[x float, y float, z float]`（出生点）
- 返回：`[ok bool]`
- 语义：满血/满体力/清 IsDead/重置 Position3/Facing=0/Velocity3=0；发布 `player:HealthChanged` + `player:StaminaChanged`。

### player:get_entity —— 敌人/武器/道具定位玩家
- args：`[]`
- 返回：`[entityId uint]`（0 = 未生成）

### player:get_state —— HUD 初始填充 / 敌人判定
- args：`[]`
- 返回：`[health int, max int, stamina float, maxStamina float, alive bool]`

### player:get_position —— 敌人追击目标点
- args：`[]`
- 返回：`[x float, y float, z float, alive bool]`

## 4. 发布/订阅消息（MessageBus 事件，谁定义谁发布）

### player:HealthChanged v1
| field | 类型 | 语义 |
|---|---|---|
| 1 | int (varint) | current |
| 2 | int (varint) | max |

订阅者：score（死亡结算）、ui（HUD 血条/红屏）。

### player:StaminaChanged v1
| field | 类型 | 语义 |
|---|---|---|
| 1 | float (fixed32) | current |
| 2 | float (fixed32) | max |

订阅者：ui（HUD 体力条）。

### player:Died v1
| field | 类型 | 语义 |
|---|---|---|
| 1 | uint (varint) | source：伤害来源实体 |

订阅者：score（→ 结算 + `score:GameOver`）、ui（死亡表现）。

## 5. 依赖声明（mod.json）

```json
"dependencies": [ { "id": "com.game.core", "version": ">=1.0.0" } ]
```

## 6. 消费的对端能力/消息

**无。** 本 Mod 是纯能力提供方 + 消息发布方。

## 7. 测试向量（owner 发布，§14.11.3；消费方 CI 校验）

文件：`src/PlayerTestVectors.cs`，`public static class PlayerTestVectors { public static TestVector[] All(); }`。

| ContractId | 形状（DataCodec） | 样例 | 消费方解析器（各自重复定义） |
|---|---|---|---|
| `player_damage_args` | [amount int, source uint] | `[25, 7u]` | enemy.EnemyAttackSystem.TryReadDamage |
| `player_heal_args` | [amount int] | `[20]` | item.ItemPickup.TryReadHeal |
| `player_reset_args` | [x float, y float, z float] | `[0f, 1f, 0f]` | game.GameFlow |
| `player_get_state_row` | [health int, max int, stamina float, maxStamina float, alive bool] | `[100, 100, 1f, 1f, true]` | ui.HudBinders、enemy.EnemyChaseSystem |
| `player_get_position_row` | [x float, y float, z float, alive bool] | `[0f, 1f, 0f, true]` | enemy.EnemyChaseSystem |

消息载荷向量（手工构造 `new TestVector(id, sample, bytes)`，bytes 由 owner 消息编码器产出；消费方用 PayloadReader 解析）：

| ContractId | 字段表 | 样例 |
|---|---|---|
| `player_health_msg` | 1=current(int) 2=max(int) | `[95, 100]` |
| `player_stamina_msg` | 1=current(float) 2=max(float) | `[0.4f, 1f]` |
| `player_died_msg` | 1=source(uint) | `[7u]` |

消费方一致性校验统一在 `tests/TestRunner/ZombtoyTestVectorTests.cs`（见 summary §6）：对同一 Bytes 解析出与 Sample 一致的结果。

## 8. 视图约定（MonoBehaviour 表现层，不参与无头测试）

- `PlayerView`：玩家模型/动画；持有 `public uint EntityId`；每帧读 Position3/Facing 同步 Transform。
- `PlayerInputView`：WASD+Shift → 写 PlayerInput；鼠标射线打 Floor 层 → 写 Facing。
- `CameraFollowView`：等距跟随（对齐原版 CamerFollow）。
- 实体↔视图映射通用模式见 summary §5（视图挂 EntityId，射线命中 collider → 取 EntityId → 写请求组件 → 系统内 ModCall）。

## 9. 行为参数（PlayerConfig 常量表，改动需升版本）

| 参数 | 值 | 来源 |
|---|---|---|
| MoveSpeed | 6.0 | PlayerMovement.cs speed=6f |
| SprintMultiplier | 1.2 | PlayerHealth sprint_Speed = speed*1.2 |
| StaminaDrainPerSec | 0.5 | PlayerHealth Stamina -= dt*0.5 |
| StaminaRegenPerSec | 0.15 | PlayerHealth Stamina += dt*0.15 |
| MaxHealth | 100 | PlayerHealth startingHealth |
| MaxStamina | 1.0 | PlayerHealth Stamina=1f |
| SpawnPoint | (0, 1, 0) | 默认出生点（game:start 传入） |
