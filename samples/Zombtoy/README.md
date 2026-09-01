# Zombtoy 复刻（samples/Zombtoy）

> 参照工程：`replica_projects/Zombtoy`（Survival-Shooter 衍生 3D 僵尸生存，单机 + .NET 8 高分榜后端，Unity 2022.3.37f1）
> 规范：[`design/replica-conventions.md`](../../design/replica-conventions.md)；分析：[`design/replica-Zombtoy-analysis.md`](../../design/replica-Zombtoy-analysis.md)；
> 总纲：[`design/zombtoy-implementation-plan.md`](../../design/zombtoy-implementation-plan.md)；契约：[`contracts/`](contracts/)（8 份 CONTRACT.md）
> 目标：**机制等价**，验证 V2.0 框架单机路径（ECS + Messaging + ModCall + UI + 商店）——不搬原版 bug（Rocket.cs 的 `using UnityEditor`、Titan groundTarget 绑错），机制等价即可。

## 里程碑进度

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0.1 | 实施设计 + 8 Mod 契约（组件/系统/协议/测试向量） | ✅（`contracts/`，上游产出） |
| **M0.2** | **工程骨架：Packages/ProjectSettings/ModPacker + 8 个 Mod（mod.json + asmdef + CONTRACT.md）** | ✅ 本任务 |
| **M0.3** | **资源迁移：原资产按 §4.1 映射进各 Mod + [`ASSET_MAPPING.md`](ASSET_MAPPING.md)** | ✅ 本任务（~2,290 文件 / ~665MB） |
| M0.4 | UI↔UGUI 绑定（框架补缺：窗口 prefab → Canvas 层级） | ⏳ 框架侧 |
| M1 | player + weapon + enemy（移动/射击/NavMesh 追敌/生命/死亡） | ⏳ |
| M2 | item + score + 武器族补齐（冰手枪/火箭筒/龙卷风）+ Titan 首领 | ⏳ |
| M3 | ui + leaderboard + game（菜单/HUD/结算 + 后端 + 状态机） | ⏳ |
| M4 | 打包（ModPacker → dist/mods）+ 无头测试 + 上传 mod_server + QA | ⏳ |

## 对标功能清单（机制 ↔ 实现）

| 原工程机制 | 复刻实现（契约定位） | 状态 |
|---|---|---|
| 玩家移动/转身/冲刺（WASD + Rigidbody + 鼠标射线转身） | com.zombtoy.player：Position3/Velocity3/Facing/PlayerInput + InputApply→Movement→Stamina 系统 | ⏳（骨架就绪） |
| 生命 100 / 体力条 / 死亡 | com.zombtoy.player：PlayerHealth/Stamina 组件 + `damage/heal/reset` 能力 + HealthChanged/StaminaChanged/Died 消息 | ⏳ |
| 射线枪（Machine Gun/Shotgun/Cryo Pistol/Rocket Launcher 4 槽） | com.zombtoy.weapon：WeaponSlot×4/WeaponAmmo + Fire/Reload/Switch 系统 + AmmoChanged/WeaponSwitched 消息 | ⏳（资源已备：Guns/3 + RocketLauncher + Rocket + IceBullet） |
| 弹药系统（弹匣/备弹/换弹） | com.zombtoy.weapon：`add_ammo/reset/get_state` 能力 | ⏳ |
| 投射物族（Rocket 爆炸 / IceBullet 追踪 / Tornado 拉拽） | com.zombtoy.weapon：Projectile 系统（M2） | ⏳（资源已备） |
| 敌人 10 种（僵尸兔/熊/象 + 巨型 + Titan 首领 + 小丑/迷你小丑 + 爆炸鸭） | com.zombtoy.enemy：EnemyKind 枚举 + 加权生成器 + Chase/Attack/Boss 系统 + `damage/apply_slow/get_all` 能力 | ⏳（10 个 prefab + 模型/动画已迁） |
| 敌人 AI（NavMesh 追敌 + 接触伤害 + 远程射击） | com.zombtoy.enemy：EnemyMovement/EnemyAttack + EnemyProjectile/EnemyRocket | ⏳ |
| 敌人生成（加权表 + 波次/并发上限 50） | com.zombtoy.enemy：SpawnSystem + EnemySpawner 组件 | ⏳ |
| 分数/最高分/连击 | com.zombtoy.score：订阅 EnemyKilled/Died + ScoreChanged/GameOver 消息 | ⏳ |
| 道具掉落（弹药/血瓶） | com.zombtoy.item：ItemSpawnSystem + `pickup` 能力 + PickedUp 消息（Ammo/HealthPotion prefab + RPG Pack/ChamferZone 已迁） | ⏳ |
| 后端高分榜（dreamlo + .NET） | com.zombtoy.leaderboard：`submit/get_top/refresh` 能力 + 传输抽象（Fake 可无头测试） | ⏳ |
| UI/菜单/结算/HUD | com.zombtoy.ui：5 窗口声明 + 消息订阅绑定（Fonts/UI sprite/Images/Music 已迁） | ⏳ |
| 游戏流程（Menu→Gameplay→Result） | com.zombtoy.game：相位状态机 + `start/to_menu/pause/resume` + StateChanged 消息 | ⏳ |
| Level1 场景（环境/NavMesh/天空盒） | com.zombtoy.game：运行时重建场景（Models/Environment + Level1/NavMesh.asset + StarSkybox04 已迁） | ⏳ |
| 相机（等距 CamerFollow + FPV 未完成） | com.zombtoy.player：相机视图（MVP 等距） | ⏳ |

