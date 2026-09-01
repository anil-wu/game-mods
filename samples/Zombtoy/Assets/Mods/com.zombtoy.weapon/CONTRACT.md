# com.zombtoy.weapon 契约文档（Rule 19：契约 = 文档）

> 定位：武器——4 槽（机关枪/霰弹/火箭筒/冰手枪）+ 副手龙卷风 + 弹药/换弹/切换 + 投射物（M2）。
> 单机 Host。线格式约定同 summary §0。武器槽位实体在 Register 创建（常驻），玩家实体经 `player:get_entity` 关联。
> 核心链路（任务通道 weapon→enemy）：FireView 射线命中 collider → 取 EnemyView.EntityId → 写 ShotRequest → FireSystem 校验 → ModCall `enemy:damage`。

---

## 1. 枚举（编号即契约，u32）

```csharp
public enum WeaponKind : byte { MachineGun=0, Shotgun=1, RocketLauncher=2, CryoPistol=3, Tornado=4 } // Tornado=副手特殊能力，非槽位
public enum SourceKind  : byte { Default=0, Blast=1, Ice=2 }   // 伤害来源分类（与 enemy CONTRACT 一致，重复定义）
```

## 2. ECS 组件（仅本 Mod 读写）

```csharp
public struct WeaponSlot   : IComponent { public byte Index; public byte Kind; }            // 槽位实体（×4）
public struct WeaponAmmo   : IComponent { public int InMag; public int Reserve; public int MaxMag; public bool IsReloading; public float ReloadTimer; public float FireCooldown; }
public struct WeaponActive : IComponent { public byte CurrentIndex; public byte Count; }     // 单实体：当前选中槽
public struct WeaponOwner  : IComponent { public uint PlayerEntityId; }                      // 武器域 → 玩家（Register 时经 player:get_entity）
public struct ShotRequest  : IComponent { public uint ShooterId; public uint HitEntityId; public bool IsHit; public float HitX, HitY, HitZ; public byte Kind; } // 视图写入、FireSystem 消费（单槽，帧末清空）
public struct SwitchRequest: IComponent { public byte Index; }                               // 视图写入（1-4 键）
public struct Projectile   : IComponent { public byte Kind; public uint OwnerId; public float X, Y, Z; public float DirX, DirY, DirZ; public float Damage; public float Life; public byte State; } // M2：Rocket/IceBullet/Tornado 逻辑实体
```

## 3. ECS 系统（职责 / 读写组件 / 更新顺序）

| 序 | 系统 | 职责 | 读 | 写 | 跨 Mod / 消息 |
|---|---|---|---|---|---|
| 1 | `FireSystem` | 消费 ShotRequest：校验 FireCooldown≤0 + 弹药>0 + 非换弹 + 玩家存活 → 扣弹（AmmoPerShot，霰弹=5）→ 冷却计时 → **调 `enemy:damage`** → 发 `weapon:AmmoChanged`；清空 ShotRequest | ShotRequest, WeaponAmmo, WeaponActive, WeaponSlot | WeaponAmmo | 调 `enemy:damage` |
| 2 | `ReloadSystem` | 换弹计时：IsReloading → 到点从 Reserve 补弹至 MaxMag（Reserve 不足全补）→ 清 IsReloading → 发 `weapon:AmmoChanged` | WeaponAmmo | WeaponAmmo | — |
| 3 | `WeaponSwitchSystem` | 消费 SwitchRequest：校验槽位范围/非换弹中 → 更新 WeaponActive → 发 `weapon:WeaponSwitched` | SwitchRequest, WeaponActive, WeaponSlot | WeaponActive | — |
| 4 | `ProjectileSystem`（M2） | 投射物逻辑更新：Rocket 直线飞行+爆炸（内圈/外圈 AOE 各调一次 `enemy:damage`，sourceKind=Blast）；IceBullet 追踪（按 `enemy:get_all` 选最近目标，命中调 `enemy:damage` sourceKind=Ice + `enemy:apply_slow`）；Tornado 持续伤害 tick（sourceKind=Default）+ 生命倒计时 | Projectile, WeaponAmmo | Projectile | 调 `enemy:damage` / `enemy:apply_slow` / `enemy:get_all` |

**伤害只发生在系统内**（FireSystem/ProjectileSystem），能力层不直接扣敌人血——所有跨 Mod 伤害调用可被无头测试覆盖。

换弹触发：视图检测到弹药=0 或按 R 键 → 经静态桥设置 `WeaponAmmo.IsReloading=true` 并写 `ReloadTimer=ReloadTime`（ReloadSystem 计时完成后补弹；弹药=0 时自动进入换弹由视图负责，与 ReloadSystem 无耦合）。

## 4. 导出能力（ModCall，owner = com.zombtoy.weapon）

### weapon:add_ammo —— 弹药箱拾取入口（item 调用）
- args：`[magazines int]`（≤0 或缺省 = 每槽 +1 满弹匣）
- 返回：`[applied bool]`
- 语义：遍历 4 槽，`Reserve=min(MaxReserve, Reserve+MaxMag*magazines)`（MaxReserve 见 config）；发 `weapon:AmmoChanged`×槽。

