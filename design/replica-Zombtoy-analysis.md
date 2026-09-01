# Zombtoy 样例复刻可行性分析

> 样例：`replica_projects/Zombtoy`（Unity 3D 僵尸生存，Survival-Shooter 衍生，单机 + .NET 8 高分榜后端）
> 问题：能否用 V2.0 框架（`design/Unity_Mod_Runtime_架构设计V2.0.md`）复刻？
> 结论：**可以，且比 FPS 复刻简单——Zombtoy 是单机游戏，无需 Replication/Network 组网；主要考验 ECS + Messaging + UI + ModCall + 商店。**
> 复刻规范：见 [`replica-conventions.md`](replica-conventions.md)（Unity 2022.3.62f3 / 产物在 `samples/Zombtoy/` / Mod 工程拆分）。

---

## 1. 样例工程解剖

### 1.1 功能清单

| 功能 | 实现方式 | 源码（`Assets/Scripts/`） |
|---|---|---|
| 玩家移动/转身 | WASD + Rigidbody.MovePosition，鼠标射线打 Floor 层旋转 | `Player/PlayerMovement.cs`（60 行） |
| 生命/体力/冲刺 | 生命 100、体力条、Shift 冲刺（加速+耗体力）、死亡 | `Player/PlayerHealth.cs`（234 行，god-object） |
| 射击（射线枪） | 射线检测 Shootable 层，命中 `EnemyHealth.TakeDamage` | `Player/PlayerShooting.cs`（130 行，挂在枪 prefab） |
| 武器切换 | 数据驱动 `WeaponEntry` 列表，1-4 键切换 4 槽 | `Inventory.cs`（135 行，已重构） |
| 弹药系统 | 每枪 `Ammo.cs`（弹匣/备弹/换弹协程），`TryShoot/CanReload` | `Ammo.cs`（115 行，×9 实例） |
| 武器类型 | 机关枪/多发/霰弹（射线）+ 冰手枪（IceBullet 追踪弹）+ 火箭筒（Rocket 爆炸范围）+ 龙卷风（Tornado 拉拽） | `Pistol/RocketLauncher/IceBullet/TornadoLaunch/Rocket/Tornado.cs` |
| 敌人类型 | 僵尸兔/僵尸熊/大象 + 巨型变体 + Titan 首领（地面锁定攻击）+ 小丑（远程）+ 迷你小丑 + 爆炸鸭 | 根目录 prefab + `Enemy/*` + 支持脚本 |
| 敌人 AI | NavMesh 追玩家 + 接触伤害 + 远程射击 | `Enemy/EnemyMovement.cs`、`Enemy/EnemyAttack.cs`、`EnemyShooting.cs`、`EnemyTargetShooting.cs` |
| 敌人生成 | 加权生成表 + 波次/冷却/并发上限（50） | `Managers/EnemyManager.cs`（622 行） |
| 分数/最高分 | 击杀 100/首领 500/连击 ×2，持久化最高分 | `Managers/ScoreManager.cs`（352 行） |
| 道具掉落 | 弹药/血瓶定时生成 | `Managers/ItemManager.cs`（61 行）+ `AmmoItem/HealthPotion.cs` |
| 后端高分榜 | .NET 8 Minimal API（`POST /addScore`、`GET /getAllScores`）+ HttpClient 客户端 | `Server/Leaderboard.cs` + `Backend/ZombtoyBackend/` |
| UI/菜单 | 主菜单/设置/排行榜/结算 + HUD（血量/体力/分数/僵尸数/弹药） | `UI/*`、根目录 `Pause/Keybinds/Sensitivity` 等 |
| 相机 | 等距（`CamerFollow`）+ 第一人称（`CamerPOV/FirstPersonMovement`，未完成） | 根目录相机脚本 |

### 1.2 架构风格（典型「静态单例 + 全局查找」单体）

- **静态事件总线**：`GameEvents`（静态类，~20 事件：生命/分数/敌人生命周期/游戏状态），legacy 与新代码都通过它解耦。
- **单例管理器**：`Singleton<T>` 基类（`ScoreManager`/`EnemyManager`/`ItemManager`/`MusicManager`），场景摆放才激活——**不自动创建**。
- **遗留耦合**：`PlayerShooting` 直接 `aim.collider.GetComponent<EnemyHealth>().TakeDamage(...)`；55 处 `GameObject.Find`；`PlayerHealth.Death()` 里 `GameObject.Find("Fill")` 改 UI。
- **全局可达、无法隔离、无法热更**——正是 V2.0 §0 要消解的结构。

