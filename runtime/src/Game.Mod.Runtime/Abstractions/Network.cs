using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;

namespace Game.Mod.Runtime
{
    /// <summary>运行角色：谁在跑网络逻辑（§11.3 Role × Path 正交，Rule 15）。</summary>
    public enum NetworkRole { Client, Host, Service }

    /// <summary>连接路径：数据怎么到达对端（§11.3）。Relay 是路径不是角色。</summary>
    public enum NetworkPath { Auto, Direct, Relay }

    /// <summary>网络启动配置（§11.3：Mod 完全不感知底层是 Direct 还是 Relay）。</summary>
    public sealed class NetworkConfig
    {
        public NetworkRole Role { get; set; }
        public NetworkPath Path { get; set; } = NetworkPath.Auto;
        public string Address { get; set; } = "";
        public int Port { get; set; }
    }

    /// <summary>协议处理器上下文（§11.4 INetworkHandler）。</summary>
    public readonly struct NetworkContext
    {
        /// <summary>来源连接（客户端侧恒为 0 = 服务器）。</summary>
        public readonly int ConnectionId;

        /// <summary>是否在 Server 侧处理。</summary>
        public readonly bool IsServer;

        public NetworkContext(int connectionId, bool isServer)
        {
            ConnectionId = connectionId;
            IsServer = isServer;
        }
    }

    /// <summary>
    /// 统一协议接口（§11.4，非泛型 ABI）：协议（Encode/Decode）与业务处理（Handler）分离。
    /// 协议 ID = 稳定哈希（Rule 16）；Decode 返回的对象应来自 Mod 自己的消息对象池（§11.4 GC 规则）。
    /// </summary>
    public interface INetworkProtocol
    {
        ProtocolId Id { get; }
        ushort Version { get; }
        NetworkDirection Direction { get; }

        /// <summary>载荷上限，发送前校验（§11.12 UGC 防护：越界 / 超 MaxSize 即丢弃并记违规）。</summary>
        int MaxSize { get; }

        void Encode(in object message, INetworkWriter writer);
        object Decode(INetworkReader reader);
    }

    /// <summary>协议业务处理器（与编解码分离，§11.4）。</summary>
    public interface INetworkHandler
    {
        void Handle(in NetworkContext context, in object message);
    }

    /// <summary>
    /// Mod 面对的网络能力（§11.7：通信方向由 API 形态强制）。
    /// 路由到 Network.Mod；Network.Mod 未加载时抛 <see cref="NoServiceException"/>。
    /// </summary>
    public interface INetworkContext
    {
        bool IsActive { get; }

        /// <summary>ECS Replication 能力（§11.10：高频状态走复制管线，不走协议消息）。</summary>
        IReplicationContext Replication { get; }

        /// <summary>注册协议（owner = 当前 Mod；生命周期与 Mod 严格一致，§11.6）。</summary>
        void RegisterProtocol(INetworkProtocol protocol, INetworkHandler? handler);

        void UnregisterProtocol(ProtocolId id);

        /// <summary>Client 唯一出口（§11.7 禁止 Client → Client）。</summary>
        void SendToServer(ProtocolId id, object message);

        /// <summary>Server 单播（面向连接由 Network.Mod 内部映射，§11.8）。</summary>
        void SendToClient(int connectionId, ProtocolId id, object message);

        /// <summary>Server 广播（应过 Interest；原型阶段为全房间）。</summary>
        void Broadcast(ProtocolId id, object message);

        /// <summary>启动网络（由配置驱动，§11.3）。</summary>
        void Start(NetworkConfig config);

        void Stop();
    }

    /// <summary>
    /// 兴趣管理钩子（§11.8）：Server 侧 Broadcast 过滤。
    /// 广播不是无差别全房间发送——Gameplay / Mod 声明 relevance 规则（区域/队伍/观察者集合/距离），
    /// Router 只把包发给"对该协议有可见性"的客户端。
    /// 回调为进程内同线程（与 INetworkHandler 同一注册-回调模式，非跨 Mod 传委托/引用，Rule 12 不触线）。
    /// 默认无 manager = 全房间广播（现状语义，零行为变化）。
    /// </summary>
    public interface IInterestManager
    {
        /// <summary>Server 侧 Broadcast 过滤：连接 connectionId 是否应收到 pid 协议这条消息。</summary>
        bool IsRelevant(int connectionId, ProtocolId protocolId, in object message);
    }

    /// <summary>
    /// 网络传输提供者（§11.2 Provider 层：Mirror / Relay / Loopback 可替换）。
    /// 只看到二进制帧，不理解任何协议（与 Relay 的盲转发同一抽象层级）。
    /// </summary>
    public interface INetworkTransport
    {
        void StartClient();
        void StartServer();
        void Stop();

