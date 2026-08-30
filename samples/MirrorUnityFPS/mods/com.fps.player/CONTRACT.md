# com.fps.player 契约文档（Rule 19：契约 = 文档）

> 线格式统一：字段编号（u16 LE）+ wireType（u8）+ data；wireType：0=Varint（有符号 zigzag），1=Fixed32，2=Fixed64，3=LengthDelimited。append-only，未知字段跳过。

## 1. 网络协议

### player:input v1（ClientToServer）

全局 ID：`xxHash32("com.fps.player:input")`

| field | 类型 | 语义 |
|---|---|---|
| 1 | float (fixed32) | moveX：左右 [-1,1] |
| 2 | float (fixed32) | moveZ：前后 [-1,1] |
| 3 | float (fixed32) | yaw：朝向（度） |
| 4 | bool (varint) | jump：是否按跳 |

### player:local v1（ServerToClient 广播）

全局 ID：`xxHash32("com.fps.player:local")`

| field | 类型 | 语义 |
|---|---|---|
| 1 | u32 (varint) | networkId：本地玩家的 NetworkId |

## 2. 复制 Archetype

### com.fps.player:player（ArchetypeHash = xxHash32 同名字符串）

| index | 组件 | 字段 |
|---|---|---|
| 0 | Position3 | 1=X(f32) 2=Y(f32) 3=Z(f32) |
| 1 | Health | 1=Current(i32) 2=Max(i32) |
| 2 | PlayerTag | 1=IsDummy(bool) |

复制帧格式（Network.Mod 基础设施）：field1=NetworkId(u32)，field2=ArchetypeHash(u32)，
field10=组件块（`[typeIdx u8][len u16][bytes]*` 裸切片流）。全量 Snapshot@15Hz。

## 3. 导出能力（ModCall，owner = com.fps.player）

### player:get_position

- args：u32 entityId（0/null = 本地玩家）
- 返回：`PositionDto { EntityId u32, X/Y/Z f32, Yaw f32, Alive bool }`（快照，§12.9 规则 1）

### player:get_all_positions

- args：无
- 返回：`PositionDto[]`（含 Dummy；用于命中判定等）

### player:apply_damage

- args：`DamageArgs { Target u32, Amount i32, Source u32 }`
- 返回：bool（本次伤害是否致死）
- 语义：服务端权威扣血；致死时发布 `player:died` 事件并挂重生计时（3s 后满血回出生点）

## 4. 消息（MessageBus 事件）

### player:died v1（Event，无返回）

| field | 类型 | 语义 |
|---|---|---|
| 1 | u32 (varint) | target：死亡实体 |
| 2 | u32 (varint) | source：伤害来源实体 |
