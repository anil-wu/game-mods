# Zombtoy 资产映射（原资源 → 所属 Mod）

> 原工程：`replica_projects/Zombtoy/Assets/`（只读参照，未修改）
> 复刻工程：`samples/Zombtoy/Assets/Mods/<modId>/`
> 依据：`design/replica-Zombtoy-analysis.md` §4.1 + 资源归属硬规则（Rule 3 / §8.4）——每个 Mod 的资源拷进**自己的 `Assets/Mods/<modId>/`**，
> 经 ModPacker 打包为独立 Bundle，运行时只经自己的 `context.Resources` 加载，不跨 Mod 共享。
> **排除项**：`.meta`（由 2022.3 重新导入）、`.cs`（代码重写）、`.unity` 场景（由 game Mod 运行时重建，`Level1.unity` 不搬）。

## 0. 迁移统计

| 项 | 值 |
|---|---|
| 迁移资源文件总数 | **~2,290**（各 Mod 内清单见下） |
| 迁移总体积 | **~665 MB**（weapon 124M / enemy 79M / item 30M / ui 46M / game 382M / player 3.6M） |
| 已排除 | `.meta` ×全部、`.cs` ×全部（Scripts/ 与三方包内编辑器脚本）、`.unity` ×9（Level1/Level3/Menu×4/演示场景） |
| 校验方式 | 源→目标逐文件比对（2249 文件 0 缺失 + 补充清单），抽样 MD5 一致；8 份 `mod.json` 经框架 `ModManifest.Parse` 全通过 |

## 1. 总映射表（§4.1 落表）

