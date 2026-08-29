using System;

namespace Game.Mod.Contract
{
    /// <summary>
    /// 协议 ID：全局稳定哈希（§11.5，Rule 16）。
    /// 逻辑身份为 "{ModId}:{ProtocolPath}"，线上数据包只携带整数头。
    /// </summary>
    public readonly struct ProtocolId : IEquatable<ProtocolId>
    {
        public uint Value { get; }

        public ProtocolId(uint value) => Value = value;

        /// <summary>由逻辑 ID 计算稳定哈希：xxHash32("{ModId}:{ProtocolPath}")。</summary>
        public static ProtocolId Of(string logicalId)
        {
            var v = XxHash32.Compute(logicalId);
            return new ProtocolId(v == 0 ? 1u : v);
        }

        /// <summary>由命名空间与路径计算：xxHash32("{mod}:{path}")。</summary>
        public static ProtocolId Of(ModId mod, string path) => Of($"{mod.Value}:{path}");

        public bool Equals(ProtocolId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is ProtocolId p && Equals(p);
        public override int GetHashCode() => (int)Value;
        public override string ToString() => $"0x{Value:X8}";

        public static bool operator ==(ProtocolId a, ProtocolId b) => a.Equals(b);
        public static bool operator !=(ProtocolId a, ProtocolId b) => !a.Equals(b);
    }

    /// <summary>资源 ID："{ModId}:{AssetPath}"（§8.3）。资源不能直接通过 Unity 路径访问。</summary>
    public readonly struct AssetId : IEquatable<AssetId>
    {
        public NamespacedId Id { get; }
        public ModId Mod => Id.Mod;
        public string Path => Id.LocalId;

        public AssetId(NamespacedId id) => Id = id;
        public AssetId(ModId mod, string path) => Id = new NamespacedId(mod, path);

        public static AssetId Parse(string value) =>
            NamespacedId.TryParse(value, out var id) ? new AssetId(id)
            : throw new FormatException($"非法 AssetId: '{value}'");

        public bool Equals(AssetId other) => Id.Equals(other.Id);
        public override bool Equals(object? obj) => obj is AssetId a && Equals(a);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Id.ToString();

        public static bool operator ==(AssetId a, AssetId b) => a.Equals(b);
        public static bool operator !=(AssetId a, AssetId b) => !a.Equals(b);
    }

    /// <summary>消息 ID："{ModId}:{MessagePath}"（§13.2）。谁定义谁发布（Rule 11）。</summary>
    public readonly struct MessageId : IEquatable<MessageId>
    {
        public NamespacedId Id { get; }
        public ModId Mod => Id.Mod;
        public string Path => Id.LocalId;

        public MessageId(NamespacedId id) => Id = id;
        public MessageId(ModId mod, string path) => Id = new NamespacedId(mod, path);

        public static MessageId Parse(string value) =>
            NamespacedId.TryParse(value, out var id) ? new MessageId(id)
            : throw new FormatException($"非法 MessageId: '{value}'");

        public bool Equals(MessageId other) => Id.Equals(other.Id);
        public override bool Equals(object? obj) => obj is MessageId m && Equals(m);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Id.ToString();

        public static bool operator ==(MessageId a, MessageId b) => a.Equals(b);
        public static bool operator !=(MessageId a, MessageId b) => !a.Equals(b);
    }

    /// <summary>能力 ID："{ModId}:{CapabilityPath}"，Mod 间能力调用（ModCall）的寻址（§12.11）。</summary>
    public readonly struct CapabilityId : IEquatable<CapabilityId>
    {
        public NamespacedId Id { get; }
        public ModId Mod => Id.Mod;
        public string Path => Id.LocalId;

        public CapabilityId(NamespacedId id) => Id = id;
        public CapabilityId(ModId mod, string path) => Id = new NamespacedId(mod, path);

        public static CapabilityId Parse(string value) =>
            NamespacedId.TryParse(value, out var id) ? new CapabilityId(id)
            : throw new FormatException($"非法 CapabilityId: '{value}'");

        public bool Equals(CapabilityId other) => Id.Equals(other.Id);
        public override bool Equals(object? obj) => obj is CapabilityId c && Equals(c);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Id.ToString();

        public static bool operator ==(CapabilityId a, CapabilityId b) => a.Equals(b);
        public static bool operator !=(CapabilityId a, CapabilityId b) => !a.Equals(b);
    }

    /// <summary>服务 ID："{ModId}:{ServicePath}"，本端服务定位与框架 Mod 基础设施发布（§10.2/§12.11）。</summary>
    public readonly struct ServiceId : IEquatable<ServiceId>
    {
        public NamespacedId Id { get; }
        public ModId Mod => Id.Mod;
        public string Path => Id.LocalId;

        public ServiceId(NamespacedId id) => Id = id;
        public ServiceId(ModId mod, string path) => Id = new NamespacedId(mod, path);

        public static ServiceId Parse(string value) =>
            NamespacedId.TryParse(value, out var id) ? new ServiceId(id)
            : throw new FormatException($"非法 ServiceId: '{value}'");

        public bool Equals(ServiceId other) => Id.Equals(other.Id);
        public override bool Equals(object? obj) => obj is ServiceId s && Equals(s);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Id.ToString();

        public static bool operator ==(ServiceId a, ServiceId b) => a.Equals(b);
        public static bool operator !=(ServiceId a, ServiceId b) => !a.Equals(b);
    }

    /// <summary>窗口 ID："{ModId}:{WindowPath}"（§10.5）。窗口必须经 UI.Mod 注册和打开（Rule 9）。</summary>
    public readonly struct WindowId : IEquatable<WindowId>
    {
        public NamespacedId Id { get; }
        public ModId Mod => Id.Mod;
        public string Path => Id.LocalId;

        public WindowId(NamespacedId id) => Id = id;
        public WindowId(ModId mod, string path) => Id = new NamespacedId(mod, path);

        public static WindowId Parse(string value) =>
            NamespacedId.TryParse(value, out var id) ? new WindowId(id)
            : throw new FormatException($"非法 WindowId: '{value}'");

        public bool Equals(WindowId other) => Id.Equals(other.Id);
        public override bool Equals(object? obj) => obj is WindowId w && Equals(w);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Id.ToString();

        public static bool operator ==(WindowId a, WindowId b) => a.Equals(b);
        public static bool operator !=(WindowId a, WindowId b) => !a.Equals(b);
    }

    /// <summary>对象池 ID："{ModId}:{PoolPath}"（§9.1）。池属于 Mod，非泛型 Rent/Return（Rule 13）。</summary>
    public readonly struct PoolId : IEquatable<PoolId>
    {
        public NamespacedId Id { get; }
        public ModId Mod => Id.Mod;
        public string Path => Id.LocalId;

        public PoolId(NamespacedId id) => Id = id;
        public PoolId(ModId mod, string path) => Id = new NamespacedId(mod, path);

        public static PoolId Parse(string value) =>
            NamespacedId.TryParse(value, out var id) ? new PoolId(id)
            : throw new FormatException($"非法 PoolId: '{value}'");

        public bool Equals(PoolId other) => Id.Equals(other.Id);
        public override bool Equals(object? obj) => obj is PoolId p && Equals(p);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => Id.ToString();

        public static bool operator ==(PoolId a, PoolId b) => a.Equals(b);
        public static bool operator !=(PoolId a, PoolId b) => !a.Equals(b);
    }
}
