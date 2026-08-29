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
    /// 网络传输提供者（§11.2 Provider 层：Mirror / Relay / Loopback 可替换）。
    /// 只看到二进制帧，不理解任何协议（与 Relay 的盲转发同一抽象层级）。
    /// </summary>
    public interface INetworkTransport
    {
        void StartClient();
        void StartServer();
        void Stop();

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

        void SendToServer(ModId sender, ProtocolId id, object message);
        void SendToClient(ModId sender, int connectionId, ProtocolId id, object message);
        void Broadcast(ModId sender, ProtocolId id, object message);

        NetworkStats GetStats();
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
        public readonly Dictionary<ModId, long> BytesByMod = new();
        public readonly Dictionary<ProtocolId, long> MessagesByProtocol = new();
    }
}