| 原项目资源 | 归属 Mod | 状态 |
|---|---|---|
| `Models/Characters/Player.fbx`、`Animations/PlayerAC.controller`、`Materials/PlayerMaterial.mat`、`Textures/Player*`（4 件） | com.zombtoy.player | ✅ 已迁移 |
| `Audio/Effects/Player Death.wav`、`Player Hurt.wav` | com.zombtoy.player | ✅ 已迁移（玩家受击/死亡音） |
| `Guns/`（Machine Gun / MultiShot / Shotgun 1） | com.zombtoy.weapon | ✅ 已迁移 |
| `Rocket.prefab`、`Rocket 1.prefab`、`RocketLauncher.prefab`、`IceBullet.prefab`、`Tornado.prefab` | com.zombtoy.weapon | ✅ 已迁移 |
| `Prefabs/GunParticles.prefab`（枪口粒子） | com.zombtoy.weapon | ✅ 已迁移 |
| 枪口/爆炸特效：`JMO Assets/WarFX`、`Imported/EffectTexturesAndPrefabs`、`Imported/Rocket Pack`、`Flames Of The Phoenix`、`Imported/TinyFire VFX` | com.zombtoy.weapon | ✅ 已迁移（§4.1「枪口特效（WarFX）」） |
| `Imported/JMO Assets`（Cartoon FX，Tornado 引用 CFX2/CFX3） | com.zombtoy.weapon | ✅ 已迁移（与 enemy 重复，独立 Bundle 各自持有） |
| 枪械材质/纹理：`GunMaterial.mat`、`FlareParticleMaterial.mat`、`LineRenderMaterial.mat`、`New Material.mat`（IceBullet 引用）、`Textures/Gun*`（4 件） | com.zombtoy.weapon | ✅ 已迁移 |
| 枪械/爆炸/换弹音频（Player GunShot、Gun cock、MachinegunRel、Minecraft Hit、strormtrooper、OHTKE0450、shotgun×3、rocket×2、blast/boom/tnt×2、cryo、tornado、audiomass-output (2)(3)） | com.zombtoy.weapon | ✅ 已迁移 |
| `Audio/Audio/FireShoot*.ogg`、`FireExplosion*.ogg`、`LargeFire*.ogg`（射击/爆炸火音） | com.zombtoy.weapon | ✅ 已迁移（FireShoot1/2 同时复制到 enemy——Clown 引用） |
| 敌人 prefab（Zombunny/ZomBear/Hellephant/Giant×3/Titan Zombunny/Clown/MiniClown/ZomDuckk） | com.zombtoy.enemy | ✅ 已迁移 |
| 敌人投射物：`EnemyProjectile.prefab`、`EnemyRocket Variant.prefab` | com.zombtoy.enemy | ✅ 已迁移 |
| `Prefabs/HitParticles.prefab`（受击特效） | com.zombtoy.enemy | ✅ 已迁移 |
| 敌人模型/动画：`Models/Characters/{Zombunny,ZomBear,Hellephant}.fbx`、`Animations/enemyAC.controller`、`ImportThis/`（Clown.fbx + ZombieDuck.fbx + override controller + 材质） | com.zombtoy.enemy | ✅ 已迁移 |
| 敌人材质/纹理：Eyes/FluffParticle/Zombunny/Zombear/Hellephant/BossGroundTarget Material + `Textures/ZomBunny*`、`ZomBear*`、`Hellephant*`、PuffSprite、PuffNormalSprite | com.zombtoy.enemy | ✅ 已迁移 |
| 敌人特效：`Imported/JMO Assets`（Cartoon FX）、`Flames Of The Phoenix`（Titan/EnemyProjectile）、`Imported/EffectTexturesAndPrefabs` + `Imported/Rocket Pack`（EnemyRocket Variant） | com.zombtoy.enemy | ✅ 已迁移（部分与 weapon 重复，各自持有） |
| 敌人音频：`Audio/Effects/{Hellephant,ZomBear,ZomBunny} Death/Hurt.wav`、`Audio/wizard/*`（Clown）、`creeper.mp3`（ZomDuckk）、`audiomass-output (1).mp3`（Giant/Titan）、`FireShoot1/2.ogg`（Clown） | com.zombtoy.enemy | ✅ 已迁移 |
| `Ammo.prefab`、`HealthPotion 1.prefab` | com.zombtoy.item | ✅ 已迁移 |
| 道具依赖：`Imported/Weapons_ChamferZone`（Ammo 引用弹壳模型）、`Imported/RPG Pack`（HealthPotion 引用 Bottle_green） | com.zombtoy.item | ✅ 已迁移 |
| `Textures/Heart.png`、`Audio/ammo pickup.mp3`、`Audio/Poofsound.mp3` | com.zombtoy.item | ✅ 已迁移 |
| 无资源（纯逻辑：计分/最高分/结算） | com.zombtoy.score | —（无 bundle） |
| 无资源（HttpClient 纯逻辑） | com.zombtoy.leaderboard | —（无 bundle） |
| `Fonts/LuckiestGuy/LuckiestGuy.ttf`、`vesmir/space age.ttf` | com.zombtoy.ui | ✅ 已迁移 |
| 菜单/结算/血条/体力/准星 sprite：`Textures/UI*.png`（9 件）+ `Images/*`（GameThumbnail、titlescreen、cogwheel、redxx、whitetriang 等）+ 根目录图标（trophy、zombtoyicon、PngItem、clipart、arrow、first person、isometric） | com.zombtoy.ui | ✅ 已迁移 |
| 音乐/UI 音效：`Audio/Music/*`（Background Music、arena music、background music 1）、`music_rev1_loop_01.wav`、`AURME9557.mp3`、`block.mp3`、`flashlightClick.mp3` | com.zombtoy.ui | ✅ 已迁移 |
| `Models/Environment/*`（地板/墙/障碍，16 个 fbx） | com.zombtoy.game | ✅ 已迁移 |
| `Prefabs/Environment.prefab`、`Prefabs/Lights.prefab` | com.zombtoy.game | ✅ 已迁移 |
| `Level1/NavMesh.asset` | com.zombtoy.game | ✅ 已迁移（场景运行时重建，NavMesh 数据保留） |
| 环境材质/纹理（Arches/Bat/Blox/Clock/DollArm/Dollhouse/Drawers/Firetruck/Hearse/Planks/Reflector/Robot/SpinningTop/Star/Stool/Train/Wall + 对应 Diffuse/Normals/Occlusion/Specular） | com.zombtoy.game | ✅ 已迁移 |
| `AllSkyFree` 天空盒全家（8 套变体 + 演示环境 + SunGlow/MoonGlow） | com.zombtoy.game | ✅ 已迁移（排除 .cs/.unity/.meta；314M 详见待处理 ③） |
| `vesmir/StarSkybox04`（Level1 实际使用的天空盒） | com.zombtoy.game | ✅ 已迁移 |
| `vesmir/Sci-Fi Styled Modular Pack`（环境模型/prefab/材质/纹理/动画） | com.zombtoy.game | ✅ 已迁移 |
| `vesmir/Audio/`（FutureWorld 环境循环、laser-shot） | com.zombtoy.game | ✅ 已迁移 |
| `Imported/EffectTexturesAndPrefabs`（Level1 引用 Star_A 特效） | com.zombtoy.game | ✅ 已迁移（与 weapon/enemy 重复） |
| `Audio/Audio/CampFire.ogg`、`FireLoop1-3.ogg`（环境火循环） | com.zombtoy.game | ✅ 已迁移 |

## 2. 跨 Mod 资源重复（设计使然，Rule 3 各自持有）

以下资源被多个 Mod 的 prefab 直接引用，按「Bundle 自包含」原则**复制进每个引用方**（不跨 Mod 加载）：