### weapon:reset —— 新对局重置（game:start 调用）
- args：`[]`
- 返回：`[ok bool]`
- 语义：全部槽位弹药/冷却/换弹复位默认值、CurrentIndex=0、清 ShotRequest/SwitchRequest；发 `weapon:AmmoChanged` + `weapon:WeaponSwitched`。

### weapon:get_state —— HUD 弹药初始填充
- args：`[]`
- 返回：`[slot uint, slotCount uint, inMag int, reserve int, maxMag int]`

## 5. 发布/订阅消息（谁定义谁发布）

### weapon:AmmoChanged v1 —— HUD 弹药
| field | 类型 | 语义 |
|---|---|---|
| 1 | uint (varint) | slot（槽位索引） |
| 2 | int (varint) | inMag |
| 3 | int (varint) | reserve |
| 4 | int (varint) | maxMag |

订阅者：ui（HUD 弹药文本 "inMag/reserve"）。

### weapon:WeaponSwitched v1 —— HUD 武器名
| field | 类型 | 语义 |
|---|---|---|
| 1 | uint (varint) | slot |

订阅者：ui（GunText 显示武器名/字号）。

## 6. 依赖声明（mod.json）

```json
"dependencies": [
  { "id": "com.game.core", "version": ">=1.0.0" },
  { "id": "com.zombtoy.player", "version": ">=0.1.0" },
  { "id": "com.zombtoy.enemy", "version": ">=0.1.0" }
]
```

## 7. 消费的对端能力/消息

| 对端 | 能力/消息 | 用途 |
|---|---|---|
| player | `player:get_entity` | Register 时关联武器域 → 玩家（写 WeaponOwner） |
| enemy | `enemy:damage` | 命中伤害（FireSystem/ProjectileSystem，sourceKind 区分） |
| enemy | `enemy:apply_slow`（M2） | 冰弹缓速 |
| enemy | `enemy:get_all`（M2） | 冰弹追踪选目标 |

## 8. 视图约定

- `FireView`：Fire1 按下 → Physics.Raycast（Shootable 层，Range）→ 命中 collider → `GetComponentInParent<EnemyView>().EntityId`（未命中=0）→ 经静态桥写 ShotRequest（含命中点）；枪口粒子/LineRenderer/音效/枪光表现；换弹 R 键 → 写换弹请求。
- `WeaponView`：枪模切换显示（1-4 键 → SwitchRequest）、枪口特效、命中贴花（池归本 Mod，Rule 3）。
- `ProjectileView`（M2）：Rocket/IceBullet/Tornado 实例化/移动/爆炸特效；爆炸免疫由 enemy 侧裁决（sourceKind=Blast）。
- Tornado 副手：Fire2 → 写 TornadoRequest → ProjectileSystem 生成 Tornado 实体（M2）；UI 冷却条经 `weapon:get_state` 扩展（M2 追加字段）。

## 9. 行为参数（WeaponConfig 常量表；默认值，资源迁移时按 prefab 校准）

| slot | 武器 | Damage | Interval | Range | MagSize | StartReserve | ReloadTime | AmmoPerShot | 备注 |
|---|---|---|---|---|---|---|---|---|---|
| 0 | MachineGun | 60 | 0.015s | 100 | 25 | 50 | 1.5s | 1 | 射线（PlayerShooting 原值 60/0.015/100） |
| 1 | Shotgun | 10×5 | 0.45s | 100 | 8 | 24 | 1.0s | 5（5 射线各 10） | 每枪 5 条射线 |
| 2 | RocketLauncher | 直击 100 / 内圈 80@3m / 外圈 40@6m | 1.0s | 60 | 1 | 6 | 2.0s | 1 | 爆炸，sourceKind=Blast；Titan 免疫 |
| 3 | CryoPistol | 30 | 0.4s | 80 | 10 | 40 | 1.2s | 1 | 追踪弹 + 缓速 40%/1.5s（M2） |
| 4（副手） | Tornado | 8/tick(0.2s) + 外圈 2 | — | 4/10m | — | — | — | — | 拉拽 + 持续伤害，CD 30s（M2） |

## 10. 测试向量（`src/WeaponTestVectors.cs`）

| ContractId | 形状 | 样例 | 消费方解析器 |
|---|---|---|---|
| `weapon_add_ammo_args` | [magazines int] | `[1]` | item.ItemPickup |
| `weapon_reset_args` | [] | `[]` | game.GameFlow |
| `weapon_get_state_row` | [slot uint, slotCount uint, inMag int, reserve int, maxMag int] | `[0u, 4u, 25, 50, 25]` | ui.HudBinders |
| `weapon_shot_request` | [shooter uint, hit uint, isHit bool, hitX float, hitY float, hitZ float, kind uint] | `[1u, 10u, true, 0f, 0f, 2f, 0u]` | （本 Mod 内部测试） |

消息载荷向量（手工构造）：

| ContractId | 字段表 | 样例 |
|---|---|---|
| `weapon_ammo_msg` | 1=slot(uint) 2=inMag(int) 3=reserve(int) 4=maxMag(int) | `[0u, 25, 50, 25]` |
| `weapon_switched_msg` | 1=slot(uint) | `[1u]` |
