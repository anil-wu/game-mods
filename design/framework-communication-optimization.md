# 框架 Mod 间通信优化空间分析

> 范围：V2.0 跨 Mod 通信四条通道（Rule 12）——ModCall（请求/响应）、Message（Pub/Sub 事件）、UI、Resource。
> 本文聚焦 **ModCall + Message**（真正的通信热路径）的现状、代价与优化空间；UI/Resource 是句柄/资源域，非通信热路径。
> 依据源码：`Game.Mod.Contract/Wire/*`（PayloadWriter/Reader/Buffer/DataCodec）、`Game.Messaging/MessageBus.cs`、
> `Game.Mod.Runtime/Runtime/ModManager.cs`（Call/Export/Invoke）、`Game.Mod.Contract/Ids.cs`。

---

## 1. 现状概述

- **线格式**：字段编号二进制（u16 fieldId + u8 wireType + 数据；Varint/Fixed32/Fixed64/LengthDelimited），自描述、append-only、未知字段 O(1) 跳过。
- **PayloadWriter**：直写缓冲（`MemoryStream(256)`），无反射无装箱（§14.12）。`ToBuffer()` = `_stream.ToArray()`（每次新建 byte[]）。
- **PayloadReader**：游标 + `TryFind` 每次**从头扫描**定位字段；懒解码 + 字段级跳读。
- **DataCodec**：`object?[]` ↔ 二进制（便利路径，定位离散/低频/动态形状）。
- **MessageBus**：`Dictionary<MessageId, List<Subscription>>`；Publish 一次编码，N 订阅者共享只读视图（零拷贝）；快照派发 + 熔断 + Trace/Stats + 延迟变更。
- **ModCall**：`Dictionary<CapabilityId, (Owner, Handler)>`；Call 先做依赖校验（遍历依赖闭包）+ NoMod/NoCapability 校验 + 同步调 handler。
- **Id**：`ProtocolId.Value = xxHash32` 在构造时预计算，`GetHashCode() => (int)Value`（已优化，字典查询无重算）。

---

## 2. 优化空间（按优先级）

### 高优先级（热点路径、明确收益、非破坏）

#### ① PayloadReader 读 N 字段 O(N²) 重扫 → 懒建字段索引 O(1)
**现状**：`TryFind` 每次 `_cursor = 0` 从头扫描到目标字段；读 N 个字段 = O(N × 载荷长度)。字段多（10+）或载荷大时平方退化。
**优化**：首次 `TryFind` 时懒建 `Dictionary<int,(WireType type,int dataStart,int end)>` 字段索引，后续字段 O(1) 定位。API 不变（TryRead* 签名不动），纯内部加速。
**收益**：多字段消息（如快照/结算/复杂能力返回）解码从 O(N²)→O(N+字段索引构建)。
**风险**：低（内部实现，线格式/消费方解析器均不变）。

#### ② MessageBus 每 Publish 一次 `list.ToArray()` 快照分配 → 直接遍历活列表
**现状**：`Publish` 内 `var snapshot = list.ToArray()` 每发布一次分配一个数组。
**分析**：`Subscribe`/`UnsubscribeAll` 在 `_dispatching > 0` 时都走 `_pendingMutations` 延迟队列，派发期间列表**不会被直接修改**，快照与活列表等价。
**优化**：去掉 `ToArray()`，`foreach (var sub in list)` 直接遍历（延迟变更机制保证安全）。
**收益**：每 Publish 省一次数组分配 + 拷贝；高频事件（击杀/计分/HUD 刷新）GC 压力明显下降。
**风险**：低（需确认无任何绕过 `_pendingMutations` 的派发期直接改表路径）。

#### ③ ModCall 依赖校验 O(deps) 每次 → 加载时预计算 HashSet O(1)
**现状**：`Call` 每次 `ApplicableDependencies(...).Any(d => d.Id == target)` 遍历调用方依赖闭包。
**优化**：Mod 加载时预计算适用依赖的 `HashSet<ModId>`，Call 时 O(1) `Contains(target)`。
**收益**：高频 ModCall（伤害/查询/拾取）省掉每次线性扫描。
**风险**：低（纯缓存，语义不变）。

#### ④ PayloadWriter `ToArray()` 每写分配 → ArrayPool / 复用缓冲
**现状**：`ToBuffer() => new PayloadBuffer(_stream.ToArray())` 每编码一次分配一个 byte[]。
**优化**：`ArrayPool<byte>.Shared` 租/还；或复用 writer + `PayloadBuffer` 直接引用内部缓冲（配合生命周期约束）。
**收益**：高频编码（ECS 快照、每帧消息）GC 分配大幅下降。
**风险**：中（Pool 归还时机需与 PayloadBuffer 生命周期对齐，避免使用已归还缓冲）。

### 中优先级（明确但需权衡）

#### ⑤ DataCodec 装箱 + 嵌套 `ToArray()` 中间分配 → 高频走手写字段 Codec（已推荐，补零分配路径）
**现状**：`Write(object[])` 每个值装箱；嵌套数组 `Write(arr).ToArray()` 产生中间缓冲。
**优化**：高频/强类型能力继续走手写 `PayloadWriter` 直写（文档已建议）；DataCodec 内避免嵌套 `ToArray()`（改 `WriteBytes` 直写子缓冲）。
**收益**：动态形状能力的分配下降。
**风险**：低。

#### ⑥ Trace/Stats 采样化
**现状**：每 Publish/Call 一次 `Stopwatch.GetTimestamp()` + 环形 Trace 写 + Stats 累加。
**优化**：默认关 Trace 或按采样率记录（`TraceCapacity=0` 时短路）。
**收益**：极高频路径省掉计时/写入开销。
**风险**：低（诊断能力保留开关）。

