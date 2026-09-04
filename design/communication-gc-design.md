# 通信与 GC 优化设计（修订版）

> 替代「通信与 GC 优化版」提案中的「同进程 Direct Dispatch」与「Contract ≠ Serialization → 同进程不 Binary」两节。
> 结论：**保留跨 Mod Binary 边界；性能优化集中在 Binary Runtime 本身；高频协作下沉到 Runtime-owned ECS 数据面。**

---

## 1. 两条核心原则

### 原则一：Mod 隔离依赖 Binary Boundary
> 性能优化**不绕过 Boundary**，而是让 Boundary 本身做到 **Zero Object Reference、Zero/Low Copy、Zero GC**。

Binary 不只是「传输格式统一」，它承担三个架构职责：
1. **热卸载安全**：确保 HybridCLR Mod 卸载时不会残留其他 Mod 的类型/对象引用；
2. **AOT/热更解耦**：避免 AOT/热更程序集之间形成强类型耦合（Rule 13 无泛型 ABI）；
3. **UGC 隔离**：让 UGC/第三方 Mod 只依赖协议契约，无需链接宿主或其他 Mod 程序集（Rule 19）。

因此，`IDamageService.Apply(in DamageRequest)` 这类跨 Mod 直接类型调用（若 IDamageService/DamageRequest 属某 Mod）**违反隔离原则**；放进共享 Contract DLL 也意味着 Contract Assembly 变成永久稳定 ABI——那改变了 Rule 19，不是单纯性能优化。**不采用。**

### 原则二：高频协作优先下沉到 ECS 数据面
> 高频业务协作优先下沉到 **Runtime-owned ECS Data Plane**，而不是优化成 Mod-to-Mod 强类型调用。

同一个 ECS World 中，不同 Mod 注册的 System 通过 Runtime 拥有的 ECS 数据面（Component / Event / CommandBuffer）高频协作——这条路径不是普通 Mod Message，跨的是 ECS 数据面，不持有对方 CLR 类型实例，可继续追求 Zero Serialization / Zero Boxing / Zero Managed Allocation。

---

## 2. 通信模型（修订）

```text
                        Contract
                            │
          ┌─────────────────┼─────────────────┐
          │                 │                 │
          ▼                 ▼                 ▼
     ECS Hot Path      Mod Boundary       Network
          │                 │                 │
   Event/Command        Binary            Binary
   Native Data         Message           Protocol
          │                 │                 │
      Zero GC       Optimized Binary  Optimized Binary
```

- **ECS 高频（跨 System）**：`Mod A System → ECS Component/Event/CommandBuffer → Runtime-owned ECS World → Mod B System`——零序列化、零装箱、零分配。
- **Mod 边界（跨 Mod）**：`Mod A → Message/ModCall Contract → Binary → Runtime → Binary → Mod B`——不传对象引用、不共享 CLR 类型、边界数据统一 Binary。
- **网络（跨进程）**：`Contract → Generated Codec → Network Buffer → Transport`——Binary + 生成 Codec + 缓冲池。

> 关键区分：**「跨 Mod」≠「跨 System」**。跨 System 走 ECS 数据面（零拷贝），跨 Mod 走 Binary 边界（隔离），两者是不同通道。

---

## 3. 三层 Contract（重定义，避免误导）

> 不引入共享 CLR Contract Assembly；三层都围绕「契约」而非「共享类型」。

| 层 | 含义 | 例（DamageRequest） |
|---|---|---|
| **Type Contract**（语义契约） | 业务数据与接口语义 | Target / Damage / Source |
| **Wire Contract**（线契约） | 二进制布局/字段/版本/编码 | MessageId、字段编号、Version、wireType |
| **Runtime Contract**（运行时契约） | 路由/生命周期/归属/频率/权限 | Mod A→Mod B、Request/Response、Max 100/s、Timeout、Client/Server 权限 |

Type Contract 是**文档语义**（CONTRACT.md），不是共享程序集；Wire Contract 是字段编号线格式（§14.11.1）；Runtime Contract 是路由/配额/权限（§14.10/§12.11）。

---

## 4. Binary 优化规范（正式提升到架构级）

> 优化的对象不是「Binary 本身」，而是 Binary 周围产生的**对象、复制、索引、中间层**。

