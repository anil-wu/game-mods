using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;

namespace Game.Mod.Runtime
{
    /// <summary>复制原型 ID："{ModId}:{ArchetypePath}"（§11.10 Spawn 携带实体原型）。</summary>
    public readonly struct ArchetypeId : System.IEquatable<ArchetypeId>
    {
        public NamespacedId Id { get; }
        public ModId Mod => Id.Mod;
        public string Path => Id.LocalId;

        public ArchetypeId(NamespacedId id) => Id = id;
        public ArchetypeId(ModId mod, string path) => Id = new NamespacedId(mod, path);

        /// <summary>线上哈希（Spawn 帧中传输 u32）。</summary>
        public uint Hash => XxHash32.Compute(Id.ToString());

        public static ArchetypeId Parse(string value) =>
            NamespacedId.TryParse(value, out var id) ? new ArchetypeId(id)
            : throw new System.FormatException($"非法 ArchetypeId: '{value}'");

        public bool Equals(ArchetypeId other) => Id.Equals(other.Id);
        public override bool Equals(object? obj) => obj is ArchetypeId a && Equals(a);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Id.ToString();

        public static bool operator ==(ArchetypeId a, ArchetypeId b) => a.Equals(b);
        public static bool operator !=(ArchetypeId a, ArchetypeId b) => !a.Equals(b);
    }

    /// <summary>
    /// 组件复制编解码（§11.10：Mod 声明可同步 Component，Codec 无反射、字段直写）。
    /// Source Generator 的手写等价物（§15.3 务实裁剪）；与消息/协议共用线格式（Rule 14）。
    /// </summary>
    public interface IComponentCodec
    {
        /// <summary>组件类型（Mod 自己的 struct，装箱传递）。</summary>
        System.Type ComponentType { get; }

        /// <summary>把装箱组件写入载荷（字段编号直写）。</summary>
        void Write(object boxedComponent, INetworkWriter writer);

        /// <summary>从载荷读出装箱组件。</summary>
        object Read(INetworkReader reader);
    }

    /// <summary>
    /// ECS Replication 能力（§11.10）：高频 ECS 状态不走 object 协议消息，走独立复制管线。
    /// 路由到 Network.Mod 的 Replication 运行时。
    /// </summary>
    public interface IReplicationContext
    {
        /// <summary>注册复制原型（组件集 + Codec，owner = 当前 Mod；Server/Client 双端注册）。</summary>
        void RegisterArchetype(ArchetypeId id, IComponentCodec[] codecs);

        /// <summary>Server：把实体纳入复制（分配全局 NetworkId，广播 Spawn 携带初始组件集）。</summary>
        uint SpawnReplicated(Entity entity, ArchetypeId id);

        /// <summary>Server：停止复制并广播 Despawn（实体本身由 Mod 另行销毁）。</summary>
        void DespawnReplicated(Entity entity);

        /// <summary>Client：NetworkId → 本地实体映射查询（NetworkId 由 Server 分配、会话内稳定）。</summary>
        bool TryGetEntity(uint networkId, out Entity entity);

        /// <summary>Client：本地实体 → NetworkId。</summary>
        bool TryGetNetworkId(Entity entity, out uint networkId);
    }
}
