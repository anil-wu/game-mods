using System;
using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>协议注册表条目（§11.6）。</summary>
    internal sealed class ProtocolEntry
    {
        public ProtocolId Id;
        public ModId Owner;
        public INetworkProtocol Protocol = null!;
        public INetworkHandler? Handler;
    }

    /// <summary>
    /// 网络运行时实现（§11.2）：协议注册表 / 路由 / 方向与版本校验 / 分包帧 / 统计。
    /// 硬边界：可以路由任何协议，但绝不拥有任何 Gameplay Protocol（§11.1）——
    /// 只认识 ProtocolId / Version / Encode / Decode / Handler / OwnerMod。
    /// </summary>
    public sealed class NetworkRuntimeImpl : INetworkRuntime, INetworkRuntimeDiagnostics
    {
        /// <summary>帧头：[ProtocolId u32][Ver u16][Flags u16][Payload Len u32]（§11.5）。</summary>
        public const int FrameHeaderSize = 12;

        private readonly Dictionary<ProtocolId, ProtocolEntry> _protocols = new();
        private readonly NetworkStats _stats = new();
        private INetworkTransport? _transport;

        public NetworkRole Role { get; private set; }
        public bool IsActive { get; private set; }

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
            if (IsActive) StartTransportEndpoints();
        }

        public void DetachTransport()
        {
            if (_transport is null) return;
            _transport.ReceivedOnClient -= OnClientFrame;
            _transport.ReceivedOnServer -= OnServerFrame;
            _transport = null;
        }

        public void Start(NetworkConfig config)
        {
            Role = config.Role;
            IsActive = true;
            if (_transport is not null) StartTransportEndpoints();
        }

        public void Stop()
        {
            IsActive = false;
            _transport?.Stop();
        }

        private void StartTransportEndpoints()
        {
            // Host = Server + Client 两套独立上下文（§11.3），本地客户端也走完整协议回环管线
            if (Role is NetworkRole.Host or NetworkRole.Service) _transport!.StartServer();
            if (Role is NetworkRole.Host or NetworkRole.Client) _transport!.StartClient();
        }

        // ---- 协议注册（§11.6：生命周期与 Mod 严格一致） ----

        public void RegisterProtocol(ModId owner, INetworkProtocol protocol, INetworkHandler? handler)
        {
            if (protocol is null) throw new ArgumentNullException(nameof(protocol));
            if (_protocols.TryGetValue(protocol.Id, out var existing))
            {
                if (existing.Owner != owner)
                    throw new NetworkException(
                        $"协议 ID {protocol.Id} 冲突: '{owner}' 与 '{existing.Owner}'（构建期查重漏网，§11.5）");
                return; // 幂等
            }
            _protocols[protocol.Id] = new ProtocolEntry
            {
                Id = protocol.Id,
                Owner = owner,
                Protocol = protocol,
                Handler = handler,
            };
        }

        public void UnregisterProtocol(ModId owner, ProtocolId id)
        {
            if (_protocols.TryGetValue(id, out var entry) && entry.Owner == owner)
                _protocols.Remove(id);
        }

        public void UnregisterAll(ModId owner)
        {
            var stale = new List<ProtocolId>();
            foreach (var (id, entry) in _protocols)
                if (entry.Owner == owner) stale.Add(id);
            foreach (var id in stale) _protocols.Remove(id);
            Replication?.UnregisterAll(owner); // 复制注册与副本实体一并清理（§11.10）
        }

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

        public bool IsRegistered(ProtocolId id) => _protocols.ContainsKey(id);

        public int ProtocolCountOf(ModId owner)
        {
            var n = 0;
            foreach (var entry in _protocols.Values)
                if (entry.Owner == owner) n++;
            return n;
        }

        // ---- 发送（§11.7：通信方向由 API 形态强制 + 归属校验） ----

        public void SendToServer(ModId sender, ProtocolId id, object message)
        {
            var entry = ValidateSend(sender, id, NetworkDirection.ClientToServer);
            var frame = EncodeFrame(entry, message);
            _transport!.SendFromClient(frame);
            CountSend(entry, frame.Length);
        }

        public void SendToClient(ModId sender, int connectionId, ProtocolId id, object message)
        {
            var entry = ValidateSend(sender, id, NetworkDirection.ServerToClient);
            var frame = EncodeFrame(entry, message);
            _transport!.SendFromServerTo(connectionId, frame);
            CountSend(entry, frame.Length);
        }

        public void Broadcast(ModId sender, ProtocolId id, object message)
        {
            var entry = ValidateSend(sender, id, NetworkDirection.ServerToClient);
            var frame = EncodeFrame(entry, message);
            _transport!.SendFromServerAll(frame);
            CountSend(entry, frame.Length);
        }

        private ProtocolEntry ValidateSend(ModId sender, ProtocolId id, NetworkDirection direction)
        {
            if (!IsActive) throw new NetworkException("网络未启动");
            if (_transport is null) throw new NetworkException("未接入传输提供者（AttachTransport）");
            if (!_protocols.TryGetValue(id, out var entry))
                throw new NetworkException($"协议 {id} 未注册");
            if (entry.Owner != sender)
                throw new NetworkException($"Mod '{sender}' 不能发送其他 Mod 的协议 {id}（归属 '{entry.Owner}'）");
            if (entry.Protocol.Direction != direction)
                throw new NetworkException($"协议 {id} 方向为 {entry.Protocol.Direction}，不能以 {direction} 发送");
            return entry;
        }

        private byte[] EncodeFrame(ProtocolEntry entry, object message)
        {
            var writer = new PayloadWriter();
            entry.Protocol.Encode(in message, writer);
            var payload = writer.ToBuffer();

            if (payload.Length > entry.Protocol.MaxSize)
            {
                _stats.DroppedQuota++;
                throw new NetworkException(
                    $"协议 {entry.Id} 载荷 {payload.Length}B 超 MaxSize {entry.Protocol.MaxSize}B（§11.12 UGC 防护）");
            }

            var frame = new byte[FrameHeaderSize + payload.Length];
            WriteU32(frame, 0, entry.Id.Value);
            WriteU16(frame, 4, entry.Protocol.Version);
            WriteU16(frame, 6, 0); // Flags
            WriteU32(frame, 8, (uint)payload.Length);
            Array.Copy(payload.Data, payload.Offset, frame, FrameHeaderSize, payload.Length);
            return frame;
        }

        // ---- 接收（§11.9 接收管线：校验 → Decode → Router → Handler） ----

        private void OnServerFrame(int connectionId, byte[] frame) =>
            Dispatch(frame, new NetworkContext(connectionId, isServer: true));

        private void OnClientFrame(byte[] frame) =>
            Dispatch(frame, new NetworkContext(0, isServer: false));

        private void Dispatch(byte[] frame, in NetworkContext context)
        {
            _stats.BytesReceived += frame.Length;
            if (frame.Length < FrameHeaderSize)
            {
                _stats.DroppedInvalid++;
                return;
            }

            var id = new ProtocolId(ReadU32(frame, 0));
            var version = ReadU16(frame, 4);
            var len = (int)ReadU32(frame, 8);
            if (len < 0 || FrameHeaderSize + len > frame.Length)
            {
                _stats.DroppedInvalid++;
                return;
            }

            // Registry 校验：Owner / Direction / Version（§11.9）
            if (!_protocols.TryGetValue(id, out var entry))
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
            if (entry.Protocol.Version != version)
            {
                _stats.DroppedInvalid++; // 协议废弃窗口：只保留当前版本（§11.6）
                return;
            }
            if (len > entry.Protocol.MaxSize)
            {
                _stats.DroppedInvalid++;
                return;
            }

            var payload = new PayloadBuffer(frame, FrameHeaderSize, len);
            object message;
            try
            {
                message = entry.Protocol.Decode(new PayloadReader(in payload));
            }
            catch (Exception)
            {
                _stats.DroppedInvalid++;
                return;
            }

            _stats.MessagesReceived++;
            entry.Handler?.Handle(in context, in message);
        }

        // ---- 统计（§11.13） ----

        public NetworkStats GetStats() => _stats;

        private void CountSend(ProtocolEntry entry, int bytes)
        {
            _stats.BytesSent += bytes;
            _stats.MessagesSent++;
            _stats.BytesByMod.TryGetValue(entry.Owner, out var m);
            _stats.BytesByMod[entry.Owner] = m + bytes;
            _stats.MessagesByProtocol.TryGetValue(entry.Id, out var p);
            _stats.MessagesByProtocol[entry.Id] = p + 1;
        }

        private static void WriteU16(byte[] d, int o, ushort v)
        {
            d[o] = (byte)(v & 0xFF);
            d[o + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v & 0xFF);
            d[o + 1] = (byte)((v >> 8) & 0xFF);
            d[o + 2] = (byte)((v >> 16) & 0xFF);
            d[o + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static ushort ReadU16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));

        private static uint ReadU32(byte[] d, int o) =>
            (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    }
}
