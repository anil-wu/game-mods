# 样例复刻规范

> 目的：以真实开源工程为参照物，验证并驱动 V2.0 框架（`Unity_Mod_Runtime_架构设计V2.0.md`）的能力建设。
> **复刻目标（最高原则）：原原本本复刻——系统、玩法、表现都必须忠实还原原版。**
> 即：机制/数值、资源（模型/材质/贴图/动画/音频）、特效、UI、镜头、手感都要与原版一致，
> 不允许用占位图元/自建近似物代替原版资源或表现。

## 硬性规范

### Rule 0 — 原原本本复刻（最高优先级）

**所有系统、玩法和表现必须原原本本复刻**：
1. **系统**：生命/体力/弹药/敌人/分数/道具等逻辑与数值与原版一致（不许改数值、不许简化机制）；
2. **玩法**：移动/射击/AI/生成/拾取/计分等行为与原版一致；
3. **表现**：模型/特效/动画/UI/镜头/音效用原版资源（原 prefab/材质/贴图/动画控制器），
   视图必须加载原版 prefab/模型，**禁止自建胶囊/方块/裸 ParticleSystem 等近似物代替**；
   仅当原版资源确实缺失时才允许回退（需日志声明）。

> 与 Rule 3 资源归属、V2.0 隔离/二进制等规则冲突时，Rule 0 优先（但不得破坏 Rule 12/13/14 跨 Mod 通信红线）。

## 硬性规范

### Rule 1 — Unity 版本

复刻工程统一使用 **Unity 2022.3.62f3**（与 `runtime/Unity` 一致）。
原工程使用其他 Unity 版本（如 Unity 6）时，差异部分（如 Starter Assets、新 API）在复刻中以 Mod 表现层内部实现替换，框架无感。

### Rule 2 — 参照工程位置

所有可复刻的参照工程放在 **`replica_projects/`** 下，一个工程一个文件夹：

```
replica_projects/
└── MirrorUnityFPS/        参照工程（只读，不修改、不提交其构建产物）
```

参照工程仅作分析与对标，**不向其中写入任何复刻代码**。

### Rule 3 — 复刻产物位置

复刻后的案例工程放在 **`samples/`** 下，与参照工程**同名**：

```
samples/
└── MirrorUnityFPS/        复刻工程（我们的代码）
```

### Rule 4 — Mod 工程拆分

复刻目标文件夹下必须放置**按 V2.0 拆分后的 Mod 工程**——复刻不是把原代码整体搬家，而是按 §15.4（一个 Mod = 功能领域 = Shared/Client/Server 模块）重新组织：

```
samples/MirrorUnityFPS/
├── README.md              复刻说明：对标功能清单（机制 ↔ 实现映射）、进度、已知差异
├── mods/                  拆分的 Mod 工程（每个 Mod 一个目录，以 modId 命名）
│   ├── com.fps.player/
│   │   ├── mod.json       Manifest v2（id/modules/dependencies/network.protocols）
│   │   ├── CONTRACT.md    契约文档（Rule 19：协议/消息/能力的线格式 + 测试向量）
│   │   ├── src/           Mod 源码（IMod v2）
│   │   └── data/          数据/配置（可选）
│   ├── com.fps.weapon/
│   ├── com.fps.inventory/
│   └── com.fps.mapgen/
└── assets/                复刻用占位美术（可选，能跑通机制即可）
```

## 资源归属硬规则（Rule 3 / §8.4）

**所有 Mod 内部的资源必须通过所属 Mod 提供加载与管理**：

1. 资源打包进所属 Mod 自己的 `assets/`（→ 该 Mod 的 AssetBundle，见 §15.1）；
2. Mod 只能经自己的 `context.Resources.Load(AssetId "本Mod:路径")` 加载，不能 `Resources.Load` /
   `AssetDatabase.LoadAssetAtPath` / 直接 `File.ReadAllBytes` 访问资源文件（§8.3）；
3. 跨 Mod 默认禁止直接访问资源（`Weapon ─X─► Vehicle/super_car.prefab`）；确需时只能走能力/服务
   （§8.4：`Resolve` 受限解析或对方导出的资源能力），且消费方应在 Manifest 声明依赖；
4. 资源生命周期归属 ModObject：卸载时整域释放（§8.8，AssetBundle 后端整域卸载）。

## 复刻工程的约束

1. **复刻 Mod 与 `mods/` 创作空间同构**：同样的 `mod.json + src/ + data/` 布局，同样的构建方式（构建脚本按目录扫描，见下）。
2. **遵守 V2.0 全部规则**：IMod 仅 Register/Unregister、无泛型 ABI、通信全二进制、协议走 Network.Mod、窗口走 UI.Mod、契约=文档。
3. **机制重新实现，资源必须用原项目资源**：代码重写（原工程多为 NetworkBehaviour 单体，本来就放不下我们的 ABI）；但**美术/模型/动画/材质/纹理/音频必须从原项目搬用**，按 Mod 领域拆分打包（走 ModPacker，规则见下方「资源归属硬规则」），不造占位资源。
4. **验收**：`tests/run.sh` 全绿（含复刻案例的无头测试）+ `verify-unity.sh` 通过 + 案例 README 的对标清单逐项勾验。

## 构建集成

- `runtime/build-mod.py` 扫描 `mods/` 与 `samples/*/mods/` 两个创作空间，统一拓扑排序构建，产物拷贝到 `runtime/Unity/Assets/StreamingAssets/mods/`。
- 无头测试（`tests/TestRunner`）按需 Include 复刻 Mod 的源码（`samples/<Case>/mods/<modId>/src/**/*.cs`）。

## 当前案例

| 参照工程 | 复刻工程 | 分析 | 状态 |
|---|---|---|---|
| `replica_projects/MirrorUnityFPS` | `samples/MirrorUnityFPS` | [可行性分析](replica-MirrorUnityFPS-analysis.md) | ✅ MVP（player+weapon+Replication，见案例 README） |
| `replica_projects/Zombtoy` | `samples/Zombtoy` | [可行性分析](replica-Zombtoy-analysis.md) | 📋 分析完成，待实施 |