## 工程结构

```
samples/Zombtoy/
├── ASSET_MAPPING.md           原资源 → Mod 映射清单（已迁移/待处理）
├── contracts/                 8 份 CONTRACT.md（上游产出，权威契约）
├── Packages/manifest.json     复制自 FPS（含 URP 包，ModPacker 打包时转内置管线）
├── ProjectSettings/           ProjectVersion.txt（2022.3.62f3）+ 空 EditorBuildSettings
├── Assets/
│   ├── Editor/ModPacker.cs    打包工具（复制自 FPS，逻辑未改）
│   ├── Plugins/               Game.Mod.Contract / Game.ECS / Game.Messaging / Game.Mod.Runtime（netstandard2.1）
│   └── Mods/<modId>/          8 个 Mod：mod.json + CONTRACT.md + Scripts/<modId>.asmdef + 资源
└── README.md
```

8 个 Mod 与依赖 DAG（Manifest 声明，无环）：

```
com.zombtoy.player ───────────────► core
com.zombtoy.leaderboard ──────────► core
com.zombtoy.enemy ──────► player
com.zombtoy.weapon ─────► player, enemy
com.zombtoy.item ───────► player, weapon
com.zombtoy.score ──────► player, enemy, leaderboard
com.zombtoy.game ───────► player, weapon, enemy, item, score, leaderboard
com.zombtoy.ui ─────────► core, com.game.ui(框架), game, player, weapon, enemy, score, leaderboard
```

## 关键技术决策（本阶段）

1. **Mod 落位 `Assets/Mods/<modId>/`**（Unity 工程形态，FPS 先例），非 `mods/` 创作空间——由 `runtime/build-mod-unity.py` 经 Unity + ModPacker 打包（该脚本当前硬编码 FPS 工程，M4 参数化）。
2. **asmdef 显式引用 4 个框架 DLL**（`overrideReferences + precompiledReferences`），命名 = modId → 产物 `com.zombtoy.<x>.dll` 与 `modules.shared` 一致；ModPacker 按「单 asmdef」约定拷贝 DLL。
3. **资源镜像原目录结构**（Models/Animations/Guns/Materials/Textures/Audio/… 同名子目录）→ ASSET_MAPPING 逐行可追溯。
4. **跨 Mod 引用资源按 Bundle 自包含复制**（Cartoon FX / FlamesOfPhoenix / EffectTexturesAndPrefabs / Rocket Pack 等重复持有，Rule 3）。
5. **`boot:false` ×8**：不进 Unity 启动集（`StreamingAssets/mods/` 只放 4 个 boot Mod），经商店/打包分发（AGENTS §8）。

## 运行与验收

```bash
# 构建（M0.4 后可用；当前无 .cs，仅验证骨架）
python runtime/build-mod-unity.py     # 需先参数化支持 Zombtoy（M4.1）
bash tests/run.sh                     # 无头测试（M1 起逐 Mod 补 Zombtoy 案例）
bash runtime/verify-unity.sh          # Unity 编译 + Play 冒烟
```

## 已知差异与风险（继承总纲 §4 / 分析 §3.3）

- **不搬运原版 bug**：`Rocket.cs` 的 `using UnityEditor;` 破坏构建、Titan `groundTarget` 绑错导致转向冻结。
- **GUID 断链**：迁移无 `.meta`，材质→纹理等引用需编辑器重连（ASSET_MAPPING 待处理 ①）。
- **URP 材质 → 内置 Standard**：ModPacker 打包时转换；`_BaseColor` 红映射按实际纹理校验。
- **UI↔UGUI 绑定（M0.4）与 Unity 资源后端是框架侧前置**，契约按「窗口声明 + context.Resources」设计，不依赖其完成度即可开工逻辑。
- 数值（生成表/伤害/弹药）以资源迁移校准为准，改动升版本 + 同步测试向量（契约 §0.8）。
