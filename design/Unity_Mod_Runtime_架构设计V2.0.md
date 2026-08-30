# Unity Mod Runtime 架构设计 V2.0

> 技术栈：Unity + ECS + HybridCLR + Mirror + Host / Server / Client / Relay
> 目标：Mod 隔离、独立开发、可热加载 / 热卸载、面向 UGC 平台

---

## 0. 一句话概括

> **ModManager 管 Mod，ModObject 管一个 Mod 的生命周期，IMod 管注册/注销，ModContext 管能力访问，Resource / Pool / ECS / Network / UI / Message Runtime 提供底层能力，而具体资源引用、对象池实例和消息订阅都归属于对应的 ModObject。**

架构核心思想：

- **Runtime 提供能力，Mod 自己决定实现**——基础设施与业务模块内部实现彻底分开。
- **一个 Mod = 一个独立能力单元 = 一个 IMod = 一份 Manifest = 自己的代码 + 自己的 ECS + 自己的网络协议 + 自己的 UI + 自己的资源 + 自己的配置。**
- Mod 不再是"插件 DLL"，而是**一个拥有代码、ECS、网络协议、资源、配置，并通过稳定 ABI/API 与其他 Mod 协作的独立游戏功能包**。
- Core Runtime / Network Runtime / Asset Runtime / ECS Runtime 只提供基础设施，**不拥有业务 Mod 的内容**。

### 0.1 第一性原理：Mod 独立性

整套架构由一条公理推出——**各个 Mod 是独立的**。其余所有规则都是被它逼出来的必然，不是可选的风格偏好：

```
各个 Mod 独立（公理）
   │
   ├─► 隔离（Rule 12）：互相看不到类型/程序集/实例/静态——禁止直接引用、静态共享、反射、全局发现、传委托
   ├─► 只能字节通信（Rule 14）：看不到类型 → 能过界的只有 bytes → bytes 藏不了对象引用
   ├─► 契约即文档（Rule 19）：没有共享类型定义字节布局 → 布局写进文档、各自解析
   ├─► 可重复定义（§14.11）：消费方按文档各自写自己的解析——结构相同、定义独立、互不影响
   ├─► 自治（Rule 3/4）：自己的资源/协议/ECS/UI/池，归自己的 ModObject
   ├─► 可热卸载（Rule 20）：没有东西漏出边界 → 卸载 = ModObject 持有物整体强制回收，无残留
   └─► UGC 生态：第三方 Mod 不用信任也能装/卸——污染不了别人、泄漏不了、可随时下线
```

**独立性买到四样东西**：独立开发（各职能各管一个 Mod）、热加载/热卸载（Rule 20 无残留）、UGC 安全（隔离+配额+字节边界）、版本/依赖自治（独立 SemVer）。

> 守住"独立"这一条，后面的隔离、字节、文档契约都是必然。Rule 12 要求"每一条跨 Mod 访问都经过 Core 可治理的通道"，守的就是这个根。

### 0.2 Mod 的定义与硬性要求

**定义**：一个 Mod = 一个独立能力单元 = 一个 `IMod` + 一份 Manifest + 自己的代码 + 自己的 ECS + 自己的网络协议 + 自己的 UI + 自己的资源 + 自己的配置。它不是"插件 DLL"，而是一个通过稳定 ABI 与其他 Mod 协作的独立游戏功能包。

**硬性要求**（一个东西要称为 Mod，必须满足；与 §16 Rule 一一对应）：

1. **唯一入口**：实现 `IMod`（仅 `Register`/`Unregister`），经 ModManager 统一加载/卸载（Rule 1）。
2. **能力经 Context**：一切能力访问走 `IModContext`，不直接持有 Runtime 全局对象（§5）。
3. **资源/协议自治**：自己的资源、池、ECS、协议归自己的 ModObject（Rule 3/4）。
4. **完全隔离**：跨 Mod 只走四条 Core 中介通道（ModCall / Message / UI / Resource）；禁止直接引用、静态共享、反射、全局发现、传委托（Rule 12）。
5. **通信二进制**：跨 Mod 数据一律字节（消息 / ModCall / 协议同一套线格式），bytes 不藏对象引用（Rule 14）。
6. **契约不共享、可重复定义**：不共享数据结构程序集；消费方按 owner 发布的契约文档各自实现解析——结构相同、定义独立（Rule 19 / §14.11）。
7. **依赖声明**：一切跨 Mod 依赖在 Manifest 声明，DAG、SemVer、Client/Server Scope（Rule 7/8）。

---

## 1. 总体分层

```
Game
│
├── Core
│   ├── ModRuntime      (ModManager / ModLoader / ModResolver)
│   ├── ECS Runtime
│   ├── Network Abstractions
│   ├── UI Abstractions  (IUiContext，详见 §10)
│   ├── Message Runtime  (MessageBus，详见 §13)
│   └── Resource Runtime
│
├── UI.Mod               (UI 基础设施：WindowManager，详见 §10)
├── Network.Mod          (网络基础设施，详见 §11)
│
├── Weapon.Mod           ├── Code / Config / Prefab / Material
├── Vehicle.Mod          │   Texture / Animation / Audio / Scene
└── Quest.Mod            └── ...
```

```
Game Runtime
                           │
                    ┌──────┴──────┐
                    │             │
                 ModRuntime    ECS Runtime
                    │
          ┌─────────┼─────────┐
          │         │         │
      Weapon.Mod Vehicle.Mod Quest.Mod
          │         │         │
       ┌──┴──┐   ┌──┴──┐   ┌──┴──┐
       │     │   │     │   │     │
     ECS   Net  ECS   Net ECS   Net
       │     │   │     │   │     │
     Asset Pool Asset Pool Asset Pool
       │     │   │     │   │     │
       └─────┴───┴─────┴───┴─────┘
```

---

## 2. Core 的最终组成

```
Core
│
├── ModManager
├── ModLoader
├── ModResolver
├── ModObject
├── ModContext
├── ResourceRuntime
├── PoolRuntime
├── ECSRuntime
└── Runtime Services
```

注意：**UIRuntime（WindowManager）不在 Core**，它与 Network.Mod 一样是基础设施 Mod（UI.Mod，详见 §10）；Core 只保留 `IUiContext` 抽象接口。

| 对象 | 职责 |
|---|---|
| ModManager | 管理所有 Mod（生命周期真正管理者） |
| ModLoader | 加载 / 卸载 Mod |
| ModResolver | 处理依赖 / 版本 |
| ModObject | 一个 Mod 的运行时实例（资源边界） |
| ModContext | Mod 的能力边界 |
| ResourceRuntime | 底层资源加载能力 |
| PoolRuntime | 底层对象池能力（PoolId + 非泛型 Rent/Return 基础设施，见 §15.3） |
| IMod | Mod 唯一入口 |

**Core 只提供非常底层的能力：**

```
Core Runtime
│
├── File / Storage
├── Memory
├── Thread
├── ECS Runtime
├── Network Runtime
└── Mod Runtime
```

**Core 中不要出现：**

```
Core
├── GlobalAssetManager          ❌
├── GlobalPrefabPool            ❌
└── GlobalModResourceManager    ❌
```

否则所有 Mod 会耦合到 Core。

---

## 3. IMod —— 极简统一入口

所有 Mod（包括 Network Mod 这类基础设施 Mod）都必须通过统一的 `IMod` 生命周期接口进入 Mod Runtime。

```csharp
public interface IMod
{
    void Register(IModContext context);
    void Unregister(IModContext context);
}
```

**只有两个方法：`Register` / `Unregister`。不要增加：**

```
Initialize()  Start()  Stop()  Update()  Tick()  OnEnable()  OnDisable()   ❌
```

原因：

- Mod Runtime 负责生命周期，Mod 自己不控制整个生命周期。
- `IMod` 越小越稳定，它就是整个 HybridCLR Mod ABI 的核心入口；具体生命周期、Tick、ECS System、Network Handler 都交给各自 Runtime 管理。
- `Register()` 的核心不是"启动游戏逻辑"，而是**向 Game Runtime 声明并注册自己提供的能力**。

Mod 自己只实现：

```csharp
public sealed class WeaponMod : IMod
{
    public void Register(IModContext context)
    {
        // 注册自己的能力
        context.Ecs.Register(...);
        context.Network.RegisterProtocol(...);
        context.Services.Register(...);
        context.Ui.Register(...);
    }

    public void Unregister(IModContext context)
    {
        // 对称注销
        context.Ui.Unregister(...);
        context.Network.UnregisterProtocol(...);
        context.Services.Unregister(...);
        context.Ecs.Unregister(...);
    }
}
```

**重要规则：`Register()` 必须是可验证的、尽可能无业务副作用的注册阶段。**

✅ 可以：

```csharp
context.Network.RegisterProtocol(...);
context.Network.RegisterHandler(...);
context.Ecs.RegisterSystem(...);
context.Ui.Register(...);
```

❌ 不建议：

```csharp
StartGame();  SpawnPlayer();  ConnectServer();  StartThread();  StartCoroutine(...);
```

> **原则：Mod 负责声明，Runtime 负责执行。**

---

## 4. ModObject —— 真正的"资源边界"

`IMod` 是代码入口，`ModObject` 是 Core 对 Mod 的运行时实例，由 Core 创建：

```csharp
public sealed class ModObject
{
    public ModInfo Info { get; }
    public IMod Instance { get; }
    public IModContext Context { get; }
    public IModResourceScope Resources { get; }
    public IModObjectPool Pool { get; }
}
```

一个 ModObject 的完整内容示例：

```
Weapon.ModObject
│
├── IMod
│
├── Context
│
├── Resources
│   ├── Sword Prefab
│   ├── Sword Texture
│   ├── Sword Material
│   └── Sword Animation
│
├── Pools
│   ├── Sword
│   ├── Bullet
│   └── VFX
│
├── ECS
│   ├── Components   （类型注册常驻，见 §9.2）
│   ├── Systems      （owner 化，卸载移除）
│   └── Entities     （owner 化，卸载随 ModObject 强制销毁，§9.2）
│
├── Views            （Unity 场景对象：MonoBehaviour 视图，owner 化，卸载即销毁）
│
└── Network
    ├── Protocols
    └── Handlers
```

> **资源边界的完整定义**：Mod 对 Runtime 的每一项持有物（系统 / 订阅 / 协议 / 窗口 / 服务 / 能力 / 池 / 资源 / **实体** / **视图**）都必须归属 ModObject——"谁注册谁归属，归属即可强制回收"。这是卸载机制（§7）成立的前提。

于是：

```
Unload Weapon.ModObject
             │
             ▼
       全部资源释放
       全部 Pool 清理
       全部注册注销
       全部引用解除
```

这就是非常干净的 **Mod Scope**。

**Mod 内部结构（一个完整的自治单元）：**

```
Weapon.Mod
│
├── IMod
│
├── ECS
│   ├── Components
│   └── Systems
│
├── Network
│   ├── Protocol
│   ├── Encode
│   └── Decode
│
├── UI
│
├── Resource
│   ├── ResourceManager
│   ├── AssetReference
│   ├── AssetLoader
│   └── ObjectPool
│
├── Gameplay
│
└── Assets
    ├── Prefabs
    ├── Textures
    ├── Materials
    ├── Animations
    └── Audio
```

---

## 5. IModContext —— 能力边界

既然 `IMod` 只有两个方法，真正的能力入口通过 `IModContext` 提供：

```csharp
public interface IModContext
{
    IModInfo Info { get; }
    IModManager Mods { get; }
    IModResourceScope Resources { get; }
    IModPoolScope Pool { get; }
    IEcsContext Ecs { get; }
    INetworkContext Network { get; }
    IUiContext Ui { get; }
    IServiceContext Services { get; }
    IMessageContext Messages { get; }
    IClientContext? Client { get; }
    IServerContext? Server { get; }
}
```

这样：

- Mod 的所有操作天然带 Scope。
- Mod 不需要直接访问 `Unity` / `Mirror` / `HybridCLR` / `Network Transport`。
- ModContext 自动绑定自己的资源空间：

```
context.Info.Id      →  com.game.weapon
context.Resources    →  Weapon Asset Namespace

WeaponAssets.Sword(context)                →  实际解析为 com.game.weapon:sword（生成代码，见 §15.3）
```

**严格规则：Mod 不允许直接持有 Runtime 全局对象。**

```csharp
Game.Instance                                   ❌
NetworkManager.singleton                        ❌
World.DefaultGameObjectInjectionWorld           ❌
```

Mod 只能通过 `IModContext` 获取能力——这样才能真正实现 **Mod Isolation**。

---

## 6. Client / Server / Host

**Client / Server / Host 是 Context 能力，不是不同的 Mod。** 不要拆成 `WeaponClientMod` / `WeaponServerMod` 两个入口。

Host 本质上同时运行 Client + Server 两套逻辑，所以用**能力判断**而不是简单枚举：

```csharp
public interface IModContext
{
    bool HasClient { get; }
    bool HasServer { get; }
    IClientContext? Client { get; }
    IServerContext? Server { get; }
    ...
}
```

| 运行环境 | HasClient | HasServer |
|---|---|---|
| Client | true | false |
| Server | true→false | false→true |
| Host | true | true |

```csharp
public sealed class WeaponMod : IMod
{
    public void Register(IModContext context)
    {
        RegisterShared(context);
        if (context.HasClient) RegisterClient(context.Client!);
        if (context.HasServer) RegisterServer(context.Server!);
    }

    public void Unregister(IModContext context)
    {
        // 对称注销
    }
}
```

```
WeaponMod
                       │
                 Register(context)
                       │
             ┌─────────┴─────────┐
             │                   │
        HasClient=true      HasServer=true
             │                   │
             ▼                   ▼
       Client Systems       Server Systems
       Client UI            Server Logic
       Client Network       Server Network
             │                   │
             └─────────┬─────────┘
                       ▼
                      Host
```

不需要 `WeaponClientMod` / `WeaponServerMod`——程序集层面的 Shared / Client / Server 模块拆分与按环境裁剪加载见 §15.4。

