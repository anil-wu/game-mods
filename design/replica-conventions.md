# 样例复刻规范

> 目的：以真实开源工程为参照物，验证并驱动 V2.0 框架（`Unity_Mod_Runtime_架构设计V2.0.md`）的能力建设。
> 复刻目标是**机制等价**，不是资源搬运——每个复刻案例都是框架能力的验收场。

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

## 复刻工程的约束

1. **复刻 Mod 与 `mods/` 创作空间同构**：同样的 `mod.json + src/ + data/` 布局，同样的构建方式（构建脚本按目录扫描，见下）。
2. **遵守 V2.0 全部规则**：IMod 仅 Register/Unregister、无泛型 ABI、通信全二进制、协议走 Network.Mod、窗口走 UI.Mod、契约=文档。
3. **不使用原工程的代码与资源**：机制重新实现（原工程多为 NetworkBehaviour 单体，本来就放不下我们的 ABI）；美术用占位资源。
4. **验收**：`tests/run.sh` 全绿（含复刻案例的无头测试）+ `verify-unity.sh` 通过 + 案例 README 的对标清单逐项勾验。

## 构建集成

- `runtime/build-mod.py` 扫描 `mods/` 与 `samples/*/mods/` 两个创作空间，统一拓扑排序构建，产物拷贝到 `runtime/Unity/Assets/StreamingAssets/mods/`。
- 无头测试（`tests/TestRunner`）按需 Include 复刻 Mod 的源码（`samples/<Case>/mods/<modId>/src/**/*.cs`）。

## 当前案例

| 参照工程 | 复刻工程 | 分析 | 状态 |
|---|---|---|---|
| `replica_projects/MirrorUnityFPS` | `samples/MirrorUnityFPS` | [可行性分析](replica-MirrorUnityFPS-analysis.md) | 待启动（前置：ECS Replication，§11.10） |