| 资源 | 持有 Mod | 原因 |
|---|---|---|
| `Imported/JMO Assets`（Cartoon FX） | weapon + enemy | Tornado（weapon）与全部敌人 prefab 引用 |
| `Flames Of The Phoenix` | weapon + enemy | Rocket（weapon）与 Titan/EnemyProjectile（enemy）引用 |
| `Imported/EffectTexturesAndPrefabs` | weapon + enemy + game | Rocket/EnemyRocket（weapon/enemy）与 Level1（game）引用 |
| `Imported/Rocket Pack` | weapon + enemy | Rocket.prefab / EnemyRocket Variant.prefab 引用 |
| `Audio/Audio/FireShoot1/2.ogg` | weapon + enemy | 枪械射击（weapon）+ Clown（enemy）引用 |
| `New Material.mat` | weapon + enemy | IceBullet（weapon）与 EnemyProjectile（enemy）引用 |

## 3. 已排除（不迁移）

| 类别 | 内容 | 理由 |
|---|---|---|
| `.meta` | 全部（3,000+） | 2022.3 重新导入（复刻规范） |
| `.cs` | `Scripts/` 全部 60+ 脚本 + 三方包编辑器脚本（WarFX Demo/Editor、Cartoon FX Easy Editor、AllSkyFree Editor、TinyFire LightControl） | 代码重写（任务约束） |
| `.unity` 场景 | Level1 / Level3 / Menu×4 / WarFX Demo / RPG Pack Demo / TinyFire Demo / AllSky 演示 | 场景由 game Mod 运行时重建 |
| `Level1.unity` | — | 任务明确不搬 |
| `Scripts/Server/placeholder.prefab` | 杂项 | Scripts 目录内的占位物，非资源目录 |
| `Resources/BillingMode.json`、`UnityPackageManager/manifest.json`、`ProjectSettings/ProjectVersion.txt` | 原工程配置 | 非 Mod 资源 |

## 4. 待处理（后续里程碑）

1. **GUID 引用断链（最大项）**：迁移不带 `.meta`，全部 GUID 重新生成——材质→纹理、prefab→模型/动画控制器、controller→动画剪辑的引用会在编辑器里断开。
   需在 Unity 编辑器内逐个重连（ModPacker 已把资源打进同 Mod Bundle，重连素材齐备）；或由 Mod 视图代码在运行时对关键材质赋值纹理。
   （FPS 先例同样接受此代价，「需逐材质重建」）
2. **URP → 内置管线**：原工程部分材质（Sci-Fi Pack、WarFX 等）为 URP ShaderGraph，导入 2022.3 内置管线会渲染品红；ModPacker 已内置 URP→Standard 转换（`ConvertUrpMaterialsToBuiltin`），打包时执行，`_BaseColor` 红映射按实际纹理校验（总纲 §4 已知风险）。
3. **AllSkyFree 体积 314M**：Level1 实际天空盒是 `vesmir/StarSkybox04`；AllSkyFree 8 套变体 PNG 面片体积大且多数未使用。可裁剪至 1–2 套变体（M4 打包前决定）。
4. **未引用音频待确认**：`Audio/benlol.mp3`、`Audio/FullSizeRender.MOV.mp3` 未被任何 prefab/场景引用（已确认），暂留原工程不迁移；`Audio/Audio` 火音按用途拆分（FireShoot→weapon、FireLoop/CampFire→game），如 Clown/枪械换弹需要其他变体再补。
5. **`Imported/TinyFire VFX` 未引用**：原工程导入但无 prefab/场景引用（已确认），迁入 weapon 作备用火特效，可裁剪。
6. **数值校准**：契约 §0.8 —— 敌人生成表/伤害/弹药等数值在资源迁移后按 prefab 实际配置校准，改动需升 Mod 版本并同步测试向量。
7. **构建工具参数化**：`runtime/build-mod-unity.py` 当前硬编码 `SAMPLE = samples/MirrorUnityFPS`，需支持 Zombtoy（M4.1 打包里程碑处理）。
8. **`Audio/Music/` 内 2 张截图 PNG**（2022-01-02_19.58.42.png 等）：原工程误放，已随目录迁入 ui，可清理。
9. **TinyFire/WarFX 内含 `.unity` 演示场景与 `.cs` 已排除**，其 prefab 若引用被排除的脚本组件会在导入时报 missing script，属预期（机制重写后由新视图接管）。

## 5. 各 Mod Bundle 声明（mod.json assets.bundles）

| Mod | Bundle | 是否含资源 |
|---|---|---|
| com.zombtoy.player | com.zombtoy.player.bundle | ✅ |
| com.zombtoy.weapon | com.zombtoy.weapon.bundle | ✅ |
| com.zombtoy.enemy | com.zombtoy.enemy.bundle | ✅ |
| com.zombtoy.item | com.zombtoy.item.bundle | ✅ |
| com.zombtoy.score | —（无资源） | — |
| com.zombtoy.leaderboard | —（无资源） | — |
| com.zombtoy.ui | com.zombtoy.ui.bundle | ✅ |
| com.zombtoy.game | com.zombtoy.game.bundle | ✅ |