后续甚至可以把 `ClientContext` / `ServerContext` 做成**权限边界**，让 Mod 根本无法在 Client 环境访问 Server-only API。

### 退出以 ModObject 为原子粒度（Host 前后端同时退出）

Client / Server 是同一 ModObject 的两套上下文——**卸载以 ModObject 为原子单位，前后端持有物同帧清零**，禁止"退了画面留了模拟"：Server 侧（权威系统 / 权威实体 / C2S 协议）与 Client 侧（S2C 协议 / 本地落地状态 / 订阅 / 视图）走同一条卸载管线（§7）。未来 Shared/Client/Server 三模块裁剪（§15.4）只影响"装什么"，不影响"退什么"。

### 资源生命周期仍然属于 ModObject，而不是 Client/Server 各搞一套

```
Client                        Server                        Host
ModObject                     ModObject                     ModObject
├── Client Context            ├── Server Context            ├── Client Context
├── Resources                 ├── Resources                 ├── Server Context
└── Pool                      └── Pool                      ├── Resources
                                                            └── Pool
```

### Server 资源和 Client 资源可以进一步分 Scope

Host 情况下 `Weapon.ModObject` 同时存在 Client + Server：

```
Weapon.ModObject
│
├── SharedScope
│
├── ClientScope
│   ├── Prefab
│   ├── Texture
│   ├── Animation
│   └── VFX
│
└── ServerScope
    ├── Config
    └── Server Data
```

这样 Host 不会因为 Server 逻辑而加载不必要的 Client 美术资源。

---

## 7. 生命周期（最终定义）

`IModManager`：

```csharp
public interface IModManager
{
    ModObject Load(ModId id);
    void Unload(ModId id);
    ModObject Get(ModId id);

    // 退出游戏 / 停止会话：按加载顺序的镜像销毁全部 Mod（依赖方先卸，pinned 除外，§10.4）
    List<string> UnloadAll();

    // 动态安装入口（Mod 商店等管理型 Mod 专用）：注册包目录 / 从包目录加载
    ModManifest RegisterDirectory(string modDirectory);
    ModObject LoadFromDirectory(string modDirectory);

    // Mod 间能力调用（跨 Mod 唯一的有返回通道，见 §12.11）
    void Export(CapabilityId id, Delegate handler);          // 当前 Mod 导出能力
    object Call(ModId target, CapabilityId id, in object args);
}
```

### 加载

```
Load(Mod)
 │
 ├── Load Manifest
 ├── Resolve Dependencies
 ├── Load Assembly
 ├── Create ModObject
 ├── Create ModContext
 ├── Create ResourceScope
 ├── Create PoolScope
 └── IMod.Register()
```

### 卸载

```
Unload(Mod)
 │
 ├── 0. 泄漏统计（各项注册计数 → 卸载报告，反映 Mod 是否对称注销）
 ├── 1. IMod.Unregister()              ← Mod 自觉：对称注销 + 清静态桥
 ├── 2. WindowManager.CloseAll(modId)  ┐
 ├── 3. MessageBus.UnsubscribeAll(modId)│
 ├── 4. Remove Network Registration    │ 先停“生产者”
 ├── 5. Remove ECS Systems(modId)      │（系统 / Handler / 视图）
 ├── 5.1 World.DestroyAll(modId)       │ 销毁 Mod 全部实体（须在系统移除后，§9.2）
 ├── 5.2 DestroyViews(modId)           ┘ 销毁场景视图 GameObject
 ├── 6. Remove Services / Capabilities ┐
 ├── 7. Clear ObjectPool               │ 再清“状态”
 ├── 8. Release ResourceScope          ┘
 ├── 9. Dispose Context                ┐ 最后失效化：残留引用即抛异常（非野指针）
 ├── 10. Destroy ModObject             │
 └── 11. Unload Assembly               ┘（HybridCLR 前为“同副本重入”，§15.2）
```

**顺序原则（硬约束）：先停生产者（系统 / Handler / 视图）→ 再清状态（实体 / 池 / 资源）→ 最后失效化（Context / 程序集）。**

**相位规则**：驱动相位内（系统 Update / 协议 Handler / 消息 Handler 执行中）调用 `Unload` 不立即执行——请求入队，当前相位结束（帧末）统一执行，避免迭代途中修改系统/实体集合导致崩溃（与 §13.6 规则 3 同构）；相位外调用同步执行。

**前置校验**：目标未加载 → `NoModException`；目标为 pinned 主 Mod → 拒绝（§10.4）；仍被其他已加载 Mod 依赖 → 拒绝（先卸依赖方，DAG 镜像序）。

**卸载报告**：管线结束输出强制回收计数（系统 / 订阅 / 协议 / 服务 / 能力 / 实体）——被 Core 强制回收 > 0 即 Mod 未对称注销，是 UGC 生态的质量信号与审核指标。

### 完整生命周期图

```
                    ModManager
                        │
                     Load Mod
                        │
                        ▼
                  Dependency Resolve
                        │
                        ▼
                    Create ModObject
                        │
             ┌──────────┼──────────┐
             │          │          │
          Context    Resources    Pool
             │          │          │
             └──────────┼──────────┘
                        │
                        ▼
                 IMod.Register()
                        │
                        ▼
                     Running
                        │
                        ▼
                IMod.Unregister()
                        │
             ┌──────────┼──────────┐
             │          │          │
          ECS注销   Network注销  UI注销
             │          │          │
             └──────────┼──────────┘
                        │
                  Pool.Clear()
                        │
                  Resources.Release()
                        │
                        ▼
                Destroy ModObject
                        │
                        ▼
                  Unload Assembly
```

### 热卸载流程

```
ModObject.Dispose()
       ↓
业务注册全部解除
       ↓
Pool 全部清空
       ↓
ResourceScope 全部释放
       ↓
Context 失效
       ↓
Assembly 卸载
```

---

## 8. 资源系统

### 8.1 ResourceScope ≠ ResourceManager

Core 提供 **Resource Runtime**，负责底层真正的资源加载机制；但**每个 Mod 拥有自己的 ResourceScope**：

```
Resource Runtime
                       │
             ┌─────────┼─────────┐
             │         │         │
          Scope A    Scope B    Scope C
             │         │         │
          Weapon     Vehicle     Quest
```

Core 仍然统一管理：Addressables / AssetBundle / Memory / Async Loading，但**资源生命周期归属于 `ModObject.Resources`**。

### 8.2 Mod 自治：资源技术由 Mod 自己决定

```
Weapon.Mod     → Addressables
Vehicle.Mod    → AssetBundle
Character.Mod  → YooAsset
UGC.Mod        → Custom Binary Resource
```

甚至可以同时存在。只要最终对外提供的能力符合 Mod API 即可。这对 UGC 平台非常重要。

```
IMod
  │
  └── Mod Resource Runtime
          │
          ├── Addressables
          ├── AssetBundle
          ├── YooAsset
          └── Custom
```

Core 不关心。未来换资源管线，Mod 不需要修改。

### 8.3 AssetId 与命名空间

资源 ID 使用 `{ModId}:{AssetPath}`：

```
weapon:sword
weapon:sword.prefab
weapon:characters/warrior
weapon:animations/attack

vehicle:car
vehicle:truck
```

天然形成 Namespace。**资源不能直接通过 Unity 路径访问**：

```csharp
Resources.Load("Weapon/Sword");        ❌
AssetDatabase.LoadAssetAtPath(...);    ❌
File.ReadAllBytes(...);                ❌

context.Resources.Load(new AssetId("weapon:sword"));           ✅（非泛型，返回 object）
WeaponAssets.Sword(context);                                   ✅（生成的强类型包装，见 §15.3）
```

### 8.4 三层资源权限

```
Resource Runtime
                        │
             ┌──────────┼──────────┐
             │          │          │
          Private     Public    Shared
             │          │          │
          Mod资源    Mod API     公共资源
```

- **Private**：如 `weapon:sword`，只有 Weapon.Mod 能访问。
- **Public**：Weapon Mod 暴露 `IWeaponService`，其他 Mod 使用服务，而不是直接拿 Weapon Prefab。
- **Shared**：如 `Core:UI` / `Core:Font` / `Core:CommonMaterial`，由 Core 管理。

**跨 Mod 默认禁止直接访问资源**：

```
Weapon.Mod
    ↓
Vehicle.Mod/super_car.prefab        ❌ 不允许
```

如果确实需要，必须通过 API/Service：

```csharp
VehicleServiceRef.Get(context);                // 推荐（生成的非泛型包装，见 §15.3）
// 或
context.Assets.Resolve("com.game.common:icons/sword");
```

> **跨 Mod 不直接共享资源，通过 API/Service 共享能力。**

依赖资源时 Resource Runtime 知道 `Weapon ↓ Common ↓ Asset`，从而保证 Common 没有被卸载时 Weapon 仍然可以使用它的资源。

### 8.5 Mod 内部资源分三类

```
Mod Resources
│
├── Static
│   ├── Config
│   ├── Texture
│   └── Material
│
├── Runtime
│   ├── Prefab
│   ├── Animation
│   └── Audio
│
└── Pooled
    ├── Bullet
    ├── VFX
    └── UI
```

生命周期：

```
Static     →  Mod Load
Runtime    →  按需 Load / Release
Pooled     →  Load ↓ Pool ↓ Rent / Return ↓ Clear
```

### 8.6 资源引用由 Mod 自己定义

```csharp
public readonly struct WeaponAssetRef
{
    public readonly string Id;
    public WeaponAssetRef(string id) { Id = id; }
}
```

Mod 可以定义 `WeaponAssetRef` / `VehicleAssetRef` / `CharacterAssetRef`，甚至 `WeaponPrefabRef` / `WeaponIconRef` / `WeaponVfxRef`。

### 8.7 资源释放边界

不要：

```csharp
Resources.UnloadUnusedAssets();      ❌ 会影响其他 Mod
```

应该：

```csharp
context.Assets.Release(asset);
// 或
context.Assets.ReleaseScope();
```

最终由 Resource Runtime 决定什么时候真正释放 Unity Object。

### 8.8 Resource Scope 示例

```csharp
// Core ABI：全非泛型（§15.3）
public interface IAssetContext
{
    object Load(AssetId id);
    void Release(AssetId id);
    IAssetScope CreateScope();
}
```

```csharp
var scope = context.Assets.CreateScope();
var prefab = (GameObject)scope.Load(new AssetId("weapon:sword"));
var icon   = (Sprite)scope.Load(new AssetId("weapon:sword_icon"));
// 或使用生成的强类型包装：WeaponAssets.Sword(scope)

// 卸载
scope.Dispose();
```

```
Weapon.Mod
     │
     ▼
AssetScope
 ├── Sword Prefab
 ├── Sword Icon
 ├── Sword Material
 └── Sword Animation
     │
     ▼
Dispose → Release
```

这对 Mod 热加载/卸载非常重要。

---

## 9. 对象池

### 9.1 池属于 Mod

Core 提供池基础设施（PoolId + 非泛型 Rent/Return，见 §15.3），但 **ModObject 拥有自己的 PoolScope**：

```
ModObject
│
└── PoolScope
      │
      ├── BulletPool
      ├── VfxPool
      └── CharacterPool
```

```csharp
var bullet = (Bullet)mod.Pool.Rent(new PoolId("weapon:bullet"));
// 或使用生成的强类型包装：WeaponPools.RentBullet(mod)
// 卸载
mod.Pool.Clear();
```

Core 不需要知道 Weapon 有多少 Bullet Pool。池规模由 Mod 自己决定：

```
Pool
│
├── Sword    ├── 10 instances  └── 50 instances
├── Bow      ├── 5 instances   └── 30 instances
└── Bullet   ├── 100 instances └── 500 instances
```

而不是 `GlobalObjectPool` 管理所有 Mod。

### 9.2 边界：GameObject Pool vs ECS Entity

**Mod 可以拥有自己的对象池，但不能直接管理 ECS World 生命周期。**

```
Weapon.Mod
   │
   └── WeaponPool          ✅

Weapon.Mod
   │
   └── Create World         ❌ 不建议
```

ECS World 属于 Game Runtime，Mod 只注册 `Components` / `Systems` / `Queries`，否则多个 Mod 会开始各自创建 Runtime。

```
GameObject Pool  属于 Mod        → VFX / UI / Prefab / Particle / Audio
ECS Entity       由 ECS Runtime  → EntityManager
```

Mod 可以封装自己的 `WeaponEntityFactory`，但不应该自己实现一个 `EntityManager`。

**ECS 归属与回收（硬规则，pushbox 案例实证）：**

1. **实体 owner 化**：Mod 经 `context.Ecs.CreateEntity()` 创建的实体归属当前 ModObject，卸载时 `World.DestroyAll(modId)` 强制销毁（§7 步骤 5.1）——不销毁会导致旧 Session/旧实体残留共享 World，重装后视图/模拟挂到僵死实体上（"操作无反应"事故）。
2. **组件类型注册常驻**：组件存储是类型键全局结构，实体存活时移除类型不安全——类型不按 Mod 移除（无状态、无害），实体强制销毁已覆盖实际危害。
3. **系统注册 owner 化**：`SystemGroup` 按 owner 追踪，卸载 `RemoveAll(modId)`——必须先于实体销毁执行（先停生产者，再清状态）。

---

## 10. UI 系统（UIManager 也是一个 Mod）

与 Network.Mod 同理：**Core 只保留 `IUiContext` 抽象接口（AOT 稳定 ABI），WindowManager 的实现作为 UI.Mod 存在**——UI 系统不是 Core 的一部分，而是一个基础设施 Mod。

```
Core (AOT)
│
├── ModRuntime (ModManager / ModLoader / ModResolver)
├── 抽象接口层: IMod / IModContext / IUiContext / ...
└── 最底层: File / Memory / Thread

Framework Mods (基础设施，HybridCLR)
│
├── UI.Mod        ← WindowManager 实现
├── Network.Mod   ← 网络基础设施
└── ...

Gameplay Mods
│
├── Weapon.Mod
├── Vehicle.Mod
└── Quest.Mod
```

这样做的好处：