1. **Generated Codec**：从 Wire Contract 源生成 Writer/Reader（消除手写字段样板，AI 友好）；
2. **BufferPool / ArrayPool**：复用编码缓冲（替代 `ToArray()` 每写分配）；
3. **Reader Zero-copy View**：`TryReadView` 零拷贝子视图（不复制 bytes）；
4. **Span / ReadOnlySpan**：netstandard2.1 兼容的跨度读写；
5. **DecodeInto**：解码到预分配 struct（`bool TryReadInt32(FieldId f, out int v)`），不产中间对象；
6. **直接写 ECS CommandBuffer**：Binary → ECS，一次解析一次写入；
7. **Field Index O(1)**：懒建字段索引，消除 `TryFind` 每次从头扫的 O(N²)；
8. **避免重复 Snapshot**：MessageBus 派发去掉冗余 `list.ToArray()`（派发期变更已延迟队列）；
9. **避免中间 Message Object**：不产 `CLR Message → Gameplay Object → ECS Command` 链；
10. **禁止高频 `object Decode(byte[])`**：高频一律强类型字段 Codec / 生成 Reader。

**反例**：`object Decode(byte[] data)`（object/boxing/new message/byte[] 分配/类型转换）。
**正例**：`bool TryReadInt32(FieldId field, out int value)`，或 `BinaryView → Generated Reader → ECS CommandBuffer`。

---

## 5. GC 架构（保留）

### 5.1 Hot Path（Managed Allocation = 0）
ECS Simulation/Query/Event/Command、高频 Mod Message、高频 Network、Movement、Combat。
禁止：new、boxing、LINQ、closure、iterator、临时 List/Dictionary/byte[]、string 拼接、object API。
优先：readonly struct、NativeArray、BufferPool、RingBuffer、Arena、预分配、Source Generator。

### 5.2 Cold Path（受控 GC）
Mod Load/Unload、Resource Load、UI 初始化、低频 RPC、编辑器工具、配置解析。

### 5.3 MemoryScope（归属 ModObject）
管理 Scratch Buffer、Message Buffer、Command Buffer、Native Memory、Temporary Arena、Network Buffer；Mod 卸载统一回收。

### 5.4 PoolScope
Initial Capacity / Soft Max / Hard Max / Trim Policy / Lifetime；池化高频消息、Packet Buffer、临时 Native Buffer、高频 GameObject、高频 ECS 临时数据。

### 5.5 Allocation Budget
按 Mod/System/Frame/Tick/Network Channel 统计 Managed Allocation；超标标记 VIOLATION。

### 5.6 Runtime Allocation Profiler
回答「哪个 Mod/System/Tick 分配了多少、是否违反 Budget」——对 AI Generated Mod 尤其重要（AI 易产 new List/LINQ/closure/boxing），由 Runtime + Analyzer + Profiler 共同约束，不依赖开发者或 AI 自觉。

---

## 6. AI Native（保留）

AI 只需理解：当前 Mod、Mod Contract、Runtime API、数据模型、性能 Budget、测试规则；
不需理解整个 Game 工程（1000+ Scripts、全量 Assets、全部 Systems、全部历史代码）。

UGC / 官方 / AI 生成内容统一建立在同一 Mod Runtime 上（同一生命周期、Context、ECS、Scope、Communication、Budget、Validation）。

---

## 7. 最终模型

| 通道 | 边界 | 序列化 | 目标 |
|---|---|---|---|
| ECS 高频（跨 System） | Runtime-owned ECS 数据面 | 无（Native Data） | Zero Serialization / Zero Boxing / Zero GC |
| Mod 边界（跨 Mod） | Binary Boundary | 有（Optimized Binary） | Zero Object Reference / Zero-Low Copy / Zero GC Hot Path |
| 网络（跨进程） | Binary Protocol | 有（Generated Codec + Buffer Pool） | Zero/Low GC |

**核心一句话**：
> Mod 隔离依赖 Binary Boundary；性能优化不绕过 Boundary，而是让 Boundary 本身做到 Zero Object Reference、Zero/Low Copy、Zero GC。
> 高频业务协作优先下沉到 Runtime-owned ECS Data Plane，而不是优化成 Mod-to-Mod 强类型调用。
