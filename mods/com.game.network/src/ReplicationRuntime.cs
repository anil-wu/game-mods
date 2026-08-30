using System;
using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// ECS Replication 运行时（§11.10）：Archetype 注册表 / NetworkId↔Entity 双向映射 /
    /// 全量 Snapshot@15Hz（原型简化：无 ChangedOnly 脏标记，模式位已预留）/ Despawn。
    ///
    /// 复制帧自描述（携带 ArchetypeHash + 全量组件）：客户端收到未知实体即创建——
    /// 天然解决"生成先于传输接入 / 中途加入"的乱序，无需独立 Spawn 协议。
    /// 组件块内部分帧：[typeIdx u8][len u16][bytes]*（Rule 14 同一线格式族）。
    /// </summary>
    public sealed class ReplicationRuntime
    {
        /// <summary>复制协议（S2C）：field1=NetworkId, field2=ArchetypeHash, field10=组件块。</summary>
        public const string ReplicateProtocolPath = "replicate";

        /// <summary>销毁协议（S2C）：field1=NetworkId。</summary>
        public const string DespawnProtocolPath = "despawn";

        public static readonly ProtocolId ReplicateProtocolId =
            ProtocolId.Of(NetworkMod.ModIdValue, ReplicateProtocolPath);
        public static readonly ProtocolId DespawnProtocolId =
            ProtocolId.Of(NetworkMod.ModIdValue, DespawnProtocolPath);

        /// <summary>快照频率（Hz）。</summary>
        public float SnapshotHz { get; set; } = 15f;

        private sealed class Archetype
        {
            public ArchetypeId Id = default;
            public ModId Owner;
            public IComponentCodec[] Codecs = Array.Empty<IComponentCodec>();
        }

        private sealed class TrackedEntity
        {
            public Entity Entity;
            public Archetype Archetype = null!;
        }

        private readonly World _world;
        private readonly NetworkRuntimeImpl _runtime;
        private readonly Dictionary<uint, Archetype> _archetypesByHash = new();
        private readonly Dictionary<uint, TrackedEntity> _serverTracked = new();   // NetworkId → 服务端实体
        private readonly Dictionary<uint, TrackedEntity> _clientReplicas = new();  // NetworkId → 客户端实体
        private readonly Dictionary<uint, uint> _serverEntityToNetId = new();      // 服务端 entityId → NetworkId
        private readonly Dictionary<uint, uint> _clientEntityToNetId = new();      // 客户端 entityId → NetworkId
        private uint _nextNetworkId = 1;
        private float _accumulator;

        public ReplicationRuntime(World world, NetworkRuntimeImpl runtime)
        {
            _world = world;
            _runtime = runtime;
        }

        // ---- 注册 ----

        public void RegisterArchetype(ModId owner, ArchetypeId id, IComponentCodec[] codecs)
        {
            if (codecs is null || codecs.Length == 0)
                throw new ArgumentException("Archetype 至少包含一个组件 Codec", nameof(codecs));
            var hash = id.Hash;
            if (_archetypesByHash.TryGetValue(hash, out var existing) && existing.Owner != owner)
                throw new NetworkException($"Archetype {id} 哈希冲突: '{owner}' 与 '{existing.Owner}'");
            _archetypesByHash[hash] = new Archetype { Id = id, Owner = owner, Codecs = codecs };
        }

        // ---- Server 侧 ----

        public uint SpawnReplicated(ModId owner, Entity entity, ArchetypeId id)
        {
            if (!_archetypesByHash.TryGetValue(id.Hash, out var archetype))
                throw new NetworkException($"Archetype '{id}' 未注册");
            if (archetype.Owner != owner)
                throw new NetworkException($"Mod '{owner}' 不能使用其他 Mod 的 Archetype '{id}'");

            var networkId = _nextNetworkId++;
            _serverTracked[networkId] = new TrackedEntity { Entity = entity, Archetype = archetype };
            _serverEntityToNetId[entity.Id] = networkId;
            return networkId;
        }

        public void DespawnReplicated(ModId owner, Entity entity)
        {
            if (!_serverEntityToNetId.TryGetValue(entity.Id, out var networkId)) return;
            var tracked = _serverTracked[networkId];
            if (tracked.Archetype.Owner != owner)
                throw new NetworkException($"Mod '{owner}' 不能 Despawn 其他 Mod 的复制实体");

            _serverTracked.Remove(networkId);
            _serverEntityToNetId.Remove(entity.Id);

            if (_runtime.IsActive)
            {
                var writer = new PayloadWriter();
                writer.WriteUInt32(1, networkId);
                _runtime.Broadcast(NetworkMod.ModIdValue, DespawnProtocolId,
                    new RawPayload(writer.ToBuffer()));
            }
        }

        /// <summary>快照泵（由 SnapshotSystem 每 tick 驱动）。</summary>
        public void TickSnapshot(float deltaTime)
        {
            if (!_runtime.IsActive || _serverTracked.Count == 0) return;
            _accumulator += deltaTime;
            var interval = 1f / Math.Max(1f, SnapshotHz);
            if (_accumulator < interval) return;
            _accumulator = 0;

            foreach (var (networkId, tracked) in _serverTracked)
            {
                var payload = BuildReplicatePayload(networkId, tracked);
                if (payload is not null)
                    _runtime.Broadcast(NetworkMod.ModIdValue, ReplicateProtocolId, new RawPayload(payload.Value));
            }
        }

        private PayloadBuffer? BuildReplicatePayload(uint networkId, TrackedEntity tracked)
        {
            // 组件块为裸切片流：[typeIdx u8][len u16][bytes]*（无字段分帧，客户端游标解析）
            var block = new List<byte>();
            for (var i = 0; i < tracked.Archetype.Codecs.Length; i++)
            {
                var codec = tracked.Archetype.Codecs[i];
                if (!_world.TryGetRaw(tracked.Entity, codec.ComponentType, out var boxed)) continue;
                if (i > byte.MaxValue) throw new NetworkException("Archetype 组件数超 256");
                var cw = new PayloadWriter();
                codec.Write(boxed, cw);
                block.AddRange(BuildComponentSlice((byte)i, cw.ToBuffer().ToArray()));
            }
            if (block.Count == 0) return null;

            var writer = new PayloadWriter();
            writer.WriteUInt32(1, networkId);
            writer.WriteUInt32(2, tracked.Archetype.Id.Hash);
            writer.WriteBytes(10, block.ToArray());
            return writer.ToBuffer();
        }

        private static byte[] BuildComponentSlice(byte typeIdx, byte[] bytes)
        {
            var slice = new byte[3 + bytes.Length];
            slice[0] = typeIdx;
            slice[1] = (byte)(bytes.Length & 0xFF);
            slice[2] = (byte)((bytes.Length >> 8) & 0xFF);
            Array.Copy(bytes, 0, slice, 3, bytes.Length);
            return slice;
        }

        // ---- Client 侧 ----

        public bool TryGetEntity(uint networkId, out Entity entity)
        {
            if (_clientReplicas.TryGetValue(networkId, out var tracked))
            {
                entity = tracked.Entity;
                return true;
            }
            if (_serverTracked.TryGetValue(networkId, out var serverTracked))
            {
                entity = serverTracked.Entity; // Host：本地实体也是权威实体
                return true;
            }
            entity = default;
            return false;
        }

        public bool TryGetNetworkId(Entity entity, out uint networkId)
        {
            if (_clientEntityToNetId.TryGetValue(entity.Id, out networkId)) return true;
            return _serverEntityToNetId.TryGetValue(entity.Id, out networkId);
        }

        /// <summary>Client 收到复制帧（由 NetworkMod 的协议 Handler 调用）。</summary>
        public void OnReplicateFrame(INetworkReader reader)
        {
            if (!reader.TryReadUInt32(1, out var networkId)) return;
            if (!reader.TryReadUInt32(2, out var archetypeHash)) return;
            if (!_archetypesByHash.TryGetValue(archetypeHash, out var archetype)) return;
            if (!reader.TryReadBytes(10, out var block)) return;

            if (!_clientReplicas.TryGetValue(networkId, out var tracked))
            {
                // 帧自描述：未知实体即创建（乱序 / 中途加入免疫）
                tracked = new TrackedEntity { Entity = _world.CreateEntity(), Archetype = archetype };
                _world.Add(tracked.Entity, new ReplicaTag()); // Host 单 World 双端区分
                _clientReplicas[networkId] = tracked;
                _clientEntityToNetId[tracked.Entity.Id] = networkId;
            }

            // 解析组件块并应用（SetRaw：客户端只读应用，权威在 Server）
            var cursor = 0;
            while (cursor + 3 <= block.Length)
            {
                var typeIdx = block[cursor];
                var len = block[cursor + 1] | (block[cursor + 2] << 8);
                cursor += 3;
                if (typeIdx >= archetype.Codecs.Length || cursor + len > block.Length) break;
                var codec = archetype.Codecs[typeIdx];
                var slice = new byte[len];
                Array.Copy(block, cursor, slice, 0, len);
                var boxed = codec.Read(new PayloadReader(slice));
                _world.SetRaw(tracked.Entity, codec.ComponentType, boxed);
                cursor += len;
            }
        }

        /// <summary>Client 收到销毁帧。</summary>
        public void OnDespawnFrame(INetworkReader reader)
        {
            if (!reader.TryReadUInt32(1, out var networkId)) return;
            if (!_clientReplicas.TryGetValue(networkId, out var tracked)) return;
            _world.Destroy(tracked.Entity);
            _clientReplicas.Remove(networkId);
            _clientEntityToNetId.Remove(tracked.Entity.Id);
        }

        // ---- 生命周期（Mod 卸载清理） ----

        /// <summary>按 owner 清理：Archetype 注册、服务端跟踪、客户端副本实体。</summary>
        public void UnregisterAll(ModId owner)
        {
            var staleArchetypes = new List<uint>();
            foreach (var (hash, archetype) in _archetypesByHash)
                if (archetype.Owner == owner) staleArchetypes.Add(hash);
            foreach (var hash in staleArchetypes) _archetypesByHash.Remove(hash);

            var staleServer = new List<uint>();
            foreach (var (netId, tracked) in _serverTracked)
                if (tracked.Archetype.Owner == owner) staleServer.Add(netId);
            foreach (var netId in staleServer)
            {
                _serverEntityToNetId.Remove(_serverTracked[netId].Entity.Id);
                _serverTracked.Remove(netId);
            }

            var staleClient = new List<uint>();
            foreach (var (netId, tracked) in _clientReplicas)
                if (tracked.Archetype.Owner == owner) staleClient.Add(netId);
            foreach (var netId in staleClient)
            {
                _world.Destroy(_clientReplicas[netId].Entity); // 客户端副本实体一并销毁
                _clientEntityToNetId.Remove(_clientReplicas[netId].Entity.Id);
                _clientReplicas.Remove(netId);
            }
        }

        /// <summary>Server 端实体被外部销毁时解除跟踪（防快照读尸体）。</summary>
        public void UntrackIfMissing()
        {
            var stale = new List<uint>();
            foreach (var (netId, tracked) in _serverTracked)
                if (!_world.Exists(tracked.Entity)) stale.Add(netId);
            foreach (var netId in stale)
            {
                _serverEntityToNetId.Remove(_serverTracked[netId].Entity.Id);
                _serverTracked.Remove(netId);
                if (_runtime.IsActive)
                {
                    var writer = new PayloadWriter();
                    writer.WriteUInt32(1, netId);
                    _runtime.Broadcast(NetworkMod.ModIdValue, DespawnProtocolId,
                        new RawPayload(writer.ToBuffer()));
                }
            }
        }
    }

    /// <summary>原始载荷消息（复制运行时已编码完成，协议只透传字节）。</summary>
    public readonly struct RawPayload
    {
        public readonly PayloadBuffer Buffer;
        public RawPayload(PayloadBuffer buffer) => Buffer = buffer;
    }

    /// <summary>复制/销毁协议（Network.Mod 自有基础设施协议，S2C）。</summary>
    public sealed class ReplicationProtocol : INetworkProtocol
    {
        private readonly string _path;
        public ReplicationProtocol(string path) => _path = path;
        public ProtocolId Id => ProtocolId.Of(NetworkMod.ModIdValue, _path);
        public ushort Version => 1;
        public NetworkDirection Direction => NetworkDirection.ServerToClient;
        public int MaxSize => 4096;

        public void Encode(in object message, INetworkWriter writer)
        {
            var raw = (RawPayload)message;
            writer.WriteBytes(1, raw.Buffer.ToArray());
        }

        public object Decode(INetworkReader reader)
        {
            reader.TryReadBytes(1, out var bytes);
            return new RawPayload(new PayloadBuffer(bytes));
        }
    }

    /// <summary>复制帧 Handler：解包外层后交给 ReplicationRuntime 按 NetworkId 路由。</summary>
    public sealed class ReplicationFrameHandler : INetworkHandler
    {
        private readonly ReplicationRuntime _replication;
        private readonly bool _despawn;

        public ReplicationFrameHandler(ReplicationRuntime replication, bool despawn)
        {
            _replication = replication;
            _despawn = despawn;
        }

        public void Handle(in NetworkContext context, in object message)
        {
            if (context.IsServer) return; // 复制帧只流向 Client
            var raw = (RawPayload)message;
            var reader = new PayloadReader(raw.Buffer);
            if (_despawn) _replication.OnDespawnFrame(reader);
            else _replication.OnReplicateFrame(reader);
        }
    }

    /// <summary>快照泵系统（ECS 驱动相位，owner = Network.Mod）。</summary>
    public sealed class SnapshotSystem : ISystem
    {
        private readonly ReplicationRuntime _replication;

        public SnapshotSystem(ReplicationRuntime replication) => _replication = replication;

        public void Update(SystemContext context)
        {
            _replication.UntrackIfMissing();
            _replication.TickSnapshot(context.DeltaTime);
        }
    }
}