- **Core 进一步变薄**：Core 不认识任何 UI 实现，只认识 `IUiContext` 抽象；
- **UI 系统可热更新**：WindowManager 自身走 HybridCLR，改 UI 框架不需要发整包；
- **UI 技术栈可替换**：UGUI / UIToolkit / FairyGUI 只是 UI.Mod 的不同实现，业务 Mod 无感知。

### 10.1 职责划分

| 能力 | UI.Mod（WindowManager） | 业务 Mod | Core |
|---|---|---|---|
| Window 注册表 / 生命周期调度 | ✅ | 声明窗口 | 只提供 IUiContext 抽象 |
| 层级管理（Layer / SortingOrder） | ✅ | 只声明目标层级 | ❌ |
| 窗口栈 / 返回导航（Back Stack） | ✅ | ❌ | ❌ |
| 焦点 / 模态 / 全局遮罩 | ✅ | ❌ | ❌ |
| 打开 / 关闭动画的调度 | ✅ 基础设施 | 提供动画实现 | ❌ |
| 窗口 Prefab / 图集 / 字体等资源 | ❌ | ✅（ResourceScope） | ❌ |
| ViewModel / 数据绑定 / 业务逻辑 | ❌ | ✅ | ❌ |
| 窗口实例 | 管引用与句柄 | 实例归属 Mod 的 UI Pool | ❌ |
| 窗口 → ModObject 归属追踪 | ✅ | ❌ | 提供 ModManager 查询 |

### 10.2 UI.Mod 遵循 IMod

```csharp
public sealed class UiMod : IMod
{
    public void Register(IModContext context)
    {
        if (!context.HasClient) return;   // Server/Service 环境退化为空

        var windowManager = new WindowManager(context.Mods);
        WindowManagerServiceRef.Register(context, windowManager);   // 生成的非泛型包装（§15.3）
    }

    public void Unregister(IModContext context)
    {
        // 关闭所有窗口、释放 UI.Mod 自己的资源
    }
}
```

业务 Mod 的 `context.Ui` 本质上是 **ModContext 对 `IWindowManager` 服务的路由/代理**：

```
Weapon.Mod
    │
    ▼
context.Ui.Open("weapon:settings")
    │
    ▼
IWindowManager (由 UI.Mod 注册)
    │
    ▼
WindowManager (UI.Mod 内部实现)
```

业务 Mod 在 Manifest 中声明依赖：

```json
{ "id": "com.core.ui", "version": "^1.0.0" }
```

### 10.3 与 Network.Mod 完全同构

UI.Mod 与 Network.Mod 是**同一个模式的两个实例**——Framework Mod（基础设施 Mod）：

| 维度 | Network.Mod | UI.Mod |
|---|---|---|
| Core 中的抽象 | `INetworkContext` / Network.Abstractions | `IUiContext` / UI.Abstractions |
| 实现归属 | HybridCLR，Network.Mod | HybridCLR，UI.Mod |
| 底层可替换 | Mirror / Relay / 未来网络栈 | UGUI / UIToolkit / FairyGUI |
| 业务 Mod 依赖 | 只依赖 Network.Abstractions | 只依赖 UI.Abstractions |
| 入口 | `NetworkMod : IMod` | `UiMod : IMod` |
| 业务 Mod 提供 | 协议 + Encode/Decode | 窗口声明 + Prefab/ViewModel |
| Framework Mod 负责 | 数据怎么安全高效到达 | 窗口什么时候开、在哪层、怎么关 |

```
业务 Mod
    │
    ├── UI.Abstractions         (AOT 稳定 ABI)
    │         │
    │         ▼
    │      UI.Mod  ──→  UGUI / UIToolkit / FairyGUI
    │
    └── Network.Abstractions    (AOT 稳定 ABI)
              │
              ▼
           Network.Mod  ──→  Mirror / Relay
```

业务 Mod 只依赖非常小的 `UI.Abstractions` / `Network.Abstractions`，**永远不引用实现**——替换 UI 框架或网络栈时业务 Mod 零改动。这对 HybridCLR 避免复杂 AOT 依赖同样有利。

### 10.4 Framework Mod 的特殊规则

UI.Mod、Network.Mod 这类**基础设施 Mod** 与普通业务 Mod 地位不同：

1. **加载顺序最前**：ModResolver 保证 Framework Mod 先于依赖它们的业务 Mod 注册；
2. **默认禁止热卸载**：卸载 UI.Mod 意味着整个 UI 系统下线，必须先卸载全部依赖它的 Mod——实际中等同于"只能整体停服级操作"。该约束由 Manifest 的 **`"pinned": true`** 字段形式化：pinned Mod 的 `Unload` 直接被 Core 拒绝，退出游戏 `UnloadAll` 时跳过（随进程生命周期存活）。主 Mod（如 com.game.core）与框架 Mod（Network.Mod / UI.Mod）一律 pinned；
3. **环境裁剪**：UI.Mod 只在 `HasClient` 环境生效，Dedicated Server 不加载或注册为空实现。

> 该规则对 Network.Mod、UI.Mod 以及未来的其他 Framework Mod（如 Asset.Mod）统一适用。

### 10.5 WindowId 与命名空间

与 AssetId 一致，窗口 ID 使用 `{ModId}:{WindowPath}`：

```
weapon:settings
weapon:crosshair
inventory:bag
```

WindowManager（UI.Mod）天然知道**每个窗口属于哪个 ModObject**（通过 ModManager 查询）——这是热卸载能干净的关键。

### 10.6 IUiContext（Core 中的稳定 ABI）

```csharp
public interface IUiContext
{
    // 声明窗口（在 IMod.Register() 中调用）
    void RegisterWindow(WindowDescriptor descriptor);

    // 打开 / 关闭
    IWindowHandle Open(WindowId id, WindowOptions options = default);
    void Close(WindowId id);

    // 查询
    bool IsOpen(WindowId id);
}
```

```csharp
public void Register(IModContext context)
{
    context.Ui.RegisterWindow(new WindowDescriptor
    {
        Id = "weapon:settings",
        Layer = UiLayer.Normal,      // Background / Normal / Popup / Top / System
        Prefab = "settings_panel",   // 从本 Mod 的 ResourceScope 解析
        Poolable = true              // 实例走本 Mod 的 UI Pool
    });
}
```

### 10.7 结构

```
UI.Mod
│
└── WindowManager
      │
      ├── Layer 管理        (Background / Normal / Popup / Top / System)
      ├── Window Stack      (导航 / Back)
      ├── Focus / Modal
      │
      └── Windows
            │
            ├── weapon:settings    ──→ Weapon.ModObject
            │     ├── Prefab  ← Weapon.ResourceScope
            │     └── Instance ← Weapon.Pool (UI Pool)
            │
            └── inventory:bag      ──→ Inventory.ModObject
```

**关键：窗口实例由业务 Mod 的资源域创建和回收，UI.Mod 只持有句柄。** WindowManager 不 Instantiate 资源本身，而是通过 Mod 的 ResourceScope/Pool 取得实例，再挂到 UI 层级节点下。

### 10.8 生命周期绑定 ModObject

窗口生命周期必须是 Mod 生命周期的子集，卸载流程为：

```
Unload(Mod)
 │
 ├── IMod.Unregister()
 ├── WindowManager.CloseAll(modId)   ← ModManager 调用 UI.Mod 强制执行
 ├── MessageBus.UnsubscribeAll(modId)
 ├── Remove Network Registration
 ├── Remove ECS Registration
 ├── Remove UI Registration
 ├── Clear ObjectPool                ← 窗口实例归还 UI Pool
 ├── Release ResourceScope
 ├── Dispose Context
 ├── Destroy ModObject
 └── Unload Assembly
```

- `CloseAll(modId)` 由 ModManager 在卸载流程中**主动调用 UI.Mod 完成**，不依赖业务 Mod 自觉——即使 Mod 忘了关窗口，热卸载后也不会残留指向已卸载 Assembly 的窗口引用。
- 卸载完成后校验该 Mod 的窗口引用计数清零。

### 10.9 三条硬规则

1. **禁止 Mod 绕过 WindowManager 直接把窗口 Instantiate 到全局 Canvas**——窗口必须经 `IUiContext` 注册和打开，否则无法追踪归属，热卸载就会漏。
2. **跨 Mod 不直接访问窗口**：其他 Mod 想打开 Weapon 的设置界面，走消息或 Service（`IWeaponUiService.ShowSettings()`），而不是引用 `WeaponSettingsWindow` 类型。
3. **UI.Mod 不认识任何具体窗口**——ViewModel、数据绑定、界面逻辑全在业务 Mod 内；WindowManager 只管"什么时候开、在哪一层、谁先谁后、什么时候必须关"。

---

## 11. Network Mod —— 游戏网络基础设施层

### 11.1 定位

> **Network Mod 是所有 Mod 共享的、AOT 稳定的网络运行时。业务 Mod 只负责定义"我有什么数据、协议是什么以及如何 Encode/Decode"，而 Network Mod 负责把这些数据安全、可靠、高效地从一个运行端传到另一个运行端。**

> Network.Mod 与 UI.Mod 同属 **Framework Mod（基础设施 Mod）** 模式，共性规则（抽象/实现分离、加载顺序、禁止热卸载、环境裁剪）见 §10.3 / §10.4。

业务 Mod 负责"数据是什么"，Network Mod 决定"数据怎么走"，Network Provider 决定"底层怎么传"，ECS 决定"数据最终如何影响游戏状态"。

Network Mod 是**整个游戏唯一的网络入口和网络出口**：Mirror、Relay、加密、压缩、分包、重组、可靠传输、流量统计、限流、ECS Replication 都**不应该**成为业务 Mod 的依赖；业务 Mod 最终只依赖非常小的 `Network.Abstractions`。这对 HybridCLR 尽量避免 AOT 泛型和复杂 AOT 依赖也非常有利。

> **硬边界：Network Mod 可以路由任何协议，但绝不拥有任何 Gameplay Protocol。** 它只认识 `ProtocolId / Version / Encode / Decode / Handler / OwnerMod`，不知道 `FireWeapon` 是什么——否则它会腐化成"全局消息定义中心"。

### 11.2 结构

```
Network Mod
│
├── Core
│   ├── NetworkRuntime
│   ├── NetworkContext
│   └── ConnectionManager
│
├── Session
│   ├── CreateSession / JoinSession（房间码）
│   └── Discovery（云端 Session Service）
│
├── Protocol
│   ├── ProtocolRegistry
│   ├── ProtocolRouter
│   └── ProtocolNegotiation（版本协商）
│
├── Channel
│   ├── ModChannel（按 Mod 隔离）
│   └── Interest（按玩家 / 区域 / 实体过滤）
│
├── Pipeline
│   ├── Encode / Decode 调度
│   ├── Validation
│   └── Dispatch
│
├── Security
│   ├── Authentication
│   ├── Encryption / Decryption
│   ├── Integrity
│   └── Replay Protection
│
├── Packet
│   ├── Packetization
│   ├── Fragmentation / Reassembly
│   ├── Sequence / ACK / Retry
│   └── Compression
│
├── Traffic
│   ├── Connection / Mod / Protocol 三级统计
│   └── Quota / Throttle
│
├── Replication
│   ├── Snapshot / Delta
│   ├── Spawn / Despawn
│   └── NetworkId ↔ Entity 映射
│
└── Provider
    ├── Direct Provider   → Mirror → Transport（KCP / TCP / WebSocket）
    └── Relay Provider    → Relay Client → Relay Server
```

### 11.3 角色 × 路径正交（Role × Path）

**Client / Host / Service 是运行角色（谁在跑网络逻辑）；Direct / Relay 是连接路径（数据怎么到达对端）。两个维度正交，禁止放进同一个枚举。**

```csharp
public enum NetworkRole { Client, Host, Service }   // 运行角色
public enum NetworkPath { Auto, Direct, Relay }     // 连接路径
```

六种组合全部合法：

| | Direct | Relay |
|---|---|---|
| Client | 直连 Host/Service | 经 Relay 连 Host/Service |
| Host | 本地开服 + 直连 | 本地开服 + 经 Relay 暴露公网 |
| Service | Dedicated Server 直连 | 本地 Service 经 Relay 暴露公网 |

```csharp
// 启动由配置驱动，Mod 完全不感知底层是 Direct 还是 Relay
context.Network.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });
```

`Auto` = 先尝试 Direct（NAT 打洞 / 地址发现），失败自动回退 Relay——连接协商由 Network Mod 与云端 Session Service 完成，业务无感知。

**Host 不绕过网络管线（硬规则）**：Host = ServerContext + ClientContext 两套独立上下文；Host 的本地客户端也走完整的 `Encode → 协议 → Decode` 回环管线（Loopback Provider，内存队列替代真实 Transport），保证 Host 玩家与普通 Client 行为完全一致，测试 / Replay / Prediction / Authority 路径统一。禁止 `Local Player ─X─► Server ECS` 的短路写法。

### 11.4 统一协议接口（非泛型 ABI）

所有业务 Mod 通过实现 `Network.Abstractions` 中的统一接口注册协议；**协议（Encode/Decode）与业务处理（Handler）分离**：

```csharp
public enum NetworkDirection { ClientToServer, ServerToClient }

public interface INetworkProtocol
{
    ProtocolId Id { get; }              // 稳定哈希生成，见 §11.5
    ushort Version { get; }
    NetworkDirection Direction { get; }
    int MaxSize { get; }                // 载荷上限，发送前校验
    void Encode(in object message, INetworkWriter writer);
    object Decode(INetworkReader reader);
}

public interface INetworkHandler
{
    void Handle(in NetworkContext context, in object message);
}
```

Mod 不接触 Mirror 的 `NetworkWriter/NetworkReader`，只面对 Core 定义的原语接口：

```csharp
public interface INetworkWriter
{
    void WriteByte(byte value);    void WriteBool(bool value);
    void WriteInt32(int value);    void WriteUInt32(uint value);
    void WriteInt64(long value);   void WriteFloat(float value);
    void WriteString(string value);
    void WriteBytes(in PayloadBuffer value);
}

public interface INetworkReader
{
    byte ReadByte();   bool ReadBool();
    int ReadInt32();   uint ReadUInt32();
    long ReadInt64();  float ReadFloat();
    string ReadString();
    PayloadBuffer ReadBytes();
}
```