### 低优先级（破坏性 / 长期）

#### ⑦ 字段头紧凑化（3 字节 → varint tag）
**现状**：每字段固定 3 字节头（u16 fieldId + u8 wireType）。
**优化**：protobuf 式 `(fieldId<<3)|wireType` varint（小 fieldId 1 字节）。
**收益**：小字段载荷（单 bool/int）体积减半。
**风险**：**破坏线格式**（契约、测试向量、已上线包全要迁）——按 §14.11 契约机制统一升版本。

#### ⑧ 异步 ModCall / 批量调用
**现状**：ModCall 同步阻塞（handler 返回前 caller 挂起）。
**优化**：为会做 I/O 的能力（如 leaderboard submit）提供异步/批量形态（回调或 Task）；高频小调用支持批量（一次编码多个 args）。
**收益**：跨 Mod I/O 不阻塞主循环；批量省单次调用开销。
**风险**：中高（ABI 变化 + 生命周期/热卸载语义需重设计）。

---

## 3. 建议落地顺序

| 阶段 | 项 | 说明 |
|---|---|---|
| 短期（非破坏，立即收益） | ② ③ ④ ⑥ | 消分配 + 依赖校验缓存 + Trace 采样——纯内部，线格式不动 |
| 中期 | ① ⑤ | 字段索引（Reader 内加速）+ DataCodec 零中间分配 |
| 长期（破坏性，需版本迁移） | ⑦ ⑧ | 线格式紧凑化 + 异步/批量 ModCall |

---

## 4. 结论

框架通信设计本身是合理的（自描述线格式、零拷贝共享视图、预计算 Id 哈希、配额/熔断/延迟变更都已到位）。
**主要优化空间在分配与二次复杂度**：
1. Reader 的 O(N²) 重扫 → 字段索引（多字段消息最大收益）；
2. MessageBus 冗余快照分配（每 Publish 一次，易改且安全）；
3. 编码/依赖校验的重复分配与线性扫描（ArrayPool + HashSet 缓存）。

这些都不破坏 Rule 12/13/14 的红线，也不改变线格式，可作为一次独立的性能 PR 落地；破坏性的线格式紧凑化与异步 ModCall 属长期演进，需走契约升版本。

---

## 5. AI 开发视角的优化空间（更高优先级）

> 本框架定位是「**为 AI 开发游戏而设**」（AI agent 写 Mod、UGC 生态）。因此真正的「优化空间」优先级应倒过来：
> **DX（让 AI 少写、写对、快速迭代）>> 运行时微性能**。性能项（§2/§3）只解决"跑得快"，DX 项解决"AI 用得省"。

### 5.1 核心缺口：Source Generator（codegen）——最大的 DX 优化
**现状**：跨 Mod 强类型数据要靠 AI **手写**字段 Codec（PayloadWriter/Reader 逐字段编号+类型），消费方还要按对方 CONTRACT.md **各自手写解析器**。每个 Mod 每条协议都是重复且易错的样板（字段编号错位、类型错位、append 漏同步）。
**优化**：从一份 schema（或 CONTRACT.md 的可机器读片段）**源生成**：owner 侧强类型 Codec + 消费方解析器 + 测试向量桩。AI 只声明字段，其余自动生成。
**收益**：
- 字段编号/类型对齐错误 → 编译期消除（不再手写）；
- 契约改布局 → 重新生成，消费方漂移在测试向量校验处自动暴露；
- AI 产出速度与正确率大幅提升（这是"AI 开发游戏"定位下**最高优先级**）。

### 5.2 契约 → 测试向量自动校验闭环
**现状**：owner 手写 `XxxTestVectors`，消费方手写一致性测试。
**优化**：codegen 生成 codec 后，自动把 owner 的测试向量编译进消费方一致性断言（跨消费方漂移自动报警）。

### 5.3 强类型替代 DataCodec（编译期检查）
**现状**：DataCodec 是 `object?[]` 动态路径（装箱 + 无编译期类型检查，字段错位只到运行时才炸）。
**优化**：codegen 提供强类型包装（编译期检查），DataCodec 退居真正动态场景（console 转发）。

### 5.4 错误信息可操作化（AI 调试友好）
**现状**：NoCapability/NoMod/Dependency 异常只有事实描述。
**优化**：错误里带"怎么修"提示（如 `Mod 'X' 未声明对 'Y' 依赖 → 在 manifest.json dependencies 加 {id:Y, version:...}`），AI 看到即可修。

### 5.5 快速迭代闭环（热卸载 + 无头测试 + 商店）
**现状**：已具备热卸载、无头测试、商店分发，但缺"改 Mod → 一键重载 → 测试 → 验收"的编排脚本。
**优化**：一条命令（rebuild → hot-reload → run tests → 报告）缩短 AI 迭代周期。

### 优先级建议（修订）

| 阶段 | 项 | 说明 |
|---|---|---|
| **P0（AI 开发游戏定位下的最高优先级）** | 5.1 Source Generator + 5.2/5.3 | 消除手写二进制样板，编译期对齐，契约漂移自动暴露 |
| P1 | 5.4 错误可操作化 + 5.5 迭代闭环 | AI 调试/迭代效率 |
| P2（非破坏性能） | §2 ② ③ ④ ⑥ | 运行时分配优化（价值次要，顺手做） |
| P3（长期/破坏） | §2 ① ⑤ ⑦ ⑧ | 字段索引/紧凑化/异步 |
