# MirrorUnityFPS 资产映射（原资源 → 所属 Mod）

> 原工程资源已忠实镜像到 `replica-assets/`（738M / 352 文件，排除 .meta 供 2022.3 重新导入）。
> 按资源归属硬规则（Rule 3/§8.4）：每个 Mod 的资源拷进**自己的 `assets/`**，只经自己的
> `context.Resources` 加载管理。下表为里程碑逐步导入的映射。

## 资产 → Mod 映射

| 原工程目录 | 归属 Mod | 用途 |
|---|---|---|
| `Model/Futuristic_soldier`（科幻兵三色 + Prefab） | com.fps.player | 玩家身体（第三人称自己/他人） |
| `Model/FPS Hand`（第一人称手 + 材质 + Prefab） | com.fps.player | 第一人称手持视角 |
| `Animation/Kevin Iglesias/Basic Motions`（闲置/跑/冲刺/跳/落地/走 + 假人） | com.fps.player | 玩家动画 |
| `StarterAssets/FirstPersonController`（PlayerCapsule/Camera Prefab） | com.fps.player | 第一人称控制器 |
| `Model/Italian machine guns` / `Model/Pistol` / `Model/Small_set_of_knives` | com.fps.weapon | 枪械/近战武器 |
| `Content/Weapon` / `Content/Projectile` / `Content/Effect` / `Content/Ammo` | com.fps.weapon | 武器内容/弹丸/特效/弹药 |
| `Model/True_Fantastic_Creatures/Centaur`（半人马） | com.fps.npc | 怪物 NPC |
| `Animation/Kevin Iglesias/Melee Warrior Animations`（近战勇士 + 动画控制器） | com.fps.npc | 敌人动画/行为 |
| `Content/NPC` | com.fps.npc | NPC 内容 |
| `Content/Inventory` + `Material/Outline Material.mat` | com.fps.inventory | 背包内容 + 拾取描边 |
| `Model/Bridge` + `Model/Building` + `StarterAssets/Environment` | com.fps.mapgen | 桥/建筑/环境件 |
| `Content/World Generation` + `Content/World Props` | com.fps.mapgen | 程序化世界 |
| `Content/World Popup` | com.fps.player | 伤害飘字 |
| `Prefab/Networking/Network Manager.prefab` | —（runtime 已有 Mirror 传输） | 不导入 |

## 已知待处理（每个里程碑解决）

1. **URP 材质 → 内置管线**：原工程材质多为 URP ShaderGraph，导入 2022.3 内置管线会渲染品红，
   需逐材质重建为 Standard/URP 等价（`Builtin_RP` 前缀材质已有内置版本可优先用）。
2. **.blend 源文件**：Unity 导入需本机装 Blender；优先用同目录 `.fbx` 导出。
3. **纹理体积**：PNG 550M + TGA 415M，可后处理压缩（ASTC/ETC2）降低包体。

## 里程碑导入清单

| 里程碑 | 需拷入对应 Mod `assets/` 的资源 |
|---|---|
| M1 移动+相机 | player：Futuristic_soldier、FPS Hand、Basic Motions、FirstPersonController |
| M2 武器 | weapon：Italian machine guns、Pistol、knives、Content/Weapon/Projectile/Effect/Ammo |
| M3 背包 | inventory：Content/Inventory、Outline Material |
| M4 生命/飘字 | player：Content/World Popup |
| M5 地图 | mapgen：Bridge、Building、StarterAssets/Environment、Content/World Generation/Props |
| M6 NPC+控制台 | npc：Centaur、Melee Warrior、Content/NPC |
