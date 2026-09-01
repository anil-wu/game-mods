# com.zombtoy.item 契约文档（Rule 19：契约 = 文档）

> 定位：道具——血瓶/弹药箱定时生成与拾取。单机 Host。线格式约定同 summary §0。
> 拾取链路：ItemView OnTriggerEnter(玩家) → 经静态桥调本 Mod `item:pickup` 能力 → 能力内 ModCall `player:heal` / `weapon:add_ammo` → 消耗并发 `item:PickedUp`。

---

## 1. 枚举（编号即契约，u32）

```csharp
public enum ItemKind : byte { HealthPotion=0, AmmoBox=1 }
```

## 2. ECS 组件（仅本 Mod 读写）

```csharp
public struct ItemData     : IComponent { public byte Kind; public int Amount; }  // 血瓶 Amount=治疗量；弹药箱 Amount=每槽+弹匣数
public struct ItemState    : IComponent { public bool Picked; }                   // 已拾取（防重复触发）
public struct ItemPosition : IComponent { public float X, Y, Z; }                 // 生成点
public struct ItemSpawner  : IComponent { public bool Enabled; public float Timer; public int ActiveCount; public int MaxItems; public int NextSpawnIndex; } // 单实体
```

## 3. ECS 系统

| 序 | 系统 | 职责 | 读 | 写 | 消息 |
|---|---|---|---|---|---|
| 1 | `ItemSpawnSystem` | 定时生成：Enabled 且 ActiveCount<MaxItems 且玩家存活 → 首延迟 10s、之后随机 [5,15]s 间隔 → 按 NextSpawnIndex 轮转出生点（ItemPosition 自持配置）→ 创建实体 + 视图 | ItemSpawner, ItemPosition | ItemSpawner | — |

拾取无独立系统：`item:pickup` 能力实现内同步完成（调用方需要 applied 结果，ModCall 语义）。

## 4. 导出能力（ModCall，owner = com.zombtoy.item）

### item:pickup —— 拾取入口（视图触发）
- args：`[itemEntityId uint]`
- 返回：`[applied bool, kind uint]`
- 语义：实体不存在/已 Picked → `[false, 0]`；按 Kind 分派：
  - HealthPotion(0)：调 `player:heal([Amount])`（返回 applied）；Score 不加分（对齐原版 HealthPotion 不加分——实际原版 AddScore(3)，见备注①）；
  - AmmoBox(1)：调 `weapon:add_ammo([Amount])`；
  - 成功后置 Picked、销毁实体+视图、发 `item:PickedUp`。
- 备注①：原版 HealthPotion 拾取给 +3 分、AmmoItem +1 分——**计分统一归 score 逻辑，本 Mod 不跨 Mod 加分**；如需保留该体验，由 score 订阅 `item:PickedUp` 加分（v1 不做，见 §5 备注）。

### item:set_spawning —— 开/关生成（game 相位控制）
- args：`[enabled bool]`
- 返回：`[ok bool]`

### item:reset —— 新对局重置（game:start 调用）
- args：`[]`
- 返回：`[ok bool]`
- 语义：销毁全部道具实体+视图、ActiveCount=0、NextSpawnIndex=0、Timer=首延迟。

## 5. 发布/订阅消息（谁定义谁发布）

### item:PickedUp v1
| field | 类型 | 语义 |
|---|---|---|
| 1 | uint (varint) | kind（ItemKind 编号） |
| 2 | int (varint) | amount |

订阅者：ui（可选拾取提示）。备注：v1 不发布计分语义，score 不订阅本消息（如需"拾取+分"体验，score 订阅并加对应分值，append-only 兼容）。

## 6. 依赖声明（mod.json）

```json
"dependencies": [
  { "id": "com.game.core", "version": ">=1.0.0" },
  { "id": "com.zombtoy.player", "version": ">=0.1.0" },
  { "id": "com.zombtoy.weapon", "version": ">=0.1.0" }
]
```

## 7. 消费的对端能力/消息

| 对端 | 能力/消息 | 用途 |
|---|---|---|
| player | `player:heal` | 血瓶治疗 |
| player | `player:get_state` | 拾取前存活判定（玩家死亡不生成/不拾取） |
| weapon | `weapon:add_ammo` | 弹药箱补弹 |

## 8. 视图约定

- `ItemView`：持有 `public uint EntityId`；旋转动画、OnTriggerEnter(玩家 tag) → 静态桥调 `item:pickup`；拾取音效、拾取后禁用 Collider 并销毁。
- `ItemSpawnerView`：按 SpawnSystem 通知实例化 prefab → 绑定 EntityId。

## 9. 行为参数（ItemConfig 常量表；默认值，资源迁移时按 prefab 校准）

| 参数 | 值 | 来源 |
|---|---|---|
| FirstDelay | 10s | ItemManager InvokeRepeating("Spawn", 10, ...) |
| Interval | random [5, 15]s | spawnTime 默认 5（prefab） |
| MaxItems | 3 | MaximumItemNumber（prefab） |
| HealthPotion.Amount | 30 | heal 值（prefab） |
| AmmoBox.Amount | 1 | 每槽 +1 满弹匣（AmmoItem 行为） |
| 出生点 | `ItemConfig.SpawnPoints` 常量数组 | 本 Mod 自持（与 game Level 场景标记同步，同 enemy §9） |

## 10. 测试向量（`src/ItemTestVectors.cs`）

| ContractId | 形状 | 样例 | 消费方解析器 |
|---|---|---|---|
| `item_pickup_args` | [itemEntityId uint] | `[10u]` | （视图/测试直调） |
| `item_set_spawning_args` | [enabled bool] | `[true]` | game.GameFlow |

消息载荷向量（手工构造）：

| ContractId | 字段表 | 样例 |
|---|---|---|
| `item_pickedup_msg` | 1=kind(uint) 2=amount(int) | `[0u, 30]` |