**GC 与频率档规则（硬约束）：**

- 协议声明频率档：`Frequency = Low | High`。低频协议（RPC / Command / Event / 一次性请求）可以走 `object` 接口，装箱一次可接受。
- 高频协议（状态、移动、战斗高频事件）**禁止装箱路径**：由 Source Generator 生成 struct + 零分配 Codec，直接写 Buffer。
- `Decode` 返回的对象必须来自 Mod 自己的消息对象池（复用实例），禁止每条消息 `new`——网络线程上的 GC 压力会直接影响帧率。
- **频率分界规则：每秒发生超过约 5 次的状态变化必须走 ECS Replication（§11.10）；一次性行为 / 事件 / 请求才走协议消息。** 不允许把高频状态塞进普通消息协议。

Encode/Decode 一律由 Source Generator 生成（`[NetworkProtocol("weapon:fire")]`），生成物是普通 IL 代码，无反射、无泛型序列化器、无运行时元数据；注册也是显式代码调用（`network.Register(...)`），禁止 `Assembly.GetTypes()` 扫描。

### 11.5 GlobalProtocolId —— 稳定哈希，禁止按序分配

- 协议逻辑身份：`{ModId}:{ProtocolPath}`，如 `com.game.weapon:fire`、`com.game.vehicle:control`——每个 Mod 拥有自己的协议命名空间，不同 Mod 的本地协议名永不冲突。
- **全局 ID = 稳定哈希**：`ProtocolId = xxHash32("{ModId}:{ProtocolPath}")`。两端各自对同一字符串计算，**结果天然一致**——不依赖 Mod 加载顺序，也不需要连接握手时交换映射表。
- **禁止按加载顺序分配全局 ID**（两端顺序不同即错位）。
- 冲突检测：构建期由打包工具对所有已发布协议做全局查重；运行期 Network Mod 注册时发现 ID 冲突直接拒绝加载该协议并报错（指出冲突双方 ModId）。
- 线上数据包只携带整数头，字符串 ID 只用于调试与工具链：

```
┌─────────────────┬───────────┬───────────┬──────────────────┐
│ ProtocolId u32  │ Ver u16   │ Flags u16 │ Payload Len u32  │
├─────────────────┴───────────┴───────────┴──────────────────┤
│ Payload（Encode 产物，之后整体进入压缩 / 加密 / 分包管线）  │
└─────────────────────────────────────────────────────────────┘
```

### 11.6 Protocol Registry 与版本协商

Registry 条目：

```
ProtocolEntry
├── ProtocolId        （全局稳定哈希）
├── OwnerMod          （ModId，卸载时按此清理）
├── Version
├── Direction         （ClientToServer / ServerToClient）
├── Delivery          （Reliable / Unreliable / Sequenced）
├── Frequency         （Low / High）
├── Encode / Decode
└── Handler
```

协议生命周期与 Mod 生命周期严格一致：`IMod.Register` 时注册协议，`Unregister` 时按 OwnerMod 一次性清理（参见 §11.14 的会话内限制）。

**版本协商在连接握手时执行**（Protocol Negotiation），策略表：

| 情况 | 策略 |
|---|---|
| 双方协议版本交集非空 | 取共同最高版本 |
| 核心玩法协议无交集 | 拒绝连接 |
| 表现层协议无交集 | 降级忽略该协议，允许连接 |
| 客户端缺少服务端声明的必需 Mod | 拒绝连接并返回缺失清单 |
| 客户端缺少服务端声明的可选 Mod | 允许连接，禁用对应协议 |

**协议废弃窗口（硬规则）：服务端只保留"当前版本 + 上一个版本"的编解码器**，更老版本直接拒绝——禁止无限期兼容历史版本，否则 Codec 维护成本随版本数线性失控。

### 11.7 Mod Network Channel 与网络隔离

每个 Mod 在 Network Mod 中拥有一个独立的 `ModChannel`，路由天然按 Mod 分区：

```
Network Mod
│
├── Weapon Channel
│   ├── Client → Server
│   ├── Server → Client / Single
│   └── Server → Client / Broadcast
│
├── Vehicle Channel
└── Quest Channel
```

隔离规则（Network Mod 在路由时强制校验 `ModId / ProtocolId / Direction / Target / Connection / Authority`）：

- 一个 Mod 的 Client 数据只能发送给**该 Mod 的** Server；`Weapon.Client ─X─► Vehicle.Server` 必须被拒绝。
- 通信形态严格为三种：**Client → Server**、**Server → Client / Single**、**Server → Client / Broadcast**（广播范围 = 本 Mod 的合法 Client Endpoint，且必须过兴趣管理，见 §11.8）。
- **禁止 Client → Client**；Client A 要让 Client B 知道状态，必须经 Server 验证后转发——所有 Gameplay 权限由 Server 掌控。
- 通信方向由 API 形态强制，而不是运行时检查：

```csharp
network.SendToServer(protocolId, payload);              // Client 只有这一个出口
network.SendToClient(playerId, protocolId, payload);    // Server 单播
network.Broadcast(protocolId, payload);                 // Server 广播（过 Interest）
```

### 11.8 寻址与兴趣管理（Interest Management）

- **Mod 不使用裸 ConnectionId 寻址**：寻址面向玩家 / 实体（`PlayerId` / `EntityId` / `NetworkId`），由 Network Mod 映射到具体 Connection。连接管理是 Network Mod 的内部事务。
- **广播不是无差别全房间发送**：Network Mod 提供 Interest 钩子，Gameplay / Mod 声明 relevance 规则（按区域、队伍、观察者集合、距离过滤），Router 只把包发给"对该协议有可见性"的客户端。实体规模上去以后，这是带宽的生命线。

### 11.9 Message Pipeline

发送：

```
Application Message
        │
        ▼
Protocol Encode
        │
        ▼
Message Validation（MaxSize / Frequency / 配额预检）
        │
        ▼
Compression          ← 压缩 → 加密 → 分包（加密后数据无法有效压缩）
        │
        ▼
Encryption
        │
        ▼
Packetization
        │
        ▼
Fragmentation
        │
        ▼
Reliability
        │
        ▼
Traffic Accounting
        │
        ▼
Network Provider
```

接收：

```
Network Provider
        │
        ▼
Traffic Accounting
        │
        ▼
Packet Validation
        │
        ▼
Reassembly
        │
        ▼
Reliability
        │
        ▼
Decrypt
        │
        ▼
Decompress
        │
        ▼
Protocol Registry 校验（Owner / Direction / Version / Authority）
        │
        ▼
Protocol Decode
        │
        ▼
Protocol Router → ModChannel
        │
        ▼
Mod Handler
```

**分包（Packetization）与可靠传输（Reliability）必须分开**：分包解决"逻辑消息太大"，可靠性解决"Packet 丢了怎么办"。业务 Mod 只看到 `Send(Message)`，而不是 `Send(Packet)` / `Send(Fragment)`。

### 11.10 ECS Replication

高频 ECS 状态不走 `object` 协议消息，走独立 Replication 管线（AOT Runtime，直接二进制 Buffer，无装箱）：

```
Server ECS                         Client
    │                                ▲
    ▼                                │
Replication System              Client ECS
    │                                ▲
    ▼                                │
Network Mod ◄──── Snapshot / Delta ──┘
    │
    ▼
Network Provider
```

规则：

- Mod 用 `[NetworkReplicated(Authority = Server, Mode = ChangedOnly)]` 声明可同步 Component；Source Generator 生成 Replication Schema + Codec——与消息、ModCall **共享同一套 Codec**（Rule 14）。
- **NetworkId ↔ Entity 映射是核心**：NetworkId 由 Server 分配（全局唯一、会话内稳定），Network Mod 维护双向映射表；客户端的 Entity 只是映射的产物。
- **Spawn / Despawn 是独立协议**：携带实体原型（ArchetypeId）+ 初始 Component 集；之后的增量变化走 Snapshot/Delta。
- 支持量化压缩（Position / Rotation 定点化）；快照与增量分离：新连接发全量快照，存量连接发增量。
- 客户端收到网络输入不直接改 ECS：`FireWeapon → Network Mod → 校验 → Weapon Mod Handler → ECS Command → Simulation`；**网络消息是输入，ECS 是权威状态**，结果由 Server 经 Replication 同步回去。

### 11.11 Relay 与 Session

**Relay 是 Blind Relay**：不理解 Mod 协议、不参与 Mod 路由、不运行任何 Game Mod，只看到 `SessionId / ConnectionId / PacketLength / Sequence / EncryptedPayload`（见 §11.12 包结构）。

**云端只做中转，不做游戏**：Session / Discovery / Connection / Relay 在云端，Gameplay / ECS / Game State 全部运行在玩家自己的 Host 或本地 Service 上——本地 Host / Service 通过 Relay 暴露到公网，**不需要公网 IP、端口映射或路由器配置**。

```
Cloud
│
├── Session Service   （发现 / 房间码 / 连接协商）
└── Relay             （盲转发）
        ▲
        │ Internet / NAT
        │
   Client A ──┐
   Client B ──┼──► Relay ──► Host / Service（本地，跑 ECS + Mods）
   Client C ──┘
```

Session 模型：Host 启动 → `CreateSession()` → Relay 返回 SessionId（房间码，如 `ABCD-1234`）；Client 拿到房间码 → `JoinSession(sessionId)` → Relay 找到 Host → 建立转发。Relay 节点视角只有一个 Session 下的连接集合，不认识任何游戏概念。

### 11.12 安全边界与信任模型

- **不要让 Mod 获得网络加密密钥**——密钥在 Network Mod 内，Mod 只能 Message → Network API；第三方 Mod 即使被反编译也不会得到 Session Key。
- 不要每个 Mod 自己做加密（密钥管理混乱、算法不统一、安全漏洞、重复实现）；加密算法做成 `INetworkSecurityProvider`。
- ModId / ProtocolId 放入加密 Payload，Relay 不可见：

```
┌──────────────────────────────┐
│ Transport Header             │   Version / SessionId / ConnectionId
│                              │   Sequence / Flags / Payload Length
├──────────────────────────────┤
│ Security Header              │   KeyId / Nonce / Counter
├──────────────────────────────┤
│ Encrypted Payload            │   ProtocolId / ProtocolVersion
│                              │   Business Data
├──────────────────────────────┤
│ Authentication Tag           │
└──────────────────────────────┘
```

- **信任模型（显式声明）**：**Host 模式下 Host 即权威，其他客户端天然信任 Host 机器**——适用于合作 / PVE / 好友联机；竞争性玩法必须使用 Service（Dedicated Server）模式，不允许用 Host 模式承载。
- **UGC 防护**：Encode 输出受 Writer 边界检查（越界 / 超 MaxSize 即丢弃并对该 Mod 记违规）；配合 §11.13 的配额，恶意或写错的 Mod 无法拖垮服务器。

### 11.14.1 重装语义（HybridCLR 接入前）

字节加载（`Assembly.Load(bytes)`）的程序集在 Unity 中**不可卸载**；重复加载会产生多份副本，而 Unity 脚本绑定按"程序集名+类名"关联到**首份副本**——若首份在卸载时静态状态已清空，重装视图会绑到已死副本（NRE 事故）。因此 HybridCLR 接入前的重装语义为**"同副本重入"**：

1. 程序集加载器按名称**去重复用**（同 AppDomain 已加载则复用首份），注册 / 静态桥 / 视图恒落同一份副本；
2. Mod 的静态状态必须**可重入**：`Register` 幂等重设，`Unregister` 清空——重装 = 同一程序集上的新 ModObject；
3. 代码变更需**重启进程**生效（商店更新新版本在本语义下不能热生效）；HybridCLR 接入后由热更正式接管，恢复"全新副本 + 新代码"。接入前商店分发应以版本号为重装依据（SemVer 纪律：任何行为变更必须升号）。

### 11.13 流量统计、配额与可观测性

统计分三级：Connection Level / Mod Level / Protocol Level，并能按层级看到压缩前后：

```
Weapon.Mod
│
├── Logical     └── 100 KB
├── Compressed  └── 40 KB
├── Encrypted   └── 40 KB
├── Fragmented  └── 43 KB
└── Transport   └── 48 KB
```

每个 Mod 可配置网络预算：

```json
{
  "networkBudget": {
    "maxMessageSize": 256000,
    "maxPacketRate": 120,
    "maxBandwidth": 32768
  }
}
```

`MaxMessageSize` / `MaxFragmentCount` / `MaxPacketRate` / `MaxBandwidth`，超预算则 Throttle——防止恶意/错误 Mod（如 `Update() → Send()` 造成的 60 × N players 网络洪水）拖垮服务器。

**可观测性是 Mod 生态的命脉**：因为协议对 Network Mod 是不透明字节，Mod 作者排障不能靠业务日志猜。Network Mod 必须提供：per-Mod 的流量 / 延迟 / 错误率 / 丢弃率指标钩子，以及统一的抓包与回放工具（配合 Rule 14 的二进制 Codec，录制即回放）。

### 11.14 会话内协议 Mod 禁止热卸载（硬规则）

- **注册了网络协议的 Mod，在联网会话存续期间禁止热卸载**：对端仍持有该协议，在途包无法归属，协议撤销与重新协商的复杂度远超收益。
- 要更新协议，必须先结束当前会话（回到大厅 / 离线状态）再热更。
- 未注册网络协议的纯本地 Mod（UI、表现、资源类）不受此限。
- Network Mod 自身是 Framework Mod，任何情况下不热卸载（§10.4）。

### 11.15 Network Mod 也必须遵循 IMod

```csharp
public sealed class NetworkMod : IMod
{
    public void Register(IModContext context)   { /* 注册 Network Runtime */ }
    public void Unregister(IModContext context) { /* 注销 Network Runtime */ }
}
```

于是整个系统形成统一入口：Core Mod / Network Mod / UI Mod / Asset Mod / Gameplay Mods 全部是 `IMod`。

---

