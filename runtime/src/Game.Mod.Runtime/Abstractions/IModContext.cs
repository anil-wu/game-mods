using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.ECS;

namespace Game.Mod.Runtime
{
    /// <summary>
    /// Mod 的能力边界（§5）：Mod 的所有操作天然带 Scope，只能通过 IModContext 获取能力——
    /// Mod 不允许直接持有 Runtime 全局对象（Game.Instance / NetworkManager.singleton / 静态世界）。
    /// 所有子接口签名全非泛型（Rule 13）：类型安全由生成包装提供，不由泛型提供。
    /// </summary>
    public interface IModContext
    {
        IModInfo Info { get; }

        /// <summary>Mod 管理 + 跨 Mod 能力调用（ModCall，§12.11）。</summary>
        IModManager Mods { get; }

        /// <summary>本 Mod 的资源域（§8：ResourceScope 归属于 ModObject）。</summary>
        IModResourceScope Resources { get; }

        /// <summary>本 Mod 的对象池域（§9：池属于 Mod）。</summary>
        IModPoolScope Pool { get; }

        /// <summary>ECS 注册面（非泛型；World 由 ECS Runtime 拥有，Mod 只注册组件/系统，§9.2）。</summary>
        IEcsContext Ecs { get; }

        /// <summary>网络能力（路由到 Network.Mod，§11）。</summary>
        INetworkContext Network { get; }

        /// <summary>UI 能力（路由到 UI.Mod 的 WindowManager，§10）。</summary>
        IUiContext Ui { get; }

        /// <summary>本端服务定位（框架 Mod 也在此发布基础设施实现，§10.2）。</summary>
        IServiceContext Services { get; }

        /// <summary>消息能力（Pub/Sub、无返回，§13）。</summary>
        IMessageContext Messages { get; }

        /// <summary>Client / Server / Host 是 Context 能力，不是不同的 Mod（Rule 2，§6）。</summary>
        bool HasClient { get; }
        bool HasServer { get; }
        IClientContext? Client { get; }
        IServerContext? Server { get; }

        ILog Log { get; }
    }

    /// <summary>
    /// 消息能力（§13.3）：事件通知（Pub/Sub、无返回），载荷一律二进制（Rule 14）。
    /// 业务代码用生成的非泛型通道类；订阅归属当前 ModObject，卸载时 Core 强制退订。
    /// </summary>
    public interface IMessageContext
    {
        /// <summary>租用载荷写入器（字段编号 + 可跳读分帧，§14.11）。</summary>
        PayloadWriter CreateWriter();

        /// <summary>发布事件（谁定义谁发布，Rule 11；id 命名空间必须属于当前 Mod）。</summary>
        void Publish(MessageId id, int version, in PayloadBuffer payload);

        /// <summary>订阅事件（归属当前 ModObject）。</summary>
        void Subscribe(MessageId id, Game.Messaging.MessageHandler handler);
    }

    /// <summary>
    /// 服务定位（§12.11）：本端 / 本 Mod 内部解耦；框架 Mod（UI.Mod / Network.Mod）
    /// 通过周知 ServiceId 在此发布基础设施实现（§10.2），context.Ui / context.Network 即路由到它们。
    /// </summary>
    public interface IServiceContext
    {
        void Register(ServiceId id, object service);
        void Unregister(ServiceId id);
        object Get(ServiceId id);
        bool TryGet(ServiceId id, out object service);
    }

    /// <summary>
    /// ECS 注册面（非泛型 ABI，Rule 13）。World 保留泛型访问供 Mod 程序集内部的系统代码使用
    /// （跨 ABI 面全非泛型；HybridCLR 泛型补充元数据在接入时处理）。
    /// </summary>
    public interface IEcsContext
    {
        World World { get; }

        /// <summary>注册组件类型（归属当前 ModObject）。</summary>
        void RegisterComponent(System.Type componentType);

        /// <summary>注册系统（归属当前 ModObject，卸载时移除）。</summary>
        void RegisterSystem(ISystem system, SystemSide side);
    }

    /// <summary>Client 环境能力（§6：Client/Server/Host 是 Context 能力）。预留权限边界扩展点。</summary>
    public interface IClientContext
    {
    }

    /// <summary>Server 环境能力（§6）。预留权限边界扩展点。</summary>
    public interface IServerContext
    {
    }
}
