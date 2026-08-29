# V2.0 架构迁移实施计划

> 目标：将当前 V1 实现（`design/mod-architecture.md`）迁移到 `design/Unity_Mod_Runtime_架构设计V2.0.md`。
> 分支：`feature/mod-runtime-v2`。所有阶段完成后跑 `tests/run.sh` 验收。

## 现状 → 目标 对照

| 现状（V1） | 目标（V2.0） |
|---|---|
| `IMod.OnLoad/OnEnable/OnDisable` | `IMod.Register/Unregister` 仅两个方法（Rule 1） |
| `IModContext` 直接暴露 World/Systems/MessageBus | 子接口能力边界：Mods/Resources/Pool/Ecs/Network/Ui/Services/Messages/Client/Server（§5） |
| 无 ModObject / ModManager，无卸载 | ModObject（资源边界）+ ModManager（生命周期 + ModCall）（§4/§7） |
| 泛型消息总线（对象引用直传、请求应答） | 二进制载荷 + MessageId 命名空间 + 归属校验 + 强制退订；需要返回走 ModCall（§13/§14，Rule 14） |
| 无能力调用 | `context.Mods.Export/Call`（§12.11，Rule 6） |
| 协议 Mod（IProtocolHost，FNV msgId，按类型） | Network.Mod：稳定哈希 ProtocolId（xxHash32）、方向校验、版本、配额、Role×Path 正交（§11，Rule 15/16） |
| 无 UI 抽象 | Core `IUiContext` + UI.Mod（WindowManager，§10，Rule 9） |
| 无资源/池 Scope | ModObject.Resources（AssetId + Scope）/ ModObject.Pool（PoolId 非泛型）（§8/§9） |
| Manifest v1（modId/entryDll/deps 对象） | Manifest v2（id/modules shared-client-server/deps 数组 optional+scope+features/protocols 声明）（§12.2/§15.4） |
| 无端侧概念（仅 ModSide 文件过滤） | Client/Server/Host 为 Context 能力（HasClient/HasServer，§6，Rule 2） |

## 阶段

1. **Contract v2**：强类型 Id（AssetId/MessageId/CapabilityId/ServiceId/WindowId/PoolId/ProtocolId）、xxHash32、线格式原语（PayloadWriter/Reader，字段编号+可跳读+append-only）、Manifest v2（兼容解析 v1）。
2. **Messaging v2**：MessageEnvelope + MessageBus（二进制、归属校验、UnsubscribeAll、异常隔离/熔断、配额、Trace、派发中订阅变更延迟生效）。
3. **ECS**：SystemGroup 注册归属 ModId + RemoveAll(owner)。
4. **Game.Mod.Runtime**（替代 Game.ModLoader）：全部 ABI 抽象（无泛型，Rule 13）+ ModManager/ModLoader/ModResolver/ModObject/上下文实现 + 卸载管线。
5. **Network.Mod**（com.game.network，替代 com.game.protocol）：协议注册表/路由/方向与版本校验/Loopback 传输/统计。
6. **UI.Mod**（com.game.ui）：WindowManager（注册/开关/CloseAll(modId)）。
7. **Mod 迁移**：com.game.core / com.sample.hello / com.sample.pushbox → IMod v2 + Manifest v2；build-mod.py 适配。
8. **Unity 集成**：ModRuntime 引导 v2（Role 配置）、Mirror 传输适配 INetworkTransport、Plugins 清单更新。
9. **测试**：全部重写/新增并跑绿。
10. **文档**：README v2。

## 务实裁剪（记录在案）

- Source Generator 暂以手写 Codec/包装代替（生成物形态一致：非泛型、字段编号读写）。
- ECS 的 World 保留泛型访问（Mod 程序集内部使用）；跨 ABI 的 `IEcsContext` 注册面全非泛型。HybridCLR 泛型补充元数据问题待接入 HybridCLR 时处理。
- ModCall 采用 §12.11 的 `object Call(...)` 签名（进程内直传冻结 DTO）；跨信任边界时由生成包装走二进制 Codec（Rule 14 的进程内二进制在消息总线严格执行）。
- 组件存储不按 Mod 卸载（类型键存储，实体存活时移除不安全）；系统/订阅/协议/窗口/池/资源全部按 Mod 卸载。
- Unity 程序集真正卸载依赖 HybridCLR/ALC，Unity 侧为字节加载（不卸载），net8 侧用可收集 ALC 验证卸载流程。