## 12. Mod 依赖

### 12.1 原则

依赖设计为**"能力依赖" + "版本约束"**，而不是简单的 DLL 依赖。

- **依赖不是"拥有"**：`Weapon.Mod ↓ depends ↓ Inventory.Mod` 不意味着 Weapon 可以访问 `InventoryMod.InternalDatabase`；依赖关系实际上是 `Weapon → depends → Inventory → exports → inventory:has_item 能力`。
- **Mod 通过导出能力（Capability）对外开放**：

```csharp
// Inventory.Mod：Register 时导出能力（生成的非泛型包装，见 §15.3）
public void Register(IModContext context)
{
    InventoryMod.ExportHasItem(context, HasItem);
    InventoryMod.ExportAddItem(context, AddItem);
}

// Weapon.Mod：调用另一个 Mod 的能力（Mod 间能力调用，见 §12.11）
bool has = InventoryMod.HasItem(context, player, ammoId);
```

### 12.2 Manifest 声明依赖

```json
{
  "id": "com.game.weapon",
  "version": "1.2.0",
  "entry": "WeaponMod",

  "assemblies": ["Weapon.dll"],

  "assets": {
    "catalog": "weapon.catalog",
    "bundles": ["weapon_shared", "weapon_client"]
  },

  "network": {
    "protocol": "weapon.protocol"
  },

  "dependencies": [
    { "id": "com.game.common",    "version": ">=1.0.0" },
    { "id": "com.game.inventory", "version": "^2.0.0" }
  ]
}
```

### 12.3 依赖解析由 ModResolver 统一负责

不要让 Mod 自己 `LoadMod("Inventory")`：

```
ModLoader
    │
    ▼
读取所有 Manifest
    │
    ▼
Dependency Resolver
    │
    ▼
Dependency Graph
    │
    ▼
Topological Sort
    │
    ▼
Register
```

```
Common
   │
   ├─────────┐
   ▼         ▼
Inventory   UI
   │
   ▼
Weapon
```

注册顺序：`Common ↓ Inventory ↓ UI ↓ Weapon`；注销反过来：`Weapon ↓ UI ↓ Inventory ↓ Common`。

**Dependency Graph 管理的是 ModObject，而不是简单 DLL。**

```
                         ModManager
                             │
          ┌──────────────────┼─────────────────┐
          │                  │                 │
     Common.Mod         Inventory.Mod      Vehicle.Mod
          │                  │                 │
          │                  ▼                 │
          │             Weapon.Mod             │
          │                  │                 │
          └──────────────────┴─────────────────┘
```

### 12.4 依赖三种类型

| 类型 | 说明 |
|---|---|
| Required | 没有依赖则 Load Failed：`{ "id": "inventory", "required": true }` |
| Optional | 存在则增强，不存在仍可运行：`{ "id": "equipment", "optional": true }`，代码用生成的 `EquipmentMod.TryXxx(context, ...)`（内部处理 `NoModException`） |
| Compatible | 版本区间兼容：`{ "id": "inventory", "version": ">=1.0.0 <3.0.0" }` |

### 12.5 Dependency Feature

比简单 Mod 依赖更有价值：只依赖需要的 Feature。

```json
{
  "dependencies": [
    {
      "id": "com.game.inventory",
      "version": "^2.0.0",
      "features": ["inventory"]
    }
  ]
}
```

未来 `Inventory.Mod` 可能包含 `inventory / equipment / trading / crafting`，Weapon 只依赖 `inventory` 而不是整个 Inventory 能力。

### 12.6 Client / Server Scope 依赖

```json
{
  "dependencies": [
    { "id": "com.game.common",    "scope": "shared" },
    { "id": "com.game.ui",        "scope": "client" },
    { "id": "com.game.inventory", "scope": "server" }
  ]
}
```

| 环境 | 加载的 Mod |
|---|---|
| Host | Common / UI / Inventory / Weapon |
| Client | Common / UI / Weapon |
| Server | Common / Inventory / Weapon |

### 12.7 Network Protocol 依赖特别处理

Weapon 需要发送 `InventoryItemId` 时，**不要直接依赖 Inventory 的内部协议**；通过 Inventory 导出的公开能力（§12.11），网络协议仍然由 Weapon.Mod 自己注册——符合"每个 Mod 自己定义自己的网络协议，所有网络数据统一经过 Network Mod"。

### 12.8 DAG 与版本

- 依赖必须形成 **DAG**，禁止循环依赖；Loader 在加载前检查 `Cycle detected (A ↓ B ↓ C ↓ A)` 直接拒绝加载。
- 版本冲突（Weapon 要 `Inventory >=2.0`，Vehicle 要 `Inventory <2.0`）时 Resolver 必须判定 `No compatible version`，而**不能同时加载两个 Inventory**。
- 第一版直接采用 **SemVer**（`1.0.0` / `^1.2.0` / `>=1.2.0 <2.0.0` / `~1.2.0`）：

```
Manifest → Dependency Resolver → Version Resolver → Load Plan
```

### 12.9 示例：请求某个 Mod 的数据

场景：Weapon.Mod 开火前需要知道玩家弹药数——这是 **Inventory.Mod 的数据**，即"访问对方 Mod 的能力"。按数据特征选三种模式之一：

**模式 1：Mod 能力调用（偶发、需要权威结果）**

```csharp
// Inventory.Mod：Register 时导出能力（§12.11）
InventoryMod.ExportGetItemCount(context, GetItemCount);

// Weapon.Mod：调用另一个 Mod 的能力（生成的非泛型包装）
if (InventoryMod.GetItemCount(context, player, AmmoId) >= cost)
{
    Fire();
}
```

**模式 2：订阅事件 + 本地缓存（高频、展示类）**

每帧查询走 Service 太重的场景（如 HUD 弹药显示），改为事件驱动：

```csharp
// Weapon.Mod / UI 侧：启动时查一次，之后靠事件维护本地缓存（生成的非泛型通道）
ItemChangedMsg.Subscribe(context, OnItemChanged);

void OnItemChanged(in ItemChanged e)
{
    ammoCache[e.Item] = e.NewCount;    // 本地只读缓存
}
```

**模式 3：跨端数据（Server 权威，Client 需要展示）**

Client 上的 Weapon 要弹药数，而弹药权威在 Server：

```
Inventory.Server (权威)
      │  ECS Replication / Network 协议
      ▼
Inventory.Client (本地只读副本)
      │
      ▼
Weapon.Client 查询本地副本（Service，同端）
```

即：**跨端不靠"远程调用对方 Service"，靠复制本地副本 + 同端查询**。

**四条硬规则：**

1. **返回快照，不返回内部引用**——Service 返回只读 DTO（`ItemSnapshot`），绝不返回 `List<Item>` / `Dictionary` 等内部容器，否则调用方持有对方内部状态，热卸载边界被破坏；
2. **依赖要声明**——Weapon 在 Manifest 中声明 `com.game.inventory` 依赖（§12.2），Resolver 保证加载顺序；
3. **必须处理 NoMod / NoCapability**——对方 Mod 未加载/已卸载时得到 `NoModException` / `NoCapabilityException`（§12.11），Weapon 要有降级策略（如禁用开火提示）；
4. **先想清楚数据在哪一端**——同端数据走 Service，跨端数据走 Replication/Network 落地成本地副本后再查，不要让 Client 直接"远程查询" Server 的 Mod。

### 12.10 隔离 ⇒ 唯一的跨 Mod 通道

Mod 彼此完全隔离：

- 不共享程序集引用（业务 Mod 互不引用）；
- 不共享类型（对方内部类型不可见）；
- 不共享实例（拿不到对方的对象）；
- 不共享静态状态（没有跨 Mod 的全局变量）。

因此，**跨 Mod 访问有且只有四条合法通道，全部由 Core 中介**：

| 通道 | 语义 | 入口 | 有返回 |
|---|---|---|---|
| Mod 能力调用 | 访问另一个 Mod 的能力：我要你做什么 | `context.Mods.Call` / 生成包装（§12.11） | ✅ 同步返回 |
| Message | 事件通知：发生了什么 | `context.Messages.Publish/Subscribe` | ❌ |
| UI | 窗口协作：打开/关闭对方界面 | `context.Ui` / UI 能力调用 | 句柄 |
| Resource | 跨 Mod 资源解析（受限） | `context.Assets.Resolve("mod:path")` | 资源句柄 |

> 注意：`context.Services` **不在此列**——它是本端/本 Mod 内部的服务定位（前端调后端、同 Mod 分层），不参与跨 Mod 访问（§12.11）。

四条通道的共同点（也是它们合法的原因）：

1. **Core 中介**——Mod 之间永不直连，所有交互可被 Core 观察、记录、限制；
2. **归属可追踪**——调用方和被调方都关联到 ModObject；
3. **卸载安全**——对方卸载后的行为是定义好的：`NoModException` / `NoCapabilityException` / 自动退订 / `CloseAll(modId)` / 资源句柄失效，而不是野指针；
4. **依赖可见**——使用任一通道都构成依赖，应在 Manifest 声明，DAG 可静态分析。

**被禁止的"第五条通道"：**

```
直接引用对方程序集 / 类型        ❌
静态类 / 全局单例共享状态         ❌
反射跨 Mod 调用                  ❌
FindObjectOfType 等 Unity 全局发现 ❌
直接传递委托 / 事件引用           ❌
```

这些写法的共同问题：**Core 看不见这次访问**——无法追踪归属、无法分析依赖、热卸载时必然泄漏。隔离的价值不在于"不能访问"，而在于"每一次访问都经过可治理的通道"。

### 12.11 Mod 间能力调用（ModCall）

先区分两个容易混淆的概念：

- **`context.Services`**：**本端 / 本 Mod 内部**的服务定位（如 UI 前端调业务后端逻辑、同 Mod 内部分层解耦）——不参与跨 Mod；
- **`context.Mods.Call`**：**跨 Mod** 能力调用——Mod 访问另一个 Mod 能力的唯一"有返回"通道。

```csharp
// Core ABI（非泛型，见 §15.3）
public interface IModManager
{
    // ...
    void Export(CapabilityId id, Delegate handler);          // 当前 Mod 导出能力
    object Call(ModId target, CapabilityId id, in object args);
}
```

业务代码使用 Source Generator 生成的非泛型包装：

```csharp
// Abstractions 包（生成代码）
public delegate bool HasItemHandler(in HasItemArgs args);   // 非泛型委托

public static class InventoryMod
{
    public static readonly ModId Id = new("com.game.inventory");
    public static readonly CapabilityId HasItemCap = new("inventory:has_item");

    // Inventory.Mod 侧：导出能力
    public static void ExportHasItem(IModContext context, HasItemHandler handler)
        => context.Mods.Export(HasItemCap, handler);

    // Weapon.Mod 侧：调用能力
    public static bool HasItem(IModContext context, EntityId player, ItemId item)
        => (bool)context.Mods.Call(Id, HasItemCap, new HasItemArgs(player, item));
}
```

调用路径：

```
Weapon.Mod
    │
    ▼  InventoryMod.HasItem(context, player, item)
context.Mods.Call
    │
    ▼  ModManager 路由（校验依赖、归属、耗时计入 Trace）
Inventory.ModObject.Exports["inventory:has_item"]
    │
    ▼
Inventory.Mod 的处理函数
```

规则：

1. **必须先声明依赖**（Manifest，§12.2）——未声明依赖的 Call 直接被 Core 拒绝；能力调用本身就是依赖；
2. 目标未加载 → `NoModException`；能力未导出 → `NoCapabilityException`；调用方必须处理（对方可能已卸载）；
3. 参数与返回是**二进制序列化的纯数据**（§14.4 / §14.5 规则同样适用），不传内部对象引用；
4. 同步返回；底层复用 Envelope + CorrelationId + 超时/熔断机制（§14），对 Mod 不可见；
5. 归属与耗时计入 Trace——能看到"谁在调谁的什么能力、多频繁、多慢"（§14.9）；
6. 能力导出的生命周期归属 ModObject：卸载时随 `UnsubscribeAll` 一并撤销（§13.5）。

---

## 13. Mod 间通信（Message Runtime）

Mod 之间的通信统一走 **Core 的 Message Runtime（MessageBus）**，Mod 之间不直接持有对方的引用。原则与窗口/资源相同：**Core 管路由和订阅管理，消息的定义和处理逻辑归 Mod。**

### 13.1 定位

- 消息总线是**进程内**通信机制，与 Network Mod 分工明确：
  - `MessageBus` → Mod 与 Mod 之间（本进程内）；
  - `Network Mod` → Client 与 Server 之间（跨进程/跨设备）。
  - 需要把本进程的事件同步到其他端时，由 Mod 自己在消息处理器里调 Network 协议发送，**MessageBus 不跨网络**。
- 消息只承担一种语义：**Event（Pub/Sub）**——一对多、无返回值，用于状态变化通知（`weapon:fired`、`inventory:item_added`）。
- **需要返回的"请求"不是消息，而是访问对方 Mod 的能力**——它归入 Service 通道（见 §13.7），由 `IServiceContext` 承担。底层可复用同一套 Envelope/CorrelationId 机制（§14），但对 Mod 暴露的是强类型接口调用，而不是消息。

### 13.2 MessageId 命名空间

与 AssetId / WindowId 一致：

```
weapon:fired
weapon:reloaded
inventory:item_added
```

MessageBus 天然知道**每条消息属于哪个 Mod、每个订阅属于哪个 ModObject**。

### 13.3 IMessageContext

```csharp
// Core ABI：全非泛型（§15.3），载荷一律为二进制（§14.5）
public interface IMessageContext
{
    // 订阅（在 IMod.Register() 中调用，归属当前 ModObject）
    void Subscribe(MessageId id, Delegate handler);

    // 发布事件（一对多）；payload 为二进制缓冲
    void Publish(MessageId id, in PayloadBuffer payload);
}
```

业务代码不直接碰 ABI，而是用 Source Generator 生成的**具体非泛型通道类**（编码/解码也在生成代码内）：

