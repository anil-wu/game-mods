# MirrorUnityFPS 样例复刻可行性分析

> 样例：`replica_projects/MirrorUnityFPS`（Mirror + Unity FPS Starter Assets，Unity 6.0.30f1）
> 问题：能否用 V2.0 框架（`design/Unity_Mod_Runtime_架构设计V2.0.md`）复刻？
> 结论：**可以，且 V2.0 架构比样例结构更适合长期演化；但需先补一块框架能力——ECS Replication（§11.10）。**

---

## 1. 样例工程解剖

### 1.1 功能清单

| 功能 | 实现方式 |
|---|---|
| 移动（FPS 控制器） | Starter Assets First Person Controller，Mirror 组网改造 |
| 武器（射击/换弹/连发/动画/特效） | `HeldBase / HeldRange / HeldMelee`（NetworkBehaviour），服务端权威 `FireHeld()` 射线检测 |
| 拾取 & 背包（UI 拖拽、武器槽） | `InventoryBase / PickupBase` + UGUI/TMP |
| 生命 & 受击信息 | `BaseCombatScript`（SyncVar currentHealth + ClientRpc 受击广播） |
| 伤害飘字 | Popup（本地表现） |
| 程序化地图 | `MapGenManager`：服务端按 seed 生成，客户端加入时同步 |
| 控制台命令 | 特性标记 + 源生成注册（`[ConCommand]`） |

### 1.2 架构风格（典型 Mirror 单体）

- **165 个脚本**，全部继承 `NetworkBehaviour`（`BaseScriptNetwork`），逻辑/表现/网络揉在同一组件里。
- **网络面**：`[SyncVar]`×3、`[Command]`×4、`[ClientRpc]`×4、`[TargetRpc]`×2、`[Server]`×22——
  同步语义散落在字段与方法特性上，没有统一协议层。
- **单例 ×17**：`MyNetworkManager.instance`、`MapGenManager.Instance`、`PoolManager`、`GameCore`（静态门面）——
  全局可达，无法隔离、无法热更、无法按功能独立开发。
- **对象池**：`PoolManager` + `Poolable` 全局池。
- **资源**：Addressables（`Assets/AddressableAssetsData`）。

这正是 V2.0 §0 要消解的结构："Runtime 提供能力，Mod 自己决定实现"的反面——基础设施与业务完全耦合。

---

## 2. 机制映射：样例 → V2.0 框架

| 样例机制 | V2.0 对应 | 现状 |
|---|---|---|
| `[Command]`（客户端请求服务端动作） | ClientToServer 协议（稳定哈希 ID，Rule 16） | ✅ 已实现（com.game.network） |
| `[ClientRpc]` / `[TargetRpc]` | ServerToClient 协议（Broadcast / SendToClient） | ✅ 已实现 |
| `[SyncVar]`（Health / 弹匣 / parentNetID） | **ECS Replication（§11.10：组件声明 `[NetworkReplicated]`，Snapshot/Delta + NetworkId↔Entity 映射）** | ❌ **未实现，关键缺口** |
| `NetworkServer.Spawn` / `NetworkIdentity` | Replication 的 Spawn/Despawn 协议（ArchetypeId + 初始组件集） | ❌ 随 Replication 补齐 |
| 服务端权威开火/命中（`[Server] FireHeld` 射线） | Server System + HitInfo 走 ModCall/协议；「网络消息是输入，ECS 是权威状态」（§11.10） | ✅ 模式已验证（推箱子） |
| `MyNetworkManager`（Host/Client 启动） | `NetworkConfig{Role, Path}` + Provider 装配（§11.3 Role×Path） | ✅ 已实现 |
| `PoolManager` 全局池 | ModObject 自己的 PoolScope（§9：池属于 Mod，非全局） | ✅ 已实现 |
| Addressables | Mod 自治资源管线 + ResourceScope（§8.2 Mod 自选 Addressables/Bundle） | ⚠️ 接口已有，Unity 后端未接 |
| 背包 UI / HUD / 拖拽 | UI.Mod WindowManager + 业务 Mod 出 Prefab/ViewModel（§10） | ⚠️ 注册表已实现，UGUI 绑定未接 |
| 控制台命令（`[ConCommand]` 源生成） | 独立 Mod 导出能力（ModCall），或 console Mod 订阅协议 | ✅ 有对应模式 |
| 地图 seed 同步（加入时全量下发） | 会话初始快照协议 + 数据驱动（seed → 两端确定性生成） | ✅ 协议可承载 |
| NPC 行为树（Thinking Network） | Server System（纯逻辑），表现层走 Client 视图 | ✅ ECS 可承载 |
| Starter Assets FPC（表现/输入） | Mod 自带 MonoBehaviour 视图（PushBoxView 模式已验证） | ✅ 已验证 |

