# Mod = 独立 Unity 工程（架构演进）

> 动机：当前 Mod 代码用 `dotnet build`（netstandard2.1）外部编译，**只能引用 UnityEngine 核心模块，
> 无法引用 Cinemachine / Input System / URP / TMP / Addressables 等包程序集**——"原原本本"复刻因此受限。
> 解法：**每个 Mod 是一个独立的 Unity 工程**，代码由 Unity 编译，可引用任意包。

## 一、总体结构

```
游戏仓库
├── runtime/Unity/               宿主工程（加载并运行 Mod）
│   ├── Assets/Scripts/          宿主引导（ModRuntime / 资源后端 / 传输）
│   ├── Assets/Plugins/          框架 DLL（Game.Mod.*，AOT 稳定 ABI）
│   └── Assets/StreamingAssets/mods/<modId>/   ← Mod 构建产物（.mod 包）
│
├── mods/<modId>/                每个 Mod = 一个 Unity 工程
│   ├── Packages/manifest.json   本 Mod 用到的包（Cinemachine/InputSystem/URP/TMP/…）
│   ├── ProjectSettings/
│   ├── Assets/
│   │   ├── Scripts/             IMod 实现 + MonoBehaviour 视图（可用任意包）
│   │   ├── Prefabs/ Models/ Materials/ Textures/ Animations/ Audio/
│   │   ├── Data/                配置/数据（可选）
│   │   └── Plugins/             框架 DLL 引用（Game.Mod.*，只读）
│   ├── mod.json                 Manifest v2（id/modules/dependencies/protocols/assets/pinned/boot）
│   └── CONTRACT.md              契约文档（Rule 19）
│
└── samples/<Case>/mods/<modId>/ 复刻案例的 Mod 工程（同构）
```

## 二、构建产物（.mod 包）

Mod 工程经 Unity 批处理构建产出：

```
com.fps.player.mod（目录）
├── manifest.json
├── com.fps.player.dll          Mod 代码程序集（Unity 编译：asmdef 或 Assembly-CSharp）
├── com.fps.player.bundle       资源包（Prefab/模型/材质/贴图/动画/音频）
└── data/...                    数据文件
```

## 三、宿主加载（不变，§7/§15.2 语义一致）

1. 发现 `.mod` 包 → 解析 manifest
2. `Assembly.Load` 代码程序集 → 找 `IMod` 入口 → `Register(context)`
3. `UnityAssetBundleBackend` 加载 `{modId}.bundle`（§8.2 Mod 自治）
4. 视图 MonoBehaviour 由运行时实例化（§4 Views 归属）
5. 卸载走 §7 管线（程序集按名去重复用；HybridCLR 接入前"同副本重入"语义，§11.14.1）

## 四、关键约束与配套改动

1. **宿主携带共享包**：Mod 视图引用 Cinemachine/InputSystem/TMP/URP 时，宿主工程必须安装同版本包
   （否则 Mod DLL 加载时类型无法解析）。这些包即"Mod 运行时共享依赖"，版本由宿主锁定。
2. **框架 ABI 不变**：IMod/IModContext/无泛型 ABI/二进制通信（Rule 13/14）——Mod 工程只多了"可用任意包"，
   跨 Mod 隔离规则（Rule 12/19）不变。
3. **逻辑仍可无头测试**：Mod 的 IMod/ECS/协议层仍纯 C#，测试工程（tests/TestRunner）照旧
   按源码编译 Mod 的逻辑部分（排除 UnityEngine 视图）。
4. **构建工具**：`runtime/build-mod-unity.py` —— 扫描 mods 与 samples 的 Mod 工程，逐个用
   Unity 批处理构建代码程序集 + AssetBundle，聚合为 `.mod` 包。

## 五、迁移计划

| 步 | 内容 |
|---|---|
| 1 | Mod 工程脚手架 + 构建工具（Unity 批处理：asmdef 编译 + AssetBundle + 聚合 .mod） |
| 2 | 宿主工程接入共享包（Cinemachine/InputSystem/TMP/URP） |
| 3 | com.fps.player 迁移试点（原样 FPC/Cinemachine/模型/动画） |
| 4 | 其余 Mod 逐个迁移 |
| 5 | 商店/打包/测试流水线适配新产物 |

> 与 V2.0 §15.2 的关系：本方案是"HybridCLR 之前的 Unity 原生热载"——Mod 代码在各自工程内由
> Unity 编译（天然支持包），宿主按程序集加载；HybridCLR 接入后，热更/卸载语义无缝升级。