```csharp
// 生成代码（WeaponFiredMsg.g.cs，非泛型）
public delegate void WeaponFiredHandler(in WeaponFired msg);   // 非泛型委托

public static class WeaponFiredMsg
{
    public static readonly MessageId Id = new("weapon:fired");

    public static void Publish(IModContext context, in WeaponFired msg)
    {
        var buffer = WeaponFiredCodec.Write(msg);              // 生成的二进制编码
        context.Messages.Publish(Id, buffer);
    }

    public static void Subscribe(IModContext context, WeaponFiredHandler handler)
        => context.Messages.Subscribe(Id, Dispatch);

    static void Dispatch(in PayloadBuffer buffer)
        => _handler(WeaponFiredCodec.Read(buffer));            // 生成的二进制解码
}
```

```csharp
// Weapon.Mod：发布事件
WeaponFiredMsg.Publish(context, new WeaponFired(weaponId, targetId));

// Quest.Mod：订阅事件
WeaponFiredMsg.Subscribe(context, OnWeaponFired);

void OnWeaponFired(in WeaponFired e)
{
    quest.Progress("kill", e.Target);
}

// Weapon.Mod：需要结果 → 不是消息，是访问 Inventory 的能力（Mod 间能力调用，§12.11）
bool has = InventoryMod.HasItem(context, player, ammoId);
```

### 13.4 结构

```
Message Runtime (Core)
│
└── MessageBus
      │
      ├── Registry        (MessageId → Owner Mod)
      ├── Router          (Pub/Sub)
      │
      └── Subscriptions
            │
            ├── weapon:fired     → [Quest.ModObject, Achievement.ModObject]
            ├── inventory:*      → [UI.ModObject]
            └── ...
```

### 13.5 生命周期绑定 ModObject

订阅必须随 Mod 一起销毁，卸载流程中加入：

```
Unload(Mod)
 │
 ├── IMod.Unregister()
 ├── WindowManager.CloseAll(modId)
 ├── MessageBus.UnsubscribeAll(modId)   ← Core 强制退订
 ├── ...
```

- `UnsubscribeAll(modId)` 由 Core 强制执行，不依赖 Mod 自觉——热卸载后不会有指向已卸载 Assembly 的悬挂回调。
- 同理，Mod 导出的能力（§12.11）也一并移除，其他 Mod 再调用会得到明确的 `NoModException` / `NoCapabilityException` 而不是调用到僵尸代码。

### 13.6 硬规则

1. **禁止 Mod 之间直接传递委托 / 事件引用**（`otherMod.OnXxx += ...`）——绕过 Bus 的订阅 Core 无法追踪，热卸载必漏。
2. **消息载荷必须是可二进制序列化的纯数据**（§14.5）——值类型 / 不可变 DTO，不携带 Unity Object、Entity 内部引用或对方 Mod 的类型；二进制化从机制上杜绝引用泄漏破坏卸载边界。
3. **消息处理器里不允许重入修改订阅表**：在 Dispatch 过程中 Subscribe/Unsubscribe 进入待处理队列，当前帧派发结束后统一生效。
4. **事件顺序只保证同一发布者有序**，跨 Mod 无全局顺序假设；有严格先后依赖的协作应使用 Service（同步拿到结果）而不是 Event 链。
5. **高频数据不走消息总线**：每帧状态同步（位置、旋转）属于 ECS Replication / Network 的范畴，消息用于离散事件与请求——避免 `Update() → Publish()` 造成的消息风暴。

### 13.7 消息与能力的统一模型

```
Event（通知：发生了什么，无返回）        →  IMessageContext（消息，Pub/Sub）
Mod 间能力访问（我要你做什么，有返回）    →  IModManager.Call（ModCall，§12.11）
本端/本 Mod 内服务定位（前端调后端）      →  IServiceContext（不参与跨 Mod）
跨端同步（告诉另一端）                  →  INetworkContext（协议）
```

关键认识：**"需要返回的请求"本质上是访问 Mod 的能力，不是消息**。

- **对外两种 API**：通知走生成的消息通道（`WeaponFiredMsg.Publish/Subscribe`）；跨 Mod 能力访问走生成的 ModCall 包装（`InventoryMod.HasItem(context, ...)`）——强类型、可 IDE 导航、契约清晰，且底层全部是非泛型调用（§15.3）；
- **对内一套机制**：Service 调用底层可以复用消息的 Envelope + CorrelationId + 超时 / 熔断 / NoHandler 机制（§14）来实现定向 Request/Response，但 Mod 永远看不到这层——能力访问不伪装成消息；
- **判断标准**：调用方需要结果吗？需要 → Service（能力访问）；不需要、只是宣告发生了什么 → Event（消息）。

Mod 之间的依赖声明（Manifest / DAG / 版本）不受影响——订阅某 Mod 的消息或调用它的 Service，同样构成对它的依赖，应在 Manifest 中声明。

---

## 14. 消息设计

### 14.1 消息的三层结构

一条消息 = **Envelope（信封，Core 定义）+ Metadata（元数据）+ Payload（载荷，Mod 定义）**：

```csharp
// Core 定义，AOT 稳定，所有 Mod 通用
public readonly struct MessageEnvelope
{
    public readonly MessageId Id;            // weapon:fired
    public readonly ModId Sender;            // 发布者（由 Bus 填充，Mod 不可伪造）
    public readonly int Version;             // 消息版本
    public readonly long Sequence;           // 发布者内自增序号
    public readonly long Timestamp;
    public readonly long CorrelationId;      // Service 调用的请求/响应关联（内部机制，对 Mod 不可见）
}
```

- **Envelope 属于 Core**（AOT 稳定 ABI），Mod 永远不需要关心；
- **Payload 属于 Mod**，Envelope 对载荷透明——Bus 只按 MessageId 路由，不理解业务内容。

### 14.2 MessageId 与所有权

格式沿用 `{ModId}:{MessagePath}`，关键规则是**所有权唯一**：

```
weapon:fired              ← 定义权和发布权属于 Weapon.Mod
inventory:item_added      ← 属于 Inventory.Mod
```

- **谁定义，谁发布**：只有 owner Mod 能 Publish 自己命名空间的消息，其他 Mod 只能订阅——防止第三方 Mod 伪造 `weapon:fired` 污染事件流；
- Bus 在 Publish 时校验 Sender 与 MessageId 的归属，不匹配直接拒绝；
- 注意 `inventory:has_item` 这类"有返回的查询"**不是消息**，而是 Inventory.Mod 的能力，走 Service（§13.7），不适用本节的发布/订阅规则。

### 14.3 消息定义（Source Generator，无反射）

```csharp
[Message("weapon:fired", Version = 1)]
public readonly struct WeaponFired
{
    public readonly int WeaponId;
    public readonly EntityId Target;
    public readonly EntityId Shooter;
}
```

Source Generator 生成注册与派发代码：

```
WeaponFired
    │
    ▼
Source Generator
    │
    ▼
WeaponFired.Message.g.cs   (注册表 + 强类型派发)
    │
    ▼
HybridCLR
```

不用反射、不用运行时发现——与 HybridCLR 序列化策略（§15）完全一致，AOT 友好。

### 14.4 Payload 设计规则

1. **纯数据、不可变**：`readonly struct`，字段只读；
2. **可二进制序列化**：所有载荷必须能由 Source Generator 生成 Codec（`Write` / `Read`）——跨 Mod 边界只流动 bytes（§14.5）；
3. **传 Id 不传引用**：载荷里是 `EntityId` / `ItemId` / `WeaponId`，不是 `GameObject` / `Entity` / 对方 Mod 的类型——二进制化之后，这条从"约定"变成"机制保证"：bytes 里不可能藏对象引用；
4. **不含委托**：载荷里不允许 `Action` / `Func`——回调必须走订阅机制，否则 Bus 无法追踪（二进制也无法序列化委托，双重杜绝）；
5. **自包含**：订阅者拿到载荷不需要再回调发布者就能理解语义（上下文字段带全）。

### 14.5 所有 Mod 通信数据全部是二进制（硬约束）

**消息载荷、ModCall 的参数与返回、网络协议——所有跨 Mod 边界的通信数据一律是二进制。** 进程内也不例外：

```
进程内 (MessageBus / ModCall):
  Publish(args) → Encode(生成的 Codec) → bytes → 路由 → bytes → Decode → Subscribe

跨网络 (Network Mod):
  Encode → Compress → Encrypt → Fragment → ...        同一套 Codec，继续走协议管线
```

**为什么进程内也要二进制：**

1. **一套编解码，处处复用**——消息、ModCall、网络协议使用同一套线格式与读写原语（Source Generator 为每个 Mod 自己的程序集生成各自的 Codec，跨 Mod 不共享编译产物，§14.11）；某个事件将来要从本地通知改成跨端同步，业务代码零改动；
2. **物理杜绝引用泄漏**——二进制里不可能藏着 `GameObject` / `Entity` / 对方 Mod 的对象引用（§14.4 规则由"约定"变成"机制保证"），热卸载边界从制度升级为物理事实；
3. **录制 / 回放免费获得**——Bus 里流动的本来就是 bytes，调试录制（§14.9）只需落盘，不需要额外的 Debug 序列化通道；
4. **为沙箱 / 跨进程留路**——未来 Mod 跑在独立进程或沙箱里，通信模型不变。

**性能对策（进程内二进制不等于慢，完整方案见 §14.12）：**

- Codec 由 Source Generator 生成，直接写字段（`WriteInt32` / `WriteFloat`），无反射、无装箱；
- Buffer 池化（`ArrayPool<byte>`）+ 栈上小缓冲（`stackalloc`），正常路径零 GC 分配；
- 订阅方拿到的是 buffer 的只读视图，Decode 按需进行（不关心的字段可跳过）；
- 真正高频的每帧数据仍然不走消息（§13.6 规则 5），消息的"离散事件"定位让序列化开销可控。

### 14.6 版本与兼容

```
[Message("weapon:fired", Version = 2)]
```

- 消息演化**只增字段、不改不改名不删**（append-only），旧订阅者读到旧版本载荷依然成立；
- 订阅者可声明版本范围：`WeaponFiredMsg.Subscribe(context, handler, minVersion: 1)`；
- 破坏性变更 = 新 MessageId（`weapon:fired_v2`），旧消息保留一个迁移周期后废弃。

### 14.7 派发管线

```
Publish(msg)
    │
    ▼
Validate      ← MessageId 存在？Sender 是 owner？版本合法？
    │
    ▼
Enqueue       ← 进入派发队列（不当场调用）
    │
    ▼
Dispatch      ← 主循环固定相位统一派发（如帧末）
    │
    ├── 查订阅表（MessageId → Subscribers）
    ├── 按 Mod 分组，同 Mod 内按注册顺序
    └── 逐个调用（异常隔离，见 §14.8）
```

**Publish 是异步派发（入队，帧末统一处理）**：

- Event 异步：避免发布者被订阅者的执行阻塞，避免 A→B→A 消息级联造成的无限重入；
- Dispatch 相位固定，保证每帧消息洪峰可控，也让消息顺序可预测；
- 需要同步拿到结果的场景不走消息——走 Service（§13.7），本质等价于一次类型安全的接口调用。

### 14.8 顺序、错误与配额

**顺序保证**：

- 同一 Sender、同一 MessageId 严格保序（Envelope.Sequence）；
- 跨 Mod 无全局顺序假设——有先后依赖的协作用 Service，不要用 Event 链（§13.6 规则 4）。

**错误隔离**：

- 订阅者抛异常 → 捕获、记录、计数，**不中断其他订阅者**；
- 同一订阅者异常超过阈值 → 熔断该订阅并上报（防止一个坏 Mod 拖垮整个消息流）；
- ModCall / Service 无实现 → 返回明确的 `NoModException` / `NoCapabilityException` / `NoServiceException`（对方 Mod 可能已卸载），调用方必须处理；
- 能力调用支持超时（异步重载 + CancellationToken）；这套 Envelope / CorrelationId / 超时 / 熔断机制同时服务于 ModCall 底层（§12.11 / §13.7）。

**配额（防消息风暴）**：

```
Weapon.Mod
├── MaxPublishPerFrame: 64
├── MaxPayloadSize: 1 KB
└── MaxSubscribersPerMessage: 32
```

- 每 Mod 每帧 Publish 上限，超限丢弃并告警；
- 高频每帧数据（位置/旋转）禁止走消息——那是 ECS Replication 的职责（§13.6 规则 5）。

### 14.9 调试与可观测性

```
Message Trace
│
├── weapon:fired
│     ├── 发布者: Weapon.Mod
│     ├── 订阅者: [Quest.Mod, Achievement.Mod]
│     ├── 本帧: 12 次
│     └── 平均耗时: 0.03 ms
│
├── inventory:item_added
│     ├── 发布者: Inventory.Mod
│     ├── 订阅者: [UI.Mod]
│     └── 本帧: 3 次
```

- Bus 记录最近 N 条消息（Id / Sender / 订阅者 / 耗时），可生成**消息流图**用于排查 Mod 间协作问题；
- **录制 / 回放免费获得**——Bus 里流动的本来就是二进制（§14.5），录制只需落盘，回放只需按序重投，不需要额外的 Debug 序列化通道。

### 14.10 每条消息是否都需要返回？

**消息（Event）一律无返回。需要返回的不是消息，而是 Mod 间能力调用（ModCall）。**

| 通道 | 返回 | 接收者数量 | 典型场景 |
|---|---|---|---|
| Event（消息，Pub/Sub） | ❌ 一律无返回 | 0..N | 状态通知：`weapon:fired`、`inventory:item_added` |
| ModCall（能力调用） | ✅ 按能力签名 | 恰好 1 个导出者 | 查询/请求能力：`InventoryMod.HasItem(context, ...)` |

规则：