        /// <summary>
        /// 客户端角色传输就绪（§11.6 握手触发点）：
        /// Loopback 在 StartClient 成功后同步触发；Mirror 在客户端连接建立后触发；Relay 在 BindAck 后触发。
        /// NetworkRuntimeImpl 订阅后发起协议版本协商握手。
        /// </summary>
        event System.Action? ClientReady;

        /// <summary>Client → Server。</summary>
        void SendFromClient(byte[] frame);

        /// <summary>Server → 单个 Client。</summary>
        void SendFromServerTo(int connectionId, byte[] frame);

        /// <summary>Server → 全部 Client。</summary>
        void SendFromServerAll(byte[] frame);

        /// <summary>帧到达 Client 侧。</summary>
        event System.Action<byte[]>? ReceivedOnClient;

        /// <summary>帧到达 Server 侧（connectionId, frame）。</summary>
        event System.Action<int, byte[]>? ReceivedOnServer;

        IReadOnlyCollection<int> ServerConnections { get; }
    }

    /// <summary>
    /// Network.Mod 暴露的网络运行时（框架 Mod 基础设施实现，经周知 ServiceId 发布，§10.2/§11.15）。
    /// 硬边界：可以路由任何协议，但绝不拥有任何 Gameplay Protocol（§11.1）。
    /// </summary>
    public interface INetworkRuntime
    {
        NetworkRole Role { get; }
        bool IsActive { get; }

        void Start(NetworkConfig config);
        void Stop();

        /// <summary>接入传输提供者（Mirror / Loopback / Relay Client）。</summary>
        void AttachTransport(INetworkTransport transport);

        void RegisterProtocol(ModId owner, INetworkProtocol protocol, INetworkHandler? handler);
        void UnregisterProtocol(ModId owner, ProtocolId id);

        /// <summary>按 OwnerMod 一次性清理（§11.6 协议生命周期与 Mod 严格一致）。</summary>
        void UnregisterAll(ModId owner);

        // ---- ECS Replication（§11.10） ----

        /// <summary>注册复制原型（owner = 声明 Mod）。</summary>
        void RegisterArchetype(ModId owner, ArchetypeId id, IComponentCodec[] codecs);

        /// <summary>Server：实体纳入复制，返回分配的 NetworkId。</summary>
        uint SpawnReplicated(ModId owner, Game.ECS.Entity entity, ArchetypeId id);

        /// <summary>Server：停止复制并广播 Despawn。</summary>
        void DespawnReplicated(ModId owner, Game.ECS.Entity entity);

        /// <summary>Client：NetworkId → 本地实体。</summary>
        bool TryGetEntity(uint networkId, out Game.ECS.Entity entity);

        /// <summary>Server：NetworkId → 权威实体。</summary>
        bool TryGetServerEntity(uint networkId, out Game.ECS.Entity entity);

        /// <summary>Client：本地实体 → NetworkId。</summary>
        bool TryGetNetworkId(Game.ECS.Entity entity, out uint networkId);

        void SendToServer(ModId sender, ProtocolId id, object message);
        void SendToClient(ModId sender, int connectionId, ProtocolId id, object message);
        void Broadcast(ModId sender, ProtocolId id, object message);

        /// <summary>
        /// 注册 / 清除兴趣过滤（§11.8；null = 清除回默认全发）。
        /// owner 校验：只有协议 owner 可为其协议声明 relevance 规则；UnregisterAll 按 owner 清理（Rule 20）。
        /// </summary>
        void RegisterInterest(ModId owner, ProtocolId pid, IInterestManager? manager);

        /// <summary>
        /// 注册协议旧版本编解码器（§11.6 废弃窗口硬规则：服务端只保留"当前版本 + 上一个版本"）。
        /// 仅协议 owner 可注册其旧版本 codec；UnregisterAll 按 owner 一并清理（Rule 20）。
        /// </summary>
        void RegisterLegacyCodec(ModId owner, ProtocolId id, ushort version, INetworkProtocol codec);

        NetworkStats GetStats();
    }

    /// <summary>网络运行时诊断（可选实现）：卸载泄漏报告等可观测性钩子。</summary>
    public interface INetworkRuntimeDiagnostics
    {
        int ProtocolCountOf(ModId owner);
    }

    /// <summary>流量统计（§11.13：Connection / Mod / Protocol 三级，原型提供 Mod/Protocol 两级）。</summary>
    public sealed class NetworkStats
    {
        public long BytesSent;
        public long BytesReceived;
        public long MessagesSent;
        public long MessagesReceived;
        public long DroppedInvalid;
        public long DroppedQuota;

        /// <summary>未协商连接上的业务帧 / 已降级禁用协议帧（§11.6 防御：握手完成前业务流量不可注入）。</summary>
        public long DroppedNotNegotiated;

        public readonly Dictionary<ModId, long> BytesByMod = new();
        public readonly Dictionary<ProtocolId, long> MessagesByProtocol = new();
    }
}