---

## 3. 可行性结论

### 3.1 架构层面：完全匹配

样例的每个功能块都自然落在 V2.0 的 Mod 边界上，且复刻版在以下维度**优于**原样例：

- **隔离与独立开发**：武器/背包/地图各自是 Mod（自己的 ECS + 协议 + UI + 资源），而不是 165 个互相 RequireComponent 的组件；
- **热加载**：改武器数值/逻辑不用重发整包（HybridCLR 接入后）；
- **协议治理**：协议 ID 稳定哈希 + 方向强制 + 配额 + per-Mod 流量统计，替代散落各处的 SyncVar/RPC 特性；
- **UGC 就绪**：第三方按契约文档（Rule 19）就能写新武器 Mod，无需读宿主源码。

### 3.2 关键缺口（按优先级）

| # | 缺口 | 影响 | 说明 |
|---|---|---|---|
| 1 | **ECS Replication（§11.10）** | 移动/生命/弹匣/持有物等高频状态同步无法实现 | SyncVar 的等价物，**复刻前置**。含 NetworkId↔Entity 映射、Spawn/Despawn、Snapshot/Delta、量化压缩 |
| 2 | Unity 资源后端（Addressables/Bundle → IResourceBackend） | 美术资源（模型/动画/材质）无法按 Mod 加载 | 文件后端已有，Unity 后端需接 |
| 3 | UI.Mod ↔ UGUI 绑定 | 背包拖拽/HUD/飘字无法落地 | WindowManager 注册表已有，需把窗口实例挂到 Canvas 层级 |
| 4 | 会话/房间流程（join code、大厅） | 多人加入流程 | Relay + Session Service 已在架构内（§11.11），relay_server 已有雏形 |
| 5 | 场景/关卡生命周期 | 地图切换 | 可作为 mapgen Mod 内部事务，框架暂不需介入 |

### 3.3 非代码差异

- 样例用 **Unity 6**，我们是 **2022.3.62f3**——Starter Assets FPC 需替换（自研简单 FPS 控制器或移植，属 Mod 表现层内部事务，框架无感）。
- 美术资源（模型/动画/TMP）不在仓库讨论范围，复刻以**机制等价**为目标。

---

## 4. 复刻 Mod 划分（映射 §15.4 模块拆分）

```
com.fps.player      移动/生命/本地输入/相机
├── Shared:  PlayerState/Health 组件、move/damage 协议 Schema（+CONTRACT.md，Rule 19）
├── Client:  输入采集、FPS 控制器视图、HUD 血量显示
└── Server:  MoveSystem（权威校验）、HealthSystem、HitSystem

com.fps.weapon      武器（射击/换弹/弹匣/特效）
├── Shared:  WeaponState/Magazine 组件、fire/reload/hit 协议
├── Client:  枪模/动画/枪口特效/命中贴花（Pool 归本 Mod）
└── Server:  FireSystem（射线权威）、ReloadSystem（经 ModCall 查背包弹药，§12.9 模式 1）

com.fps.inventory   拾取/背包/武器槽
├── Shared:  Inventory/Item 组件、pickup/drop/move 协议
├── Client:  背包 UI（窗口声明给 UI.Mod）、拖拽 ViewModel、拾取描边
└── Server:  InventorySystem（权威）、PickupSystem；导出 inventory:* 能力供武器 Mod 调用

com.fps.mapgen      程序化地图
├── Shared:  seed 同步协议、地图数据 Schema
├── Client:  按 seed 确定性重建表现（桥/地形）
└── Server:  生成器（seed 权威），加入时全量下发

com.fps.console     控制台命令（可选）
└── Shared:  命令协议；Server: 解析 → ModCall 路由到各 Mod 能力
```

依赖 DAG：`player ← weapon ← inventory`，`mapgen` 独立，`console → *`（可选依赖）。

---

## 5. 建议迭代顺序

1. **阶段 0（框架）**：实现 ECS Replication（§11.10）——`[NetworkReplicated]` 组件声明、Spawn/Despawn、Snapshot/Delta、NetworkId↔Entity 映射。这是唯一硬性前置。
2. **阶段 1（MVP）**：`com.fps.player` + `com.fps.weapon`——移动同步 + 服务端权威射击 + 生命/死亡。对标样例的 1/2/3/5 项。
3. **阶段 2**：`com.fps.inventory` + HUD/飘字（UI.Mod 接 UGUI 绑定）。
4. **阶段 3**：`com.fps.mapgen`（seed 同步）+ 控制台 + NPC。

每阶段验收：`tests/run.sh` 全绿 + `verify-unity.sh` 通过 + 双端（Host + Client 或双 Host 回环）联调。