1. **Event 不允许有返回**——订阅者是 0..N 个，"返回给谁"在语义上不成立；Event 是广播事实，不是索取结果；0 个订阅者不是错误。
2. **需要结果 = 访问另一个 Mod 的能力 = 走 ModCall**——`inventory:has_item` 这类"有返回的请求"本质上是调用 Inventory.Mod 导出的能力，应该用 `context.Mods.Call`（经生成包装）表达，而不是 Request 消息。
3. **能力调用的返回不是"回一条消息"**——ModCall 是同步返回值，**不要**用反向 Publish 一个 `xxx_response` 事件来实现响应，那会造成消息回环、关联困难和时序混乱。底层若复用消息机制实现定向调用，关联由 Envelope 的 `CorrelationId` 在内部完成（§14.1），对 Mod 不可见。
4. **判断标准只有一条**：调用方是否需要知道结果？
   - 需要结果 → ModCall（能力访问，调用方必须处理 `NoModException` / `NoCapabilityException` / 超时）；
   - 不需要结果、只是宣告发生了什么 → Event（消息）。

```csharp
// Event：宣告事实，无返回，不关心谁听
WeaponFiredMsg.Publish(context, new WeaponFired(...));

// Mod 间能力调用：需要结果，强类型，必须处理失败
bool has = InventoryMod.HasItem(context, player, ammoId);
```

### 14.11 契约解耦：契约 = 文档，Mod 间零程序集共享

> **问题**：Mod A 发布消息，Mod B 要订阅就必须知道载荷的数据结构——如果通过共享数据结构 DLL 解决，Mod 之间仍然存在程序集级别的耦合，对 UGC 生态不可接受。

**决策：Mod 之间不共享任何数据契约程序集。契约的唯一载体是文档，字节流是唯一事实来源。** Mod A 发布消息 / 能力 / 协议时，发布的是**字节流格式文档**；Mod B 想消费，基于文档**自己实现解析逻辑**。通信数据仍然全部是二进制（Rule 14 不变）。

```
Inventory.Mod（owner）                    Weapon.Mod（消费方）
     │                                        │
     │  发布 schema 文档：                     │  读文档，自己写解析：
     │  inventory:item_added v2               │  field 1 = ItemId (u32)
     │  field 1 = ItemId   (u32)              │  field 2 = Count  (u16) → 忽略
     │  field 2 = Count    (u16)              │
     └────── bytes 是唯一流动的东西 ──────────┘
              两个 Mod 之间没有任何 DLL 依赖
```

**1. 字节流格式必须标准化——这是"文档即契约"能落地的前提。**

所有跨 Mod 通信的载荷遵循统一的线格式（Wire Format）：

- **字段带编号**：每个字段有稳定的 field id，按编号读写，不按位置；
- **自描述分帧**：载荷可跳读——消费方只解析自己关心的字段，不认识的字段按长度跳过；
- **append-only 演化**（§14.6 不变）：只增字段、不改编号、不删字段。

没有这三条，"看文档自己写解析"就会退化成"对方加个字段，我的手写解析器就崩"。有了这三条，消费方解析 v1 的子集，面对 v2 的字节流照常工作。

**2. Core 提供安全读取原语——"自己写解析"不等于"操作裸内存"。**

消费方基于 `IPayloadReader` / `IPayloadWriter` 原语手写解析（与网络层 `INetworkReader` / `INetworkWriter` 同一套，§11.4）：

```csharp
// Weapon.Mod 订阅 inventory:item_added，基于文档自己实现的解析
void OnItemAdded(in PayloadReader reader)
{
    uint itemId = reader.ReadUInt32(fieldId: 1);   // 按编号读，边界检查
    // field 2 (Count) 我不关心，直接不读；未知字段自动跳过
}
```

原语负责边界检查、字段跳读、类型读取；越界 / 类型不匹配抛异常而非内存错误。手写解析的成本和安全都可控。

**3. owner Mod 的义务升级。**

既然没有共享代码保证两端一致，契约纪律必须落在 owner Mod 上：

- **文档与实现同步**：schema 文档随版本发布，字段编号、类型、语义、版本号完整描述；文档与实际发出的字节流不一致属于 owner 的缺陷；
- **发布测试向量**：每个消息 / 能力 / 协议附**样例字节流 + 期望值**，消费方用 CI 校验自己的手写解析器——这是无共享 DLL 模式下防止解析发散的关键补偿机制；
- **版本纪律不变**：append-only，破坏性变更 = 新 MessageId / 新协议（§14.6）；网络协议复用握手期版本协商（§11.6）。

**4. owner 侧仍用 Source Generator，消费侧自由选择。**

owner Mod 自己发消息时仍享受强类型（struct + 生成的 Codec，§14.3）——这是它自己程序集内部的事。消费方不引用 owner 的任何东西，可以手写解析，也可以基于文档用工具**自行生成**自己侧的解析代码（生成物归消费方所有）。两端的代码同源于文档，但**不共享任何编译产物**。

**结论**：Mod 解耦的定义在这里收到最严——**Mod 之间唯一的共享物是文档，唯一的通信介质是标准化字节流**。WeaponMod 改实现、InventoryMod 改实现，互不影响；契约演化的安全性由"字段编号 + append-only + 版本协商"三条机制保证，而不是由共享代码保证。

> **可重复定义是 intended 模式，不是坏味道**：多个消费方各自定义同一份结构的自己的解析类型（如 WeaponMod 的 `PlayerSnapshot` 与 NpcMod 的 `PlayerSnap` 都解析同一份 player 行字节），结构相同、定义独立、互不影响、零编译产物共享。这正是"各个 Mod 独立"（§0.1）在数据契约上的直接推论。唯一要防的是两端解析漂移——由 owner 发布测试向量（§14.11.3）+ 字段编号 + append-only 三条兜底。

> **例外说明**：第一方核心 Mod 之间，如果团队内部确实愿意共享契约程序集以换取编译期类型安全，不禁止——但那是团队内部的工程便利，**不是架构要求，UGC / 第三方 Mod 一律走文档契约**。

### 14.12 进程内消息传递的性能设计

**原则：先把默认路径做对，它的成本就已经接近 memcpy；绝不用"直接传对象引用 / 共享内存 struct"换性能**——那会废掉 Rule 14 用二进制换来的热卸载物理保证（引用泄漏、无法追踪）。进程内消息的开销模型是：

```
Publish 总成本 = 1 次 Encode（直写池化 buffer）
              + 0 次拷贝（N 个订阅者共享同一份只读视图）
              + 订阅方按需解码（只解析自己读的字段）
```

按下面的手段逐项压：

**1. 一次编码，零拷贝分发。** Publish 时生成 Codec 直接写入池化 buffer（`ArrayPool<byte>` 租用），Envelope 携带 `ReadOnlyMemory<byte>` 视图传给每个订阅者——**无论 1 个还是 10 个订阅者，buffer 只有一份，不复制**。这是相对"每个订阅者反序列化一份对象"方案的最大优势。

**2. 懒解码 + 字段级跳读。** `IPayloadReader` 是 buffer 上的游标，不是"先完整反序列化成对象再给你"。字段编号 + 自描述分帧（§14.11）使消费方**只解析自己关心的字段**，不认识的字段按长度 O(1) 跳过。订阅者只读 2 个字段就只付 2 个字段的解析成本——文档契约模式下这条是天然成立的。

**3. 直写 API（writer-first）——热路径消掉中间 struct。** 普通路径是"构造 struct → Encode 进 buffer"两步；热路径提供直写重载，生成代码把字段直接写进 buffer，不产生中间值：

```csharp
// 普通路径：WeaponFiredMsg.Publish(context, new WeaponFired(...));
// 热路径：跳过中间 struct，字段直写 buffer
WeaponFiredMsg.Publish(context, writer =>
{
    writer.WriteInt32(1, weaponId);
    writer.WriteInt32(2, targetId);
});
```

**4. Blittable 快速通道（仅限声明冻结布局的消息）。** 载荷为 unmanaged blittable struct（定长、无 string / 变长字段）且声明 `Layout = Frozen` 的消息，线格式直接定义为 struct 的内存布局并写进契约文档；进程内 `MemoryMarshal` 把 buffer **重解释**为 `ref readonly struct`——零解析、零拷贝。代价是放弃该消息的字段跳读与 append-only 演化能力，因此只允许稳定的核心低频消息使用，网络路径仍走标准 Codec。

**5. 池化与栈分配。** buffer 一律池化租用归还；小载荷（≤ 256B）`stackalloc` 栈上编码，正常路径零 GC 分配。消息是"离散事件"定位（§13.6），单帧频次有配额约束（§14.8），池化压力可控。

**6. 默认同步直派。** 进程内 Publish 默认在发布线程同步调用 handler，无异步队列、无跨线程拷贝；需要延后 / 跨帧处理的订阅者显式声明（派发模式见 §14.7），队列成本由需要它的人承担。

**7. 高频数据不进消息。** 每帧 / 高频状态同步走 ECS Replication（§11.10，增量 + 量化），消息只承载离散事件——从源头控制消息频率，比优化单条消息成本有效得多。

**明确禁止的"捷径"：**

| 方案 | 为什么禁止 |
|---|---|
| 直接传消息对象引用 | 热卸载后引用悬空，Rule 14 的物理保证作废 |
| 共享内存 / 指针传 struct | 同上；且 UGC 下无法做边界检查 |
| 为省一次 Encode 跳过 Envelope / 校验 | 配额、Trace、录制回放（§14.9）全部失效 |

**验收标准**：小载荷（≤ 64B）、1~2 个订阅者的进程内 Publish，目标 **< 1μs 级**、稳态零 GC 分配；Profile 不达标的先查"是否把高频数据塞进了消息"（手段 7），再查 codec 实现，不许动二进制约束。

---

## 15. Mod Package 与 HybridCLR

### 15.1 一个 Mod 就是一个完整 Package

```
Weapon.Mod
│
├── manifest.json
│
├── code/
│   ├── Weapon.Shared.dll      ← 数据 / 协议 Schema / ECS Component / 公共 API
│   ├── Weapon.Client.dll      ← UI / 输入 / 表现 / Client Handler
│   └── Weapon.Server.dll      ← Authority / 校验 / Server System / Simulation
│
├── assets/
│   ├── catalog
│   ├── bundles
│   └── ...
│
├── config/
│   └── ...
│
└── network/
    └── protocol               ← 协议清单（声明，供协商与查重，见 §11.5 / §11.6）
```

代码按 **Shared / Client / Server** 三个模块拆分（职责与加载规则见 §15.4）。

```
Mod Package
                     │
                     ▼
                 Mod Loader
                     │
          ┌──────────┼──────────┐
          ▼          ▼          ▼
        Code       Assets     Network
          │          │          │
          └──────────┼──────────┘
                     ▼
                 IMod.Register
                     │
                     ▼
                 Mod Runtime
```

### 15.2 与 HybridCLR 的关系

```
AOT Runtime
                        │
        ┌───────────────┼────────────────┐
        │               │                │
     ModRuntime     NetworkRuntime   AssetRuntime
        │               │                │
        └───────────────┼────────────────┘
                        │
                    HybridCLR
                        │
        ┌───────────────┼────────────────┐
        │               │                │
    Weapon.Mod      Vehicle.Mod      Quest.Mod
        │               │                │
      DLL             DLL              DLL
        │               │                │
      Assets          Assets           Assets
```

- AOT 层只负责提供**稳定 ABI 和 Runtime**；Mod 自己携带代码、数据、资源。
- AOT 只需要认识 `IMod` / `IModContext`，而不需要知道 Weapon / Vehicle / Quest。
- 序列化推荐 **Source Generator** 生成 Encode/Decode（如 `[NetworkProtocol("weapon:fire")] struct FireWeapon`），避免 Reflection 和大量 Generic AOT。

### 15.3 无泛型 ABI（硬约束）

**所有跨 ABI 的公开 API 禁止泛型方法与泛型参数。** 原因：HybridCLR 热更代码中每出现一个新的泛型实例化，就可能触发 AOT 泛型缺失（需要补充元数据，否则运行时报错）——这是热更事故的高发区，而且错误只在运行到对应代码路径时才暴露。

**原则：**

1. `IModContext` 及其所有子接口（Services / Messages / Resources / Pool / Ui / Ecs / Network）的签名**全部非泛型**；
2. 用 Id 代替类型参数：`ServiceId` / `MessageId` / `AssetId` / `WindowId` / `PoolId`；
3. **类型安全不由泛型提供，由 Source Generator 生成的具体包装类提供**——生成代码本身也是非泛型的（具体类型 + 显式 cast + 非泛型委托）；
4. `typeof(T)` 可以使用（Type 令牌不是泛型实例化）；用自定义非泛型委托代替 `Action<T>` / `Func<T>`。

**对照：**

```csharp
// ❌ 禁止：泛型 ABI
T Get<T>();
void Subscribe<T>(MessageId id, Action<T> handler);
T Load<T>(string path);
IPool<T> Rent<T>(PoolId id);

// ✅ Core ABI：Id + 非泛型签名
object Get(ServiceId id);
void Subscribe(MessageId id, Delegate handler);
object Load(AssetId id);
object Rent(PoolId id);
```

**类型安全由生成代码兜底（以 Mod 间能力调用为例）：**

```csharp
// Abstractions 包中由 Source Generator 生成（非泛型）
public delegate bool HasItemHandler(in HasItemArgs args);   // 非泛型委托

public static class InventoryMod
{
    public static readonly ModId Id = new("com.game.inventory");
    public static readonly CapabilityId HasItemCap = new("inventory:has_item");

    public static void ExportHasItem(IModContext context, HasItemHandler handler)
        => context.Mods.Export(HasItemCap, handler);

    public static bool HasItem(IModContext context, EntityId player, ItemId item)
        => (bool)context.Mods.Call(Id, HasItemCap, new HasItemArgs(player, item));
        // cast 和 Id 查找都在生成代码内
}
```

业务 Mod 写的永远是强类型的 `InventoryMod.HasItem(context, ...)`，但编译产物里**不存在任何新的泛型实例化**——cast 和 Id 查找都在生成代码中展开。

同理生成的非泛型包装：

| 能力 | 生成包装 |
|---|---|
| Mod 间能力调用 | `InventoryMod.HasItem / ExportHasItem`（§12.11） |
| Message | `WeaponFiredMsg.Publish/Subscribe`（含非泛型委托 `WeaponFiredHandler`） |
| Resource | `WeaponAssets.Sword(context)` |
| Pool | `WeaponPools.RentBullet(mod)` |
| Window | `WeaponWindows.OpenSettings(context)` |

