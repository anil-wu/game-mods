# Zombtoy 复刻实施总纲

> 权威分析：[`replica-Zombtoy-analysis.md`](replica-Zombtoy-analysis.md)。本总纲是执行层规划（决策 + 里程碑 + 验收），
> 拆解给 sub agent 的任务书都指向本文件 + 分析文档 + FPS 先例（`samples/MirrorUnityFPS/`）。

## 0. 关键决策（已定）

| 决策 | 结论 |
|---|---|
| 工程结构 | `samples/Zombtoy/` 独立 Unity 工程（复制 FPS 的 `Packages/` + `ProjectSettings/` + `Assets/Editor/ModPacker.cs`），Mod 放 `Assets/Mods/<modId>/` |
| Mod 划分 | 8 个：player / weapon / enemy / item / score / leaderboard / ui / game |
| 逻辑/表现分层 | **ECS 管逻辑**（组件+System，可无头测试）；**MonoBehaviour 管表现**（NavMesh/射线/渲染/输入/动画，`*View.cs` 不参与无头测试） |
| 跨 Mod 通道 | 伤害走 **ModCall**（二进制）；状态变更/击杀/计分走 **Message**（事件）；UI 订阅消息 |
| UI 方案 | MVP 用 Mod 自带 View 直接建 UGUI（HUD/菜单）；WindowManager 绑定做基础版（窗口 prefab → Canvas 层级） |
| 资源 | **原项目资源**（模型/材质/纹理/音频/prefab）按 `Assets/Mods/<modId>/` 拆分，ModPacker 打包 Bundle，`context.Resources` 加载（Rule 3） |
| 场景/流程 | 单宿主场景，`com.zombtoy.game` 的 View 管状态机（Menu → Gameplay → Result），不用多 Unity 场景 |
| 版本 | Unity 2022.3.62f3（宿主 `runtime/Unity` 一致） |

## 1. Mod 契约速查（跨 Mod 能力/消息，Rule 12 四条通道内）

| 方向 | 通道 | 定义 |
|---|---|---|
| weapon → enemy 伤害 | ModCall | enemy 导出 `damage(entityId, amount, source)`（DataCodec 二进制） |
| enemy → player 接触伤害 | ModCall | player 导出 `damage(amount, source)` |
| enemy → score 计分 | Message | enemy 发布 `EnemyKilled`（含 enemyType/分值） |
| player → ui 生命 | Message | player 发布 `HealthChanged` |
| score → ui 分数 | Message | score 发布 `ScoreChanged` |
| enemy → ui 僵尸数 | Message | enemy 发布 `ZombieCountChanged` |
| score → leaderboard | ModCall | leaderboard 导出 `submit(score)` / `getTop(n)` |

## 2. 里程碑

### M0 基础（框架 + 骨架 + 资源）
- M0.1 实施设计 + 8 Mod 契约（组件/系统/协议/测试向量）→ `samples/Zombtoy/` 各 `CONTRACT.md`
- M0.2 工程骨架（Packages/ProjectSettings/ModPacker/mod.json×8/asmdef）
- M0.3 资源迁移（原资产 → 各 Mod）+ `ASSET_MAPPING.md`
- M0.4 UI↔UGUI 绑定（框架补缺：窗口 prefab 实例化到 Canvas 层级）

### M1 可玩核心（MVP）
- M1.1 player：移动/转身/生命/体力/冲刺/相机
- M1.2 weapon：射线枪 + 弹药 + 换弹
- M1.3 enemy：NavMesh 追敌 + 生命/死亡 + 加权生成器

### M2 完整战斗
- M2.1 item：弹药/血瓶拾取
- M2.2 score：计分 + 最高分 + 结算
- M2.3 weapon 族补齐（火箭筒/冰手枪/龙卷风）+ Titan 首领

### M3 完整流程
- M3.1 ui：菜单/HUD/结算
- M3.2 leaderboard：.NET 8 后端客户端
- M3.3 game：主 Mod + 状态机（Menu→Gameplay→Result）

### M4 交付
- M4.1 打包（ModPacker → dist/mods）+ 无头测试 + 上传 mod_server
- M4.2 QA：`tests/run.sh` 全绿 + `verify-unity.sh` 通过 + Play 冒烟

## 3. 验收标准

1. `tests/run.sh` 全绿（含 Zombtoy 无头测试：ECS 系统、ModCall 伤害、消息订阅、生成器加权表、计分）。
2. `verify-unity.sh` 通过（Unity 编译 + Play 冒烟）。
3. 案例 README 对标清单逐项勾验（移动/射击/敌人/拾取/计分/排行榜）。
4. 商店装 `com.zombtoy.game`（依赖闭包）→ 可玩到「移动+打僵尸+计分」闭环。

## 4. 已知风险

- 原项目 `Rocket.cs` 有 `using UnityEditor;` 破坏构建 —— 复刻时**不搬运**。
- Titan `groundTarget` 绑定 bug —— 复刻用独立准星 decal，不复刻 bug。
- URP 材质 → 内置 Standard 转换（ModPacker 已有逻辑）；`_BaseColor` 红映射 bug 需按实际纹理校验。
- new-api 余额低：文本任务走直连 `deepseek/deepseek-v4-flash` + `kimi/kimi-k3`（免费）；视觉对比暂缓。
