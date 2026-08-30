using System;
using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// 网络运行时实现（§11.2）：编排器——生命周期（Start/Stop/AttachTransport）、
    /// 公开 API 路由（注册/发送/统计）、Dispatch 顶层控制流。
    /// 五层各自独立：ProtocolRegistry（注册表 + legacy codec）/ FrameCodec（12B 帧头）/
    /// NegotiationLayer（§11.6 版本协商）/ InterestManager（§11.8 兴趣过滤）/ SecurityLayer（§11.12 加密）/
    /// TrafficLayer（§11.13 流量配额）。
    /// 硬边界：可以路由任何协议，但绝不拥有任何 Gameplay Protocol（§11.1）——
    /// 只认识 ProtocolId / Version / Encode / Decode / Handler / OwnerMod。
    /// </summary>
    public sealed class NetworkRuntimeImpl : INetworkRuntime, INetworkRuntimeDiagnostics
    {
        /// <summary>帧头：[ProtocolId u32][Ver u16][Flags u16][Payload Len u32]（§11.5）。</summary>
        public const int FrameHeaderSize = FrameCodec.FrameHeaderSize;

        private readonly ProtocolRegistry _registry = new();
        private readonly FrameCodec _codec = new();
        private readonly NetworkStats _stats = new();
        private readonly InterestManager _interest = new();
        private readonly SecurityLayer _security;
        private readonly TrafficLayer _traffic;
        private readonly NegotiationLayer _negotiation;

        private INetworkTransport? _transport;

        /// <summary>
        /// 构造。securityProviderFactory 可注入自定义加密算法（§11.12 算法可替换）；
        /// 缺省使用 AES-CBC+HMAC-SHA256 默认实现（零第三方依赖）。
        /// </summary>
        public NetworkRuntimeImpl(Func<INetworkSecurityProvider>? securityProviderFactory = null)
        {
            _security = new SecurityLayer(
                securityProviderFactory ?? (() => new AesHmacSecurityProvider()), _stats);
            _traffic = new TrafficLayer(_stats);
            _negotiation = new NegotiationLayer(_registry, this);
        }

        public NetworkRole Role { get; private set; }
        public bool IsActive { get; private set; }

        /// <summary>版本协商层（§11.6）：引导/测试装配 RequiredMods 等策略。</summary>
        public NegotiationLayer Negotiation => _negotiation;

        /// <summary>加密层（§11.12）：引导装配 psk（runtime.Security.Enable(psk)）；未启用 = 明文兼容模式。</summary>
        public SecurityLayer Security => _security;

        /// <summary>流量配额层（§11.13）：引导/测试装配预算（runtime.ConfigureBudget）。</summary>
        public TrafficLayer Traffic => _traffic;

        /// <summary>ECS Replication 运行时（由 NetworkMod 装配，§11.10）。</summary>
        public ReplicationRuntime? Replication { get; set; }

        /// <summary>未接入传输时的回退（Host 本地 Loopback 由平台/测试装配）。</summary>
        public INetworkTransport? Transport => _transport;

        public void AttachTransport(INetworkTransport transport)
        {
            if (_transport is not null) DetachTransport();
            _transport = transport;
            _transport.ReceivedOnClient += OnClientFrame;
            _transport.ReceivedOnServer += OnServerFrame;
            _transport.ClientReady += OnClientReady;
            if (IsActive) StartTransportEndpoints();
        }

        public void DetachTransport()
        {
            if (_transport is null) return;
            _transport.ReceivedOnClient -= OnClientFrame;
            _transport.ReceivedOnServer -= OnServerFrame;
            _transport.ClientReady -= OnClientReady;
            _transport = null;
        }

        public void Start(NetworkConfig config)
        {
            Role = config.Role;
            IsActive = true;
            RegisterHandshakeProtocols(); // 握手协议自我注册（§11.6，不依赖 NetworkMod 装配顺序）
            if (_transport is not null) StartTransportEndpoints();
        }

        public void Stop()
        {
            IsActive = false;
            _transport?.Stop();
            _negotiation.Reset(); // 连接状态清零（支持重启）
            _security.Reset(); // 序号/重放窗口清零（保持启用状态与密钥）
            _traffic.Reset(); // 窗口/违规/预算清零
        }

        /// <summary>客户端传输就绪 → 发起版本协商握手（§11.6）。</summary>
        private void OnClientReady()
        {
            if (!IsActive) return;
            _negotiation.SendHello();
        }

        private void StartTransportEndpoints()
        {
            // Host = Server + Client 两套独立上下文（§11.3），本地客户端也走完整协议回环管线
            if (Role is NetworkRole.Host or NetworkRole.Service) _transport!.StartServer();
            if (Role is NetworkRole.Host or NetworkRole.Client) _transport!.StartClient();
        }

        private void RegisterHandshakeProtocols()
        {
            if (_registry.IsRegistered(NegotiationLayer.HelloId)) return;
            _registry.Register(NetworkMod.ModIdValue, new HandshakeHelloProtocol(),
                new HandshakeHelloHandler(_negotiation));
            _registry.Register(NetworkMod.ModIdValue, new HandshakeResultProtocol(),
                new HandshakeResultHandler(_negotiation));
        }

        // ---- 协议注册（§11.6：生命周期与 Mod 严格一致） ----

        public void RegisterProtocol(ModId owner, INetworkProtocol protocol, INetworkHandler? handler) =>
            _registry.Register(owner, protocol, handler);

        public void UnregisterProtocol(ModId owner, ProtocolId id) => _registry.Unregister(owner, id);

        public void UnregisterAll(ModId owner)
        {
            _registry.UnregisterAll(owner); // 含 legacy codecs（Rule 20）
            _interest.UnregisterAll(owner); // 兴趣钩子一并清理（Rule 20）
            _traffic.UnregisterAll(owner); // 预算与统计一并清理（Rule 20）
            Replication?.UnregisterAll(owner); // 复制注册与副本实体一并清理（§11.10）
        }

        public void RegisterLegacyCodec(ModId owner, ProtocolId id, ushort version, INetworkProtocol codec) =>
            _registry.RegisterLegacyCodec(owner, id, version, codec);

        public void RegisterInterest(ModId owner, ProtocolId pid, IInterestManager? manager)
        {
            if (manager is not null)
            {
                if (!_registry.TryGet(pid, out var entry))
                    throw new NetworkException($"协议 {pid} 未注册");
                if (entry.Owner != owner)
                    throw new NetworkException(
                        $"Mod '{owner}' 不能为其他 Mod 的协议 {pid} 注册兴趣（归属 '{entry.Owner}'，§11.8）");
            }
            _interest.Register(owner, pid, manager);
        }

        public void ConfigureBudget(ModId owner, NetworkBudget? budget) => _traffic.ConfigureBudget(owner, budget);

        // ---- ECS Replication（§11.10，委托 ReplicationRuntime） ----

        public void RegisterArchetype(ModId owner, ArchetypeId id, IComponentCodec[] codecs)
        {
            if (Replication is null) throw new NetworkException("Replication 未装配");
            Replication.RegisterArchetype(owner, id, codecs);
        }

        public uint SpawnReplicated(ModId owner, global::Game.ECS.Entity entity, ArchetypeId id)
        {
            if (Replication is null) throw new NetworkException("Replication 未装配");
            return Replication.SpawnReplicated(owner, entity, id);
        }

        public void DespawnReplicated(ModId owner, global::Game.ECS.Entity entity)
        {
            Replication?.DespawnReplicated(owner, entity);
        }

        public bool TryGetEntity(uint networkId, out global::Game.ECS.Entity entity)
        {
            entity = default;
            return Replication is not null && Replication.TryGetEntity(networkId, out entity);
        }

        public bool TryGetNetworkId(global::Game.ECS.Entity entity, out uint networkId)
        {
            networkId = 0;
            return Replication is not null && Replication.TryGetNetworkId(entity, out networkId);
        }

        public bool TryGetServerEntity(uint networkId, out global::Game.ECS.Entity entity)
        {
            entity = default;
            return Replication is not null && Replication.TryGetServerEntity(networkId, out entity);
        }

        public bool IsRegistered(ProtocolId id) => _registry.IsRegistered(id);

        public int ProtocolCountOf(ModId owner)
        {
            var n = 0;
            foreach (var entry in _registry.All)
                if (entry.Owner == owner) n++;
            return n;
        }

        // ---- 发送（§11.7：通信方向由 API 形态强制 + 归属校验；§1.2 发送管线） ----

        public void SendToServer(ModId sender, ProtocolId id, object message)
        {
            var entry = ValidateSend(sender, id, NetworkDirection.ClientToServer);
            _negotiation.ValidateSend(NegotiationLayer.ClientConnId, id); // 未协商/拒绝/禁用 → 抛（§11.6）
            var frame = EncodeFrame(entry, entry.Protocol, message, entry.Protocol.Version); // 客户端恒发自身注册版本
            if (!_traffic.CheckSend(entry.Owner, id, frame.Length)) return; // ⑤ 预算/限流 → drop（§11.13）
            var wire = _security.EncryptC2S(frame); // ⑥ 加密（压缩后、分包前，§11.9；未启用 = 直通）
            _transport!.SendFromClient(wire); // ⑦
            _traffic.AccountSendWire(entry.Owner, NegotiationLayer.ClientConnId, wire.Length); // 加密后传输字节记账
        }

        public void SendToClient(ModId sender, int connectionId, ProtocolId id, object message)
        {
            var entry = ValidateSend(sender, id, NetworkDirection.ServerToClient);
            if (!_negotiation.CanDeliver(connectionId, id)) return; // 未协商/禁用连接不发（§11.6）
            var (version, codec) = _negotiation.SelectSendCodec(connectionId, id, entry);
            var frame = EncodeFrame(entry, codec, message, version);
            if (!_traffic.CheckSend(entry.Owner, id, frame.Length)) return; // ⑤ 预算/限流 → drop（§11.13）
            var wire = _security.EncryptS2C(connectionId, frame); // ⑥ 加密（§11.9 压缩后、分包前）
            _transport!.SendFromServerTo(connectionId, wire); // ⑦
            _traffic.AccountSendWire(entry.Owner, connectionId, wire.Length);
        }

        public void Broadcast(ModId sender, ProtocolId id, object message)
        {
            var entry = ValidateSend(sender, id, NetworkDirection.ServerToClient);

            // §1.2 步骤②③：候选连接 = 已协商 ∧ 未禁用 ∧ 兴趣放行——过滤在 Encode 之前（省带宽）
            var candidates = new List<int>();
            foreach (var connId in _transport!.ServerConnections)
            {
                if (!_negotiation.CanDeliver(connId, id)) continue;
                if (!_interest.IsRelevant(connId, id, in message)) continue;
                candidates.Add(connId);
            }
            if (candidates.Count == 0) return;

            // 帧只 Encode 一次（§1.2：逐连接只多一次发送；多连接版本不一致由协商保证收敛到共同版本）
            var (version, codec) = _negotiation.SelectSendCodec(candidates[0], id, entry);
            var frame = EncodeFrame(entry, codec, message, version);
            if (!_traffic.CheckSend(entry.Owner, id, frame.Length)) return; // ⑤ 预算/限流 → drop（§11.13）
            foreach (var connId in candidates)
            {
                var wire = _security.EncryptS2C(connId, frame); // ⑥ 逐连接加密（各连接独立 Seq，§11.12）
                _transport!.SendFromServerTo(connId, wire); // ⑦
                _traffic.AccountSendWire(entry.Owner, connId, wire.Length);
            }
        }

        private ProtocolEntry ValidateSend(ModId sender, ProtocolId id, NetworkDirection direction)
        {
            if (!IsActive) throw new NetworkException("网络未启动");
            if (_transport is null) throw new NetworkException("未接入传输提供者（AttachTransport）");
            return _registry.ValidateSend(sender, id, direction);
        }

        private byte[] EncodeFrame(ProtocolEntry entry, INetworkProtocol codec, object message, ushort version)
        {
            try
            {
                return _codec.Encode(entry.Id, codec, in message, version);
            }
            catch (NetworkException)
            {
                _stats.DroppedQuota++; // §11.12 UGC 防护：超 MaxSize 丢弃并记违规
                _traffic.RecordViolation(entry.Owner); // 对该 Mod 记违规（配合配额/挂起，§11.13）
                throw;
            }
        }

        // ---- 接收（§1.3 接收管线：解密 → 连接级收包预算 → 帧头 → 协商校验 → Registry 校验 → codec 选择 → Decode → Handler） ----

        private void OnServerFrame(int connectionId, byte[] wire)
        {
            var frame = _security.DecryptC2S(connectionId, wire); // ⑥' 信封解析 + 认证（Relay 只见信封，§11.12）
            if (frame is null) return; // DroppedAuth / DroppedReplay 已计数
            if (!_traffic.AccountReceive(connectionId, frame.Length, enforce: true)) return; // ⑤' 连接级收包预算（§11.13）
            Dispatch(frame, new NetworkContext(connectionId, isServer: true));
        }

        private void OnClientFrame(byte[] wire)
        {
            var frame = _security.DecryptS2C(wire);
            if (frame is null) return;
            if (!_traffic.AccountReceive(NegotiationLayer.ClientConnId, frame.Length, enforce: false)) return; // 客户端只记账不执行
            Dispatch(frame, new NetworkContext(0, isServer: false));
        }

        private void Dispatch(byte[] frame, in NetworkContext context)
        {
            if (!_codec.TryParseHeader(frame, out var id, out var version, out var payloadOffset, out var payloadLen))
            {
                _stats.DroppedInvalid++;
                return;
            }

            // 基础设施（握手）帧不计入业务统计（§11.13 业务口径：既有测试对精确计数的断言不受握手污染）
            var isHandshake = NegotiationLayer.IsHandshake(id);
            if (!isHandshake) _stats.BytesReceived += frame.Length;

            // Registry 校验：Owner / Direction（§11.9）
            if (!_registry.TryGet(id, out var entry))
            {
                _stats.DroppedInvalid++; // 在途包无法归属（§11.14）
                return;
            }
            var expectServer = entry.Protocol.Direction == NetworkDirection.ClientToServer;
            if (expectServer != context.IsServer)
            {
                _stats.DroppedInvalid++; // 方向不符（C2S 只能在 Server 侧收，S2C 只能在 Client 侧收）
                return;
            }

            // 协商校验（§11.6）：握手帧放行；未协商 / 拒绝 / 禁用 → drop（防御：握手完成前业务流量不可注入）
            if (!_negotiation.ValidateReceive(context.ConnectionId, id, out var expectedVersion))
            {
                _stats.DroppedNotNegotiated++;
                return;
            }
            if (version != expectedVersion)
            {
                _stats.DroppedInvalid++; // 协议废弃窗口：版本不匹配（§11.6）
                return;
            }
            if (payloadLen > entry.Protocol.MaxSize)
            {
                _stats.DroppedInvalid++;
                return;
            }

            var codec = _negotiation.SelectReceiveCodec(context.ConnectionId, id, entry, expectedVersion);
            var payload = new PayloadBuffer(frame, payloadOffset, payloadLen);
            object message;
            try
            {
                message = codec.Decode(new PayloadReader(in payload));
            }
            catch (Exception)
            {
                _stats.DroppedInvalid++;
                return;
            }

            if (!isHandshake) _stats.MessagesReceived++;
            entry.Handler?.Handle(in context, in message);
        }

        // ---- 统计（§11.13） ----

        public NetworkStats GetStats() => _stats;
    }

    /// <summary>握手 hello 帧处理器：服务器侧收到客户端协议表 → 交集计算并回 result（§11.6）。</summary>
    public sealed class HandshakeHelloHandler : INetworkHandler
    {
        private readonly NegotiationLayer _negotiation;

        public HandshakeHelloHandler(NegotiationLayer negotiation) => _negotiation = negotiation;

        public void Handle(in NetworkContext context, in object message)
        {
            if (!context.IsServer) return;
            _negotiation.OnServerHello(context.ConnectionId, (HandshakeHello)message);
        }
    }

    /// <summary>握手 result 帧处理器：客户端侧收到协商结果 → 应用（§11.6）。</summary>
    public sealed class HandshakeResultHandler : INetworkHandler
    {
        private readonly NegotiationLayer _negotiation;

        public HandshakeResultHandler(NegotiationLayer negotiation) => _negotiation = negotiation;

        public void Handle(in NetworkContext context, in object message)
        {
            if (context.IsServer) return;
            _negotiation.OnClientResult((HandshakeResult)message);
        }
    }
}
