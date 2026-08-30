# Mod 退出机制完善分析 —— 以 com.sample.pushbox 为案例

> 前提文档：V2.0 §7（生命周期/卸载管线）、§4（ModObject 资源边界）。
> 本文以 pushbox 的"安装启动 → Esc 退出 → 再进入"真实闭环为样本，复盘退出机制暴露的全部问题，给出系统化设计与完善路线。

---

## 1. 案例解剖：pushbox 退出时到底要"退掉"什么

一个运行中的 pushbox ModObject，对 Runtime 的全部持有物：

| 类别 | 持有物 | 注册入口 | 归属追踪 |
|---|---|---|---|
| ECS | 6 个组件类型注册 | `context.Ecs.RegisterComponent` | 类型键（不可按 Mod 移除，见 §3.4） |
| ECS | 2 个系统（Push/WinCheck） | `context.Ecs.RegisterSystem` | ✅ owner |
| ECS | **4~N 个实体**（箱子/玩家/会话） | `context.Ecs.CreateEntity` | ✅ owner（本次补全） |
| 网络 | 2 个协议 + 2 个 Handler（闭包持有 World/实体 id） | `context.Network.RegisterProtocol` | ✅ owner |
| 消息 | game_won 事件定义（无订阅） | —（发布方） | ✅ owner 校验 |
| 视图 | PushBoxView GameObject（每帧发输入、OnGUI 渲染） | 运行时 ModLoaded 实例化 | ✅ owner（本次补全） |
| 静态桥 | `World / Context / SessionEntityId / ClientWinner` | Mod 自管理 | ⚠️ 靠 Unregister 自觉 |
| 闭包 | `WinCheckSystem.OnGameWon`（捕获 context） | Mod 自管理 | ⚠️ 系统移除即失效 |
| ModObject | ResourceScope / PoolScope / Context | Core 创建 | ✅ 强制回收 |
| 管理器登记 | _loaded / _loadOrder / 能力 / 服务 | Core | ✅ 强制回收 |
| 程序集 | pushbox.dll（字节加载） | IAssemblyLoader | ⚠️ 平台限制不可卸载 |

**结论：退出机制的本质 = 上表每一行都有明确的、被 Core 强制的回收路径。"谁注册谁归属，归属即可强制回收"。**

---

## 2. 三个真实缺陷复盘（本案例逼出来的补丁）

### 缺陷 1：视图残留（已修）
卸载后 PushBoxView GameObject 仍在场景中，每帧访问已清空的静态桥 → NRE 刷屏。
**修法**：视图按 ModObject 追踪（`_viewsByMod`），`ModUnloaded → DestroyViews`。
**教训**：Mod 生命周期不止覆盖逻辑层——**Unity 场景对象也是 Mod 持有物**。

### 缺陷 2：程序集副本分歧（已修）
字节加载不可卸载，重装产生副本 #2；Unity 脚本绑定按"程序集名+类名"关联到**首份副本**（静态桥已死）→ Awake NRE。
**修法**：`ByteArrayAssemblyLoader` 按名去重复用，注册/静态桥/视图恒落同一份。
**教训**：**重装语义 = 同一程序集上的新 ModObject**——静态状态必须可重入（Register 幂等重设，Unregister 清空）。代码变更需重启进程生效（HybridCLR 接入后由热更正式接管）。

### 缺陷 3：ECS 实体残留（已修）
卸载只移除了系统，旧 Session/箱子/玩家实体残留共享 World；重装后视图取到僵死旧实体 → "操作无反应"。
**修法**：`World.CreateEntity(owner) / DestroyAll(owner)`，卸载管线在移除系统后销毁 Mod 实体。
**教训**：**ModObject 资源边界必须覆盖 ECS 实体**——这是 §4"资源边界"在 ECS 上的应有之义，V2.0 原文未明写，建议补入。

---

## 3. 退出机制的系统化设计（生命周期视角）

### 3.1 注册即归属（一切皆可追踪）

任何 Mod 对 Runtime 的注册动作必须携带 owner（ModId），没有例外：

```
系统/订阅/协议/窗口/服务/能力/池/资源/实体/视图  → 全部 owner 化 ✅（已完成）
组件类型注册                                  → 无法 owner 化（见 §3.4 妥协）
```

### 3.2 退出管线 = 对称注销 + 强制兜底

```
Unload(Mod)
 │
 ├── 1. IMod.Unregister()            ← Mod 自觉（对称注销 + 清静态桥）
 ├── 2. WindowManager.CloseAll(modId) ← 以下全是 Core 强制，不依赖自觉
 ├── 3. MessageBus.UnsubscribeAll(modId)
 ├── 4. Network.UnregisterAll(modId)   （协议 + Handler）
 ├── 5. Systems.RemoveAll(modId)       （先移除系统，再销毁实体——顺序敏感）
 ├── 5.1 World.DestroyAll(modId)       （实体）
 ├── 6. Services / Capabilities 撤销
 ├── 7. Pool.Clear() / Resources.ReleaseAll()
 ├── 8. DestroyViews(modId)            （场景对象）
 ├── 9. Context.Invalidate()           （残留引用即报错而非野指针）
 └── 10. Assembly（复用去重 / 平台限制）
```

**顺序原则**：先停"生产者"（系统/Handler/视图），再清"状态"（实体/池/资源），最后失效化（Context/程序集）。

### 3.3 Host 模式：前后端必须同时退出（原子粒度）

Client / Server 是**同一 ModObject 的两套上下文**（Rule 2，§6）——退出以 ModObject 为粒度原子发生，
禁止“退了画面留了模拟”。卸载管线一次覆盖双端持有物：