### 1.3 与 FPS 样例的本质差异

| 维度 | MirrorUnityFPS | Zombtoy |
|---|---|---|
| 联机 | 是（Mirror，SyncVar/RPC/权威服务端） | **否（单机）** |
| 需要 Replication | **是（硬前置）** | **否** |
| 需要 Network.Mod 组网 | 是 | 否（仅高分榜 HTTP 后端） |
| 主要考验 | ECS Replication + 网络分层 | **ECS + Messaging + UI + ModCall + 商店** |
| 后端 | Relay + 会话 | .NET 8 高分榜（复用 mod_server 模式） |

---

## 2. 机制映射：样例 → V2.0 框架

| 样例机制 | V2.0 对应 | 现状 |
|---|---|---|
| `GameEvents`（静态事件总线，~20 事件） | **MessageBus v2（Pub/Sub，Event）**，谁定义谁发布 | ✅ 已实现（`Game.Messaging`） |
| `Singleton<T>` 管理器 | **Mod 服务**（Register 里注册到 `ModObject`，Unregister 对称撤销，Rule 20） | ✅ 已实现 |
| 跨对象直接调用（`enemyHealth.TakeDamage`） | **ModCall（二进制，Rule 14）**：weapon 调 enemy 导出的 `damage` 能力 | ✅ 已实现（`DataCodec` + `CapabilityHandler`） |
| 分数/生命/僵尸数 → UI 更新 | **Message 订阅**：player/enemy/score 发布，UI Mod 订阅（`XxxBinder` 模式已存在） | ✅ 已实现 |
| 玩家状态（生命/体力/弹药） | **ECS**：player/weapon/enemy Mod 各自拥有组件 + System | ✅ 已实现（`Game.ECS`） |
| NavMesh 追敌 / 射线射击（Unity API） | **Mod 表现层 MonoBehaviour 视图**（PushBoxView 模式已验证） | ✅ 已验证 |
| 敌人 prefab ↔ ECS 实体 | **视图挂 EntityId 映射**，射线命中 collider → 取实体 → ModCall | ✅ 模式可用 |
| 后端高分榜（dreamlo + .NET） | **leaderboard Mod**（HttpClient → .NET 8 后端），或复用 mod_server | ⚠️ 需建 Mod |
| 菜单/HUD/结算 UI | **UI.Mod WindowManager + 业务 Mod 出 View** | ⚠️ **UI↔UGUI 绑定未接**（关键缺口） |
| 资源（模型/动画/材质） | **Mod 自治 `context.Resources`**（Rule 3） | ⚠️ **Unity 资源后端未接**（关键缺口） |

---

## 3. 可行性结论

### 3.1 架构层面：完全匹配，且比 FPS 更轻

Zombtoy 是单机游戏，**不需要 Replication 与 Network 组网**（FPS 复刻已把这些做完了，这里用不上）。它需要的是框架的**单机路径**：

- **ECS**：玩家（生命/体力）、敌人（生命/AI）、武器（弹药）各自的组件+System，落在各自 Mod 的 World 里。
- **Messaging**：取代 `GameEvents`——`EnemyKilled`/`PlayerDeath`/`ScoreChanged`/`HealthChanged` 都是 Event 消息。
- **ModCall**：取代跨对象直接调用——weapon→enemy 伤害、enemy→player 接触伤害，都走二进制能力调用。
- **UI.Mod**：菜单/HUD/结算，窗口声明给 UI.Mod，业务 Mod 出 View。
- **leaderboard Mod**：高分榜客户端，对接 .NET 8 后端（后端可沿用，或映射到 mod_server 的下载/注册模式）。

### 3.2 关键缺口（按优先级）

| # | 缺口 | 影响 | 说明 |
|---|---|---|---|
| 1 | **UI.Mod ↔ UGUI 绑定** | 菜单/HUD/血条/分数/弹药无法落地 | WindowManager 注册表已有，需把窗口实例挂到 Canvas 层级 + UGUI 组件绑定 |
| 2 | **Unity 资源后端**（Bundle → IResourceBackend） | 模型/动画/材质无法按 Mod 加载 | 文件后端已有，Unity 后端需接；FPS 复刻已用 ModPacker 打包 Bundle，可复用 |
| 3 | （可选）ECS 视图↔实体绑定规范 | 射线命中 collider → EntityId → ModCall 的惯用写法 | 已有 PushBox 先例，沉淀为约定即可 |

