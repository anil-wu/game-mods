# com.fps.mapgen 契约文档（Rule 19：契约 = 文档）

## 1. 网络协议

### mapgen:seed v1（ServerToClient 广播）
| field | 类型 | 语义 |
|---|---|---|
| 1 | i32 (varint zigzag) | seed：确定性生成种子 |

## 2. 生成算法（确定性，两端一致）

障碍物布局由 `GenerateBoxes(seed)` 确定性生成（LCG 伪随机，无跨端数据复制）：
- 10 个立方体障碍物，位置/尺寸由 seed 派生；
- 同 seed 两端生成完全一致（契约的关键：算法与常量随文档发布，改动需升协议版本）。

## 3. 遗留
- 障碍物碰撞 / 挡弹（raycast 阻塞）：待 mapgen 导出 `mapgen:raycast_blocked` 能力供 weapon 调用。