| 端 | 持有物 | 清理步骤 |
|---|---|---|
| Server | 权威系统 / 权威实体 / C2S 协议与 Handler | 步骤 4 / 5 / 5.1 |
| Client | S2C 协议与 Handler / 本地落地状态（静态桥） / 订阅 / 视图 | 步骤 4 / 1 / 3 / 8 |

验证：`PushBoxTests.HostMode_ClientAndServer_ExitSimultaneously`——退出后双端注册同帧清零，
大厅（pinned 框架 Mod）双端存活。未来拆 Shared/Client/Server 三模块后，此粒度不变：
模块裁剪影响“装什么”，不影响“退什么”——ModObject 仍是唯一退出单位。

### 3.4 退出语义分层

| 层级 | 语义 | 入口 | 范围 |
|---|---|---|---|
| 软退出 | 退出当前游戏回大厅 | `context.Mods.Unload(自身)`（pushbox Esc） | 单 Mod（含其依赖方），pinned 除外 |
| 硬退出 | 退出应用 / 停止 Play | `ModRuntimeHost.Shutdown()`（OnApplicationQuit/OnDestroy） | UnloadAll 按加载镜像销毁，pinned 随进程存活 |
| 异常退出 | Mod 崩溃熔断 | （未实现，见路线 P3） | 单个问题 Mod |

### 3.5 已知妥协（记录在案）

1. **组件类型注册不可按 Mod 移除**：组件存储是类型键全局结构，实体存活时移除类型不安全。妥协：类型常驻（无状态、无害），实体强制销毁已覆盖实际危害。
2. **程序集不可卸载（Unity 字节加载）**：以"去重复用 + 静态重入"为当前语义；真卸载待 HybridCLR/ALC。
3. **静态桥靠 Unregister 自觉清空**：Core 无法强制清 Mod 自有静态字段。缓解：Context 失效化后旧静态桥调用即抛 `ModStateException`（比野指针安全）。

---

## 4. 完善路线（按优先级）

### P0 — 卸载请求帧末执行（防止迭代中崩溃）✅ 已实现

**问题**：当前 `Unload` 是同步路径。pushbox 在**视图 Update** 中调用（安全），但若 Mod 作者在**系统 Update / 协议 Handler / 消息 Handler** 中调 `context.Mods.Unload(...)`：

- `Systems.RemoveAll` 在 `SystemGroup.Update` 迭代 `_systems` 途中修改集合 → `InvalidOperationException`；
- `World.DestroyAll` 在 `Query<Session>()` 迭代途中销毁实体 → 存储迭代崩溃。

**设计**：与 MessageBus 的"派发中变更延迟生效"（§13.6 规则 3）同构——
`IModManager.Unload` 在 Runtime 驱动相位（Tick）内被调用时转为**卸载请求入队**，当前相位结束（帧末）统一执行；相位外调用仍同步执行（测试/工具路径不变）。

### P1 — 卸载泄漏报告（Mod 质量信号）✅ 已实现

**设计**：Unload 时统计"Core 强制回收了多少项"（系统/订阅/协议/窗口/服务/能力/实体/池/资源各自计数），与 `Unregister` 对称注销对比——**被强制回收 > 0 意味着 Mod 没有对称注销**，输出泄漏报告到日志：

```
[ModManager] 已卸载 com.sample.pushbox（强制回收: 系统 2, 协议 2, 实体 4, 视图 1；对称注销 OK）
```

价值：UGC 生态下让 Mod 作者知道"你的 Unregister 写漏了"，也是审核指标。

实现：Unload 前统计系统/订阅/协议（INetworkRuntimeDiagnostics）/服务/能力/实体计数，
卸载日志输出强制回收报告；相位内卸载经 BeginPhase/EndPhase 入队帧末执行（Tick 已挂接）。

### P2 — Unity 侧资源归属补全

视图已归属销毁。剩余零散项：**协程**（MonoBehaviour 销毁即停，已覆盖）、**Input/UI 事件订阅**（在视图内，已覆盖）、**场景中非视图 GameObject**（Mod 若 `new GameObject` 绕过视图机制则无法追踪——规则：**Mod 的场景对象必须经由自带视图创建**，写入开发规范）。

### P3 — 异常熔断 → 自动卸载

**设计**：系统 Update / Handler 抛异常计入 Mod 维度的熔断计数（消息订阅已有单点熔断），超阈值 → Core 标记该 Mod 为 Faulted 并自动 Unload（退化为软退出），防止坏 Mod 拖垮整个进程。与 §14.8 熔断思想一致，粒度从订阅提升到 ModObject。

### P4 — 真卸载（HybridCLR / 可收集 ALC）

net8 侧 `CollectibleAssemblyLoader` 已就绪；Unity 侧待 HybridCLR 接入后替换字节加载，程序集与静态状态真正回收，"重装 = 全新副本 + 新代码"生效（商店更新新版本无需重启）。

---

## 5. 对 V2.0 文档的修订建议

1. §4 ModObject 内容清单补充：**ECS Entities（owner 化）** 与 **Scene Views（Unity 场景对象，owner 化）**；
2. §7 卸载流程补充两步：`World.DestroyAll(modId)`（系统移除之后）与 `DestroyViews(modId)`（Context 失效之前）；
3. §7 增加原则：**"先停生产者，再清状态，最后失效化"** 的顺序约束；
4. §11.14 旁注：重装语义在 HybridCLR 接入前为"同副本重入"（静态状态幂等重设）。