> 效果：热更安全（无泛型实例化）、类型安全（生成代码保证）、可搜索可导航（具体类而非泛型参数）。

### 15.4 Shared / Client / Server 模块与环境裁剪

**Mod 是产品/功能边界，Module 是运行时边界：一个 Mod = 一个功能领域（Weapon.Mod），内部三个运行模块（Shared / Client / Server）。** 不要拆成 `WeaponClient.Mod` + `WeaponServer.Mod` 两个 Mod——否则协议、数据、版本、ModId 的归属会被撕裂（最终往往又退回出一个 `Weapon.Shared`，实际是三个模块伪装成三个 Mod）。

- Shared：双方都需要理解的东西——ECS Component、数据、协议 Schema、常量、公共 API。
- Client：UI、输入、表现、Client System、Client Network Handler。
- Server：Authority、校验、Server System、Simulation、Server Network Handler。
- 例外：只有当 Client 与 Server 确实由不同作者/团队**独立演化、独立发布**时（如官方 Client + 社区 Server），才允许拆成两个 Mod。

运行时按环境裁剪加载：

| 运行环境 | 加载模块 |
|---|---|
| Client | Shared + Client |
| Service | Shared + Server |
| Host | Shared + Client + Server |
| Relay | 不加载任何 Game Mod（不理解 Mod 协议，见 §11.11） |

Dedicated Server 因此不需要加载 UI / 输入 / 表现代码与资源，Client 也不需要加载 Authority / 管理逻辑。

**版本统一**：一个 Mod 只有一个版本号，天然意味着 Shared / Client / Server 同版本——避免 `Client 1.2 × Server 1.1` 的兼容性矩阵爆炸。

Manifest 表达模块划分：

```json
{
  "id": "com.game.weapon",
  "version": "1.0.0",
  "modules": {
    "shared": "Weapon.Shared.dll",
    "client": "Weapon.Client.dll",
    "server": "Weapon.Server.dll"
  },
  "network": {
    "protocols": [
      { "id": "fire",   "version": 1, "direction": "ClientToServer" },
      { "id": "reload", "version": 1, "direction": "ClientToServer" },
      { "id": "state",  "version": 1, "direction": "ServerToClient" }
    ]
  }
}
```

协议清单是**声明**（供握手协商与构建期查重）；真正的 Encode/Decode/Handler 由 Mod DLL 在 `Register` 时注册（§11.4 / §11.6）。

---

## 16. 最终核心规则

| 规则 | 内容 |
|---|---|
| Rule 1 | 一个 Mod 只有一个 `IMod`（`Register()` / `Unregister()`） |
| Rule 2 | Client / Server / Host 是 Context 能力，不是不同 Mod（`HasClient` / `HasServer`） |
| Rule 3 | Mod 自己拥有自己的资源（`Mod → Assets`） |
| Rule 4 | Mod 自己定义自己的 Network Protocol（`Mod → Encode / Decode`） |
| Rule 5 | 所有网络通信必须经过 Network Mod（`Mod ↓ Network Mod ↓ Mirror / Relay`） |
| Rule 6 | Mod 之间的通知通过 Core MessageBus 的消息（Event，Pub/Sub、无返回）；需要返回的跨 Mod 能力访问走 ModCall（`Weapon ↓ inventory:has_item 能力 ↓ Inventory`，§12.11）；不直接访问内部实现 |
| Rule 7 | 依赖关系由 Manifest 声明，由 Mod Loader 统一解析（`Manifest ↓ Dependency Graph ↓ Version Resolve ↓ Load Order ↓ IMod.Register()`） |
| Rule 8 | Mod 依赖必须支持 Client / Server Scope |
| Rule 9 | 所有窗口必须经过 UI.Mod 的 WindowManager 注册和打开，窗口实例与资源仍归属业务 Mod；Core 只保留 IUiContext 抽象（`Mod ↓ 声明窗口 ↓ UI.Mod/WindowManager 调度 ↓ 实例归 Mod Pool/Scope`） |
| Rule 10 | Mod 间的消息必须经过 Core MessageBus，订阅归属 ModObject，卸载时 Core 强制退订（`Mod ↓ Subscribe ↓ MessageBus ↓ UnsubscribeAll(modId)`） |
| Rule 11 | 消息"谁定义谁发布"：Publish/Handler 权限归属 owner Mod，载荷为只读纯数据（`{ModId}:{MessagePath} → readonly struct，传 Id 不传引用`） |
| Rule 12 | Mod 完全隔离：跨 Mod 访问有且只有四条 Core 中介通道——ModCall（能力调用）、Message（事件通知）、UI（窗口协作）、Resource（受限资源解析）；禁止直接引用、静态共享、反射、全局发现、传递委托（§12.10 / §12.11） |
| Rule 13 | 无泛型 ABI：所有跨 ABI 公开 API 禁止泛型方法/泛型参数，一律 Id + 非泛型签名；类型安全由 Source Generator 生成的具体包装类提供（§15.3） |
| Rule 14 | 所有跨 Mod 通信数据全部是二进制：消息载荷、ModCall 参数与返回、网络协议使用同一套线格式与读写原语；进程内不例外（§14.5） |
| Rule 15 | 网络角色与路径正交：Role（Client / Host / Service）× Path（Direct / Relay）是两个独立维度，Relay 是连接路径不是角色；Host 的本地客户端也走完整协议回环管线，不绕过网络（§11.3） |
| Rule 16 | 全局协议 ID 由稳定哈希生成（`xxHash32("{ModId}:{ProtocolPath}")`），两端各自计算天然一致；构建期全局查重，禁止按加载顺序分配（§11.5） |
| Rule 17 | 联网会话存续期间，禁止热卸载注册了网络协议的 Mod；协议更新必须先结束会话（§11.14） |
| Rule 18 | 一个 Mod = 一个功能领域 = Shared / Client / Server 三个运行模块，按环境裁剪加载；不拆成独立的 Client Mod / Server Mod（§15.4） |
| Rule 19 | 契约 = 文档：Mod 之间不共享任何数据契约程序集；消息 / ModCall Args / 协议 Schema 以字节流格式文档发布，消费方基于文档自行实现解析（用 Core 提供的安全读写原语）；线格式统一为字段编号 + 可跳读分帧 + append-only；owner Mod 必须随版本发布测试向量（§14.11） |
| Rule 20 | 注册即归属：Mod 的一切注册（系统 / 订阅 / 协议 / 窗口 / 服务 / 能力 / 池 / 资源 / **实体** / **视图**）归属 ModObject；卸载时 Core 按归属**强制回收**（不依赖 Mod 自觉），顺序为"先停生产者 → 再清状态 → 最后失效化"；相位内卸载请求帧末执行（§7） |

## 17. 最终职责边界表

| 能力 | 谁负责 |
|---|---|
| Mod 生命周期 | Mod Runtime |
| IMod | Mod |
| ECS World | Core / ECS Runtime |
| ECS Component | Mod |
| ECS System | Mod |
| Network Transport | Network Mod（Provider 层，Mirror / Relay 可替换） |
| Network Protocol | Mod |
| Encode/Decode | Mod（Source Generator 生成，无反射） |
| Protocol Registry / 路由 / 版本协商 | Network Mod |
| GlobalProtocolId | 稳定哈希（构建工具查重 + Network Mod 注册期校验） |
| ModChannel 隔离 / 方向与权限校验 | Network Mod |
| 兴趣管理（Relevance） | Network Mod 提供钩子，Mod / Gameplay 提供过滤规则 |
| NetworkId ↔ Entity 映射、Spawn/Despawn、快照/增量 | Network Mod（Replication 管线） |
| Relay Session / 盲转发 | Network Mod（Relay Provider）；云端只做 Session/Discovery/Relay |
| 加密 | Network Mod |
| 分包/组包 | Network Mod |
| 流量统计 | Network Mod |
| 资源存储 | Mod |
| 资源引用 | Mod |
| Asset Loader | Mod |
| Asset Bundle / Addressables / YooAsset | Mod 自己选择 |
| GameObject Pool | Mod |
| VFX Pool | Mod |
| UI Pool | Mod |
| Window 注册表 / 层级 / 窗口栈 / 焦点 | UI.Mod（WindowManager，基础设施 Mod） |
| Window Prefab / ViewModel / 业务逻辑 | Mod |
| Window 实例 | Mod（UI Pool），Core 只持句柄 |
| Message 路由 / 订阅管理 | Core Message Runtime（MessageBus） |
| Message 定义 / 载荷 / 处理逻辑 | Mod |
| Message 订阅生命周期 | 归属 ModObject，卸载强制退订 |
| Message Envelope / 校验 / 配额 / Trace | Core Message Runtime |
| Message Payload / 版本演化（append-only） | 消息的 owner Mod |
| 契约文档（消息 / Args / 协议的字节流 Schema + 测试向量） | owner Mod 发布并维护，与实现同步；Mod 间不共享契约程序集，消费方基于文档自行解析（§14.11） |
| ECS Entity 生命周期 | ECS Runtime（创建/销毁）；实体 owner 归属 ModObject，卸载 `DestroyAll(modId)` 强制回收（§9.2） |
| 场景视图（MonoBehaviour）实例化与销毁 | Core Runtime 通用机制（`ModLoaded` 实例化 / `ModUnloaded` 销毁，归属 ModObject） |
| Mod 间调用 | ModCall（`context.Mods.Call`，§12.11） |
| Mod 依赖 | Mod Loader |
| 主 Mod / 框架 Mod 禁销毁（pinned） | ModManager（Manifest `"pinned": true`，§10.4） |

---

## 18. 修订记录

### V2.0.1（2026-08，pushbox 退出闭环实证修订）

以 `com.sample.pushbox` 的"商店安装 → 游玩 → Esc 退出 → 再进入"闭环为验收场，三个真实缺陷倒逼设计补全（详见 `mod-lifecycle-exit-analysis.md`）：

| 缺陷（实证事故） | 修订 |
|---|---|
| 视图 GameObject 卸载后残留 → NRE | §4 ModObject 增加 Views 归属；§7 管线增加 DestroyViews(modId) |
| 字节加载重装产生副本分歧，Unity 绑定已死首份副本 → NRE | §11.14.1 重装语义：程序集按名去重 + 静态重入（HybridCLR 前） |
| ECS 实体卸载不销毁，重装后挂旧实体 → "操作无反应" | §9.2 实体 owner 化（CreateEntity(owner) / DestroyAll(modId)）；§7 增加步骤 5.1 |

其他补全：

- §6 新增"退出以 ModObject 为原子粒度（Host 前后端同时退出）"；
- §7 IModManager 增加 `UnloadAll`（退出游戏按加载镜像销毁）、`RegisterDirectory` / `LoadFromDirectory`（Mod 商店动态安装通道）；卸载管线重写为 11 步 + 顺序原则 + 相位规则 + 泄漏报告（P0/P1）；
- §10.4 pinned Manifest 字段形式化（主 Mod / 框架 Mod 禁止销毁）；
- §16 新增 Rule 20（注册即归属，卸载强制回收）；
- 构建约定：`mod.json` 的 `boot` 字段（false = 不进启动集，仅商店/打包分发）。

### V2.0.2（2026-08，Mod 定义与要求澄清 + Rule 14 ModCall 二进制化落地）

应"明确 Mod 的要求和定义"的评审，补全表述并记录一处实现对齐：

- §0.1 新增"第一性原理：Mod 独立性"——明确整套架构由"各个 Mod 独立"一条公理推出
  （独立 → 隔离 → 只能字节通信 → 契约即文档 → 可重复定义 → 可热卸载 → UGC 生态）。
- §0.2 新增"Mod 的定义与硬性要求"——把"一个东西要称为 Mod 必须满足什么"收敛为 7 条（对应 Rule 1/3/4/5/12/14/19/7/8）。
- §14.11 / Rule 19 明确"可重复定义"是** intended 模式**而非坏味道：消费方按 owner 发布的契约文档各自实现解析（结构相同、定义独立、互不影响），两端不共享任何编译产物。
- 实现对齐记录：ModCall（§12.11）进程内传输此前为 `object + DynamicInvoke`，现已按 Rule 14 改为二进制
  （`PayloadBuffer ↔ PayloadBuffer` + 通用 `DataCodec`），与消息总线同一套读写原语——"bytes 不可能藏对象引用"
  的物理保证对 ModCall 同样成立。Rule 1–20 全部满足（逐项对照见 `implementation-assessment.md`）。

### V2.0.3（2026-08，契约测试向量机制落地）

§14.11.3 的"owner 发布测试向量"从义务落成机制：

- 框架新增 `Game.Mod.Contract.Wire.TestVector`（样例字段 + 规范编码字节 + `DecodeFields`）。
- owner Mod 发布向量：`com.fps.player.PlayerTestVectors`（position_row）、`com.fps.npc.NpcTestVectors`（npc_row）。
- 消费方 CI 校验：`tests/TestRunner/TestVectorTests`——`weapon.PlayerSnapshot` 与 `npc.PlayerSnap`
  两个独立消费方对同一字节解出一致结果；含漂移负向用例（owner 破坏布局 → 消费方旧解析器检出）。
- 第一方在共享测试程序集跑；UGC 场景向量应序列化进包（样例 + 字节 hex）供消费方 CI 离线校验。

## 19. 结语

这套设计下：

- **Mod 真正成为"自治单元"**——美术、策划、关卡、UI、动画等不同职能只需要最小模块范围就能工作和验证，每个 Mod 可以拥有自己完整的资源管线、运行时资源管理和 Pool，而不用把整个游戏工程暴露给开发者。
- 架构可以概括为一句话：

> **Core 管运行时，Network Mod 管网络，ECS Runtime 管 ECS 世界，Mod 管自己的业务、资源、引用和对象池；Mod 之间只通过公开 API/Service 协作。**

- 非常适合后续做 **Mod 热加载、Mod 商店、UGC、版本管理、服务器 Mod 校验、Mod 沙箱和独立 Mod 开发环境**。
