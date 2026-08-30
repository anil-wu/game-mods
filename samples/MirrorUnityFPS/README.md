# MirrorUnityFPS 复刻（samples/MirrorUnityFPS）

> 参照工程：`replica_projects/MirrorUnityFPS`（Mirror + Starter Assets，Unity 6）
> 规范：[`design/replica-conventions.md`](../../design/replica-conventions.md)；分析：[`design/replica-MirrorUnityFPS-analysis.md`](../../design/replica-MirrorUnityFPS-analysis.md)
> 目标：**机制等价**，验证 V2.0 框架（ECS Replication / Mod 隔离 / 协议治理 / 生命周期）。

## 复刻计划（自主决策版）

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | **ECS Replication**（框架，§11.10：Archetype 注册 / Spawn-Despawn / Snapshot / NetworkId↔Entity） | ✅ |
| 1 | **com.fps.player**：服务端权威移动（输入 C2S → 积分 → 复制回客户端）+ 生命/伤害/死亡/重生 | ✅ |
| 2 | **com.fps.weapon**：射击（冷却/弹药）+ 换弹 + 命中判定（服务端 ray-sphere）+ 伤害经 ModCall | ✅ |
| 3 | Unity 表现层（代码原语：胶囊体/平面/第一人称相机/弹道线/准星，零美术资源） | ✅ |
| 4 | 文档 + 验收（无头 e2e + Unity 冒烟） | ✅ |
| 5 | 背包/拾取（com.fps.inventory）+ HUD 正式窗口 | ⏳ 后续 |
| 6 | 程序化地图（seed 同步）+ 控制台命令 + NPC 行为树 | ⏳ 后续 |

## 对标清单（机制 ↔ 实现）

| 原工程机制 | 复刻实现 | 状态 |
|---|---|---|
| 简单移动（FPC） | 服务端权威移动：输入协议 → Server MovementSystem → Replication | ✅（WASD/跳跃/偏航） |
| 第一/第三人称视图 | 代码原语胶囊体 + 第一人称相机（本地玩家） | ✅（无动画） |
| 简单武器（Shot/Reload/Hold to fire） | weapon:fire / weapon:reload 协议 + 服务端冷却/弹药/命中 | ✅（无动画/特效资源，弹道线为代码原语） |
| 简单生命 + 受击信息 | Health 组件 + `player:apply_damage` ModCall + 死亡/重生 + 复制 | ✅ |
| 伤害飘字 | 未做（UI.Mod 未接 UGUI，HUD 为 OnGUI 占位） | ⏳ |
| 背包/拾取/拖拽 UI | 未做（阶段 5） | ⏳ |
| 程序化地图（seed） | 未做（阶段 6） | ⏳ |
| 控制台命令 | 未做（阶段 6） | ⏳ |
| NetworkManager/HUD | ModRuntime 引导 + Host 自动启动（§11.3） | ✅ |
| PoolManager | ModObject PoolScope（本 MVP 未用到池） | — |
| SyncVar（生命/弹药） | ECS Replication（Archetype + Snapshot） | ✅ |
| Command/RPC | 稳定哈希协议（Rule 16）+ ModCall | ✅ |

## Mod 工程拆分（规范 Rule 4）

```
samples/MirrorUnityFPS/mods/
├── com.fps.player/    移动/生命/生成/复制 Archetype 注册
│   ├── mod.json       deps: core, network
│   ├── CONTRACT.md    协议/能力/事件契约（Rule 19）
│   └── src/           组件 / 协议 / 系统 / 能力导出 / PlayerMod / PlayerView
└── com.fps.weapon/    射击/换弹/弹药/命中/弹道
    ├── mod.json       deps: core, network, com.fps.player（ModCall 取位置/施加伤害）
    ├── CONTRACT.md
    └── src/           组件 / 协议 / WeaponSystem / WeaponMod / WeaponView
```

依赖 DAG：`com.fps.player ← com.fps.weapon`（weapon 经 ModCall 调 player 能力，§12.11）。

## 关键技术决策（自主）

1. **跨 Mod 位置/伤害**：武器 Mod 不引用玩家 Mod 类型（Rule 12 隔离）——命中判定经
   `player:get_all_positions` / `player:apply_damage` 能力调用（返回快照 DTO，§12.9 规则 1）。
2. **服务端权威**：开火原点取服务端位置（防客户端伪造）；命中在服务端 ray-sphere 判定。
3. **Replication 简化**：原型为全量 Snapshot@15Hz（无 ChangedOnly 脏标记），实体规模小可接受；
   脏增量/量化压缩留待后续（§11.10 已预留模式位）。
4. **单玩家 Host**：本地玩家 netId 经 `player:local` 广播下发；多客户端加入流程留待 Session 阶段。
5. **演示集成**：两个 Mod `boot=true` 进启动集（Play 即开局）；同时打包上架 mod_server 可商店分发。
   场景含 2 个静态靶标（Dummy）供射击验证。

## 运行

```bash
python runtime/build-mod.py            # 构建（含 samples/*/mods 扫描）
bash tests/run.sh                      # 无头测试（含 Replication/Player/Weapon e2e）
bash runtime/verify-unity.sh           # Unity 编译 + Play 冒烟（需先关闭编辑器）
# Unity Play：WASD 移动 / 空格跳 / 鼠标视角 / 左键射击 / R 换弹 / 击中红色靶标扣血致死
```
