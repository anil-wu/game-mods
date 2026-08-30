# com.fps.weapon 契约文档（Rule 19：契约 = 文档）

> 线格式统一：字段编号（u16 LE）+ wireType（u8）+ data；0=Varint（有符号 zigzag），1=Fixed32，2=Fixed64，3=LengthDelimited。append-only。

## 1. 网络协议

### weapon:fire v1（ClientToServer）

全局 ID：`xxHash32("com.fps.weapon:fire")`

| field | 类型 | 语义 |
|---|---|---|
| 1 | float (fixed32) | yaw：开火朝向（度） |

语义：服务端权威开火——原点取服务端射手位置（防伪造），冷却/弹药/换弹校验，
命中 ray-sphere（半径 0.6，射程 30，伤害 34），结果经 `player:apply_damage` 施加。

### weapon:reload v1（ClientToServer）

空载荷。服务端校验后进入 1s 换弹，完成回满弹药并广播 weapon:state。

### weapon:shot v1（ServerToClient 广播）

| field | 类型 | 语义 |
|---|---|---|
| 1 | u32 (varint) | shooterNetId：射手 NetworkId |
| 2 | u32 (varint) | hitNetId：被命中者 NetworkId（0=未命中） |
| 3..5 | float (fixed32) | 命中点（未命中 = 弹道终点） |
| 6 | bool (varint) | killed：本次是否击杀 |

### weapon:state v1（ServerToClient 广播）

| field | 类型 | 语义 |
|---|---|---|
| 1 | u32 (varint) | netId：所属玩家 NetworkId |
| 2 | i32 (varint zigzag) | ammo |
| 3 | i32 (varint zigzag) | maxAmmo |
| 4 | bool (varint) | reloading |

## 2. 消息（MessageBus 事件）

### weapon:shot v1（Event，无返回，owner = com.fps.weapon）

| field | 类型 | 语义 |
|---|---|---|
| 1 | u32 (varint) | shooter：射手实体 |
| 2 | u32 (varint) | hit：被命中实体（0=未命中） |
| 3 | bool (varint) | killed |

## 3. 消费的对端能力（com.fps.player，见其 CONTRACT.md）

| 能力 | 用途 |
|---|---|
| `player:get_position` | 取本地射手实体 / 弹道起点（args=null 或 u32 entityId） |
| `player:get_all_positions` | 懒挂 WeaponState + 命中判定（返回 object[] 行集） |
| `player:apply_damage` | 命中伤害（args=`[target u32, amount i32, source u32]`，返回 bool=致死） |

**能力 ABI 形状**：BCL 纯数据（基元 + object[] 元组，字段布局即契约），双方各自按文档解析，零自定义类型共享。
