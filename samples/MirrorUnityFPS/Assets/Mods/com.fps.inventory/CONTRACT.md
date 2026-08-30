# com.fps.inventory 契约文档（Rule 19：契约 = 文档）

## 1. 网络协议

### inventory:pickup v1（ClientToServer）
| field | 类型 | 语义 |
|---|---|---|
| 1 | u32 (varint) | netId：拾取物 NetworkId |

服务端校验：实体存在 / 拾取者 2.5m 内 → 施加效果 → 广播 picked → Despawn+销毁。

### inventory:picked v1（ServerToClient 广播）
| field | 类型 | 语义 |
|---|---|---|
| 1 | u32 | netId |
| 2 | byte | kind：0=治疗 1=弹药 |
| 3 | i32 (zigzag) | amount |

## 2. 复制 Archetype：com.fps.inventory:item
| index | 组件 | 字段 |
|---|---|---|
| 0 | ItemPosition3 | 1=X 2=Y 3=Z (f32) |
| 1 | ItemTag | 1=Kind(byte) 2=Amount(i32) |

## 3. 消费的对端能力
- `player:get_position`：取拾取者实体/位置（args=null 或 u32 entityId → 行 [id,x,y,z,yaw,alive]）
- `player:heal`：args=[target u32, amount i32] → bool（治疗）
- `weapon:add_ammo`：args=[target u32, amount i32] → bool（补弹，懒挂 WeaponState）

## 4. 遗留
- 拖拽背包 UI / 武器槽：待 UI.Mod 接 UGUI（阶段遗留，见案例 README）
