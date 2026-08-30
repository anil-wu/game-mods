# com.fps.npc 契约文档（Rule 19：契约 = 文档）

## 1. 复制 Archetype：com.fps.npc:npc

| index | 组件 | 字段 |
|---|---|---|
| 0 | NpcPosition3 | 1=X 2=Y 3=Z (f32) |
| 1 | NpcHealth | 1=Current(i32) 2=Max(i32) |

## 2. 导出能力（ModCall，owner = com.fps.npc）

### npc:get_all_npcs
- args：无
- 返回：object[] 行集，行布局 `[EntityId u32, X f32, Y f32, Z f32, Alive bool]`

### npc:apply_damage
- args：`[target u32, amount i32, source u32]`
- 返回：bool（是否致死）；致死发布 `npc:died` 事件，5s 后重生回出生点

## 3. 消息（MessageBus 事件）
### npc:died v1：field1=target(u32) field2=source(u32)

## 4. 消费的对端能力
- `player:get_all_positions`：AI 目标选取（行布局见 com.fps.player/CONTRACT.md）
- `player:apply_damage`：攻击伤害

## 5. 行为参数（确定性，改动需升版本）
仇恨范围 8 / 攻击范围 1.6 / 移动 2.6 / 攻击伤害 15 / 攻击间隔 1s / 重生 5s / NPC 100 血。