> ECS Replication、网络分层、ModCall 二进制、契约测试向量——FPS 复刻阶段已全部就绪，Zombtoy **无需新增框架能力**，主要工作量在「游戏逻辑按 Mod 重写 + UI 绑定 + 资源后端」。

### 3.3 非代码差异

- 样例 Unity **2022.3.37f1**，复刻规范固定 **2022.3.62f3**——版本兼容，无 API 迁移问题（比 FPS 的 Unity 6 更省事）。
- 样例有**未提交的阻塞 bug**（`Rocket.cs` 里 `using UnityEditor;` 破坏玩家构建、Titan 的 `groundTarget` 绑定错把整个地板当准星导致转向冻结）——复刻时**不搬运这些 bug**，机制等价即可。
- **资源必须用原项目资源**（模型/动画/材质/纹理/音频），按 Mod 领域拆分打包（见 §4.1 资源映射），不造占位资源；代码重写，但资源搬用。

---

## 4. 复刻 Mod 划分（映射 §15.4；落地 `samples/Zombtoy/mods/`）

```
samples/Zombtoy/
├── README.md                      对标功能清单 + 进度 + 已知差异
└── mods/
    ├── com.zombtoy.player       玩家：移动/转身/生命/体力/冲刺/死亡/相机
    │   ├── Shared:  PlayerState/Stamina 组件、damage/heal 协议（+CONTRACT.md）
    │   ├── Client:  输入采集、移动视图、HUD 血条/体力条
    │   └── Server:  HealthSystem、StaminaSystem（本机权威，无网络）
    │
    ├── com.zombtoy.weapon       武器：射线枪/冰手枪/火箭筒/龙卷风 + 弹药 + 切换
    │   ├── Shared:  WeaponState/Ammo 组件、fire/reload 协议
    │   ├── Client:  枪模视图、枪口特效、命中贴花（Pool 归本 Mod）
    │   └── Server:  FireSystem（射线权威）、ReloadSystem、ProjectileSystem
    │
    ├── com.zombtoy.enemy        敌人：僵尸兔/熊/象/巨型/Titan/小丑/爆炸鸭 + 生成器
    │   ├── Shared:  EnemyHealth/EnemyType 组件、damage/killed 协议
    │   ├── Client:  敌人视图（NavMesh 追敌）、死亡下沉、受击特效
    │   └── Server:  SpawnSystem（加权表）、ChaseSystem、AttackSystem、BossSystem
    │
    ├── com.zombtoy.item         道具：弹药/血瓶生成与拾取
    │   ├── Shared:  Item/HealthPotion/AmmoItem 组件、pickup 协议
    │   └── Server:  ItemSpawnSystem、PickupSystem
    │
    ├── com.zombtoy.score        分数：击杀/首领/连击 + 最高分 + 结算
    │   ├── Shared:  Score 组件、score-changed/game-over 协议
    │   └── Server:  ScoreSystem（订阅 EnemyKilled/PlayerDeath）
    │
    ├── com.zombtoy.leaderboard  高分榜：.NET 8 后端客户端
    │   └── Shared:  submit-score/get-scores 能力（HttpClient）
    │
    ├── com.zombtoy.ui           菜单/HUD/结算/设置（声明窗口给 UI.Mod）
    │   └── Client:  MenuView、HudView、ResultView、SettingsView
    │
    └── com.zombtoy.game         主 Mod：依赖所有，引导游戏流程（场景/回合）
        └── Shared:  game-state 协议、GameFlow（启动/暂停/结算路由）
```

**依赖 DAG**：

```
player ──┬──► weapon ──► enemy   （weapon 伤害走 enemy 能力）
         │
         └──► item               （拾取回血/回弹药走 player/weapon 能力）
score ◄── enemy（EnemyKilled 消息）
score ──► leaderboard（提交最高分）
ui ──► player/weapon/score/enemy（订阅消息更新 HUD）
game ──► 全部（依赖聚合 + 引导）
```

