using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Fps.Weapon
{
    // ---- 协议载荷 DTO（纯数据，线格式见 CONTRACT.md） ----

    public readonly struct FireRequest
    {
        public readonly float Yaw;
        public FireRequest(float yaw) => Yaw = yaw;
    }

    public readonly struct ReloadRequest
    {
        public static readonly ReloadRequest Instance = new();
    }

    /// <summary>开火事件（S2C）：弹道表现 + 命中反馈。</summary>
    public readonly struct ShotEvent
    {
        public readonly uint ShooterNetId;
        public readonly uint HitNetId;      // 0 = 未命中
        public readonly float HitX, HitY, HitZ; // 命中点（未命中 = 弹道终点）
        public readonly bool Killed;

        public ShotEvent(uint shooterNetId, uint hitNetId, float hitX, float hitY, float hitZ, bool killed)
        {
            ShooterNetId = shooterNetId; HitNetId = hitNetId;
            HitX = hitX; HitY = hitY; HitZ = hitZ; Killed = killed;
        }
    }

    /// <summary>武器状态同步（S2C）：弹药 HUD。</summary>
    public readonly struct WeaponStateSync
    {
        public readonly uint NetId;
        public readonly int Ammo;
        public readonly int MaxAmmo;
        public readonly bool Reloading;

        public WeaponStateSync(uint netId, int ammo, int maxAmmo, bool reloading)
        {
            NetId = netId; Ammo = ammo; MaxAmmo = maxAmmo; Reloading = reloading;
        }
    }

    // ---- 协议（稳定哈希 ID，Rule 16） ----

    public abstract class WeaponProtocolBase : INetworkProtocol
    {
        public abstract string Path { get; }
        public ProtocolId Id => ProtocolId.Of(WeaponMod.ModIdValue, Path);
        public ushort Version => 1;
        public abstract NetworkDirection Direction { get; }
        public virtual int MaxSize => 128;
        public abstract void Encode(in object message, INetworkWriter writer);
        public abstract object Decode(INetworkReader reader);
    }

    public sealed class FireProtocol : WeaponProtocolBase
    {
        public override string Path => "fire";
        public override NetworkDirection Direction => NetworkDirection.ClientToServer;
        public override void Encode(in object message, INetworkWriter writer)
            => writer.WriteFloat(1, ((FireRequest)message).Yaw);
        public override object Decode(INetworkReader reader)
        {
            reader.TryReadFloat(1, out var yaw);
            return new FireRequest(yaw);
        }
    }

    public sealed class ReloadProtocol : WeaponProtocolBase
    {
        public override string Path => "reload";
        public override NetworkDirection Direction => NetworkDirection.ClientToServer;
        public override void Encode(in object message, INetworkWriter writer) { /* 空载荷 */ }
        public override object Decode(INetworkReader reader) => ReloadRequest.Instance;
    }

    public sealed class ShotProtocol : WeaponProtocolBase
    {
        public override string Path => "shot";
        public override NetworkDirection Direction => NetworkDirection.ServerToClient;
        public override void Encode(in object message, INetworkWriter writer)
        {
            var m = (ShotEvent)message;
            writer.WriteUInt32(1, m.ShooterNetId);
            writer.WriteUInt32(2, m.HitNetId);
            writer.WriteFloat(3, m.HitX);
            writer.WriteFloat(4, m.HitY);
            writer.WriteFloat(5, m.HitZ);
            writer.WriteBool(6, m.Killed);
        }
        public override object Decode(INetworkReader reader)
        {
            reader.TryReadUInt32(1, out var shooter);
            reader.TryReadUInt32(2, out var hit);
            reader.TryReadFloat(3, out var x);
            reader.TryReadFloat(4, out var y);
            reader.TryReadFloat(5, out var z);
            reader.TryReadBool(6, out var killed);
            return new ShotEvent(shooter, hit, x, y, z, killed);
        }
    }

    public sealed class StateProtocol : WeaponProtocolBase
    {
        public override string Path => "state";
        public override NetworkDirection Direction => NetworkDirection.ServerToClient;
        public override void Encode(in object message, INetworkWriter writer)
        {
            var m = (WeaponStateSync)message;
            writer.WriteUInt32(1, m.NetId);
            writer.WriteInt32(2, m.Ammo);
            writer.WriteInt32(3, m.MaxAmmo);
            writer.WriteBool(4, m.Reloading);
        }
        public override object Decode(INetworkReader reader)
        {
            reader.TryReadUInt32(1, out var netId);
            reader.TryReadInt32(2, out var ammo);
            reader.TryReadInt32(3, out var max);
            reader.TryReadBool(4, out var reloading);
            return new WeaponStateSync(netId, ammo, max, reloading);
        }
    }
}