关键跨 Mod 通道（Rule 12，四条通道内）：
- **weapon → enemy 伤害**：ModCall（weapon 调 enemy 导出 `damage(entityId, amount, source)`）。
- **enemy → player 接触伤害**：ModCall（enemy 调 player 导出 `damage(amount, source)`）。
- **enemy → score 计分**：Message（enemy 发布 `EnemyKilled`，score 订阅）。
- **player/score → ui 更新**：Message（`HealthChanged`/`ScoreChanged`/`ZombieCountChanged`，ui 订阅）。
- **score → leaderboard**：ModCall（提交/查询）。

### 4.1 资源映射（原项目 → Mod）

原项目资源按领域拆分进各 Mod 的 `Assets/Mods/<modId>/`，经 ModPacker 打包为 Bundle，运行时走 `context.Resources`（Rule 3）。机制重写、资源搬用：

| 原项目资源（`replica_projects/Zombtoy/Assets/`） | 归属 Mod |
|---|---|
| 玩家角色模型（`Models/Characters`）、玩家 prefab、玩家动画 | com.zombtoy.player |
| `Guns/`（机关枪/多发/霰弹）、`Rocket/RocketLauncher/IceBullet/Tornado` 等投射物 prefab、枪口特效（WarFX） | com.zombtoy.weapon |
| 敌人 prefab（`Zombunny/ZomBear/Hellephant/Giant*/Titan/Clown/MiniClown/ZomDuck`）、敌人模型/动画、受击特效 | com.zombtoy.enemy |
| `Ammo.prefab`、`HealthPotion 1.prefab` | com.zombtoy.item |
| 无（纯逻辑） | com.zombtoy.score |
| 无（HttpClient 纯逻辑） | com.zombtoy.leaderboard |
| `Fonts/LuckiestGuy`、菜单/结算 UI、血条/体力条/准星 sprite | com.zombtoy.ui |
| `Level1` 环境（地板/墙/障碍）、`AllSkyFree` 天空盒、`Sci-Fi Pack` 环境模型 | com.zombtoy.game（或独立 com.zombtoy.level） |
| `Audio/`（SFX/音乐） | 按用途归属（枪声→weapon、敌人→enemy、背景音乐→ui） |

> 第三方资源（WarFX/Cartoon FX/AllSkyFree/Sci-Fi Pack）同样按领域归属打包进各 Mod，不跨 Mod 共享（Rule 3/§8.4）。

---

## 5. 建议迭代顺序

### 阶段 0（框架补缺 + 资源迁移，复用 FPS 成果）
1. **UI.Mod ↔ UGUI 绑定**——窗口实例挂 Canvas 层级 + UGUI 组件绑定（唯一硬性前置）。
2. **Unity 资源后端**——复用 FPS 的 ModPacker（Bundle）产物，接 `IResourceBackend` 的 Unity 实现。
3. **原项目资源迁移 + 打包**——按 §4.1 映射，把 `replica_projects/Zombtoy/Assets/` 的资源复制进各 Mod 的 `Assets/Mods/<modId>/`，ModPacker 打包 Bundle + 上传 mod_server。

### 阶段 1（MVP：核心玩法闭环）
`com.zombtoy.player` + `com.zombtoy.weapon` + `com.zombtoy.enemy`：
- 移动/转身 + 射线射击 + 敌人 NavMesh 追敌 + 生命/死亡。
- 对标样例的「能跑起来打僵尸」——移动、开火、受击、击杀。

### 阶段 2（完整战斗）
`com.zombtoy.item` + `com.zombtoy.score` + 武器族补齐（冰手枪/火箭筒/龙卷风）+ 首领 Titan。

### 阶段 3（UI + 后端 + 完整流程）
`com.zombtoy.ui` + `com.zombtoy.leaderboard` + `com.zombtoy.game`：
- 菜单/结算/排行榜 + .NET 8 后端对接 + 游戏流程引导。

### 每阶段验收
- `tests/run.sh` 全绿（含 Zombtoy 案例的无头测试：ECS 组件/系统、ModCall 伤害、消息订阅）。
- `verify-unity.sh` 通过 + 案例 README 对标清单逐项勾验。
- 无头测试覆盖：玩家受击/击杀计分/弹药消耗/生成器加权表/道具拾取（跨 Mod 用 ModCall.Invoke 辅助，二进制）。
