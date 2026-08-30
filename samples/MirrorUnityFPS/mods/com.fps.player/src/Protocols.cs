using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Fps.Player
{
    // ---- 复制 Archetype 组件 Codec（CONTRACT.md §3；生成代码的手写等价物） ----

    public sealed class Position3Codec : IComponentCodec
    {
        public System.Type ComponentType => typeof(Position3);
        public void Write(object boxed, INetworkWriter w)
        {
            var p = (Position3)boxed;
            w.WriteFloat(1, p.X);
            w.WriteFloat(2, p.Y);
            w.WriteFloat(3, p.Z);
        }
        public object Read(INetworkReader r)
        {
            r.TryReadFloat(1, out var x);
            r.TryReadFloat(2, out var y);
            r.TryReadFloat(3, out var z);
            return new Position3 { X = x, Y = y, Z = z };
        }
    }

    public sealed class HealthCodec : IComponentCodec
    {
        public System.Type ComponentType => typeof(Health);
        public void Write(object boxed, INetworkWriter w)
        {
            var h = (Health)boxed;
            w.WriteInt32(1, h.Current);
            w.WriteInt32(2, h.Max);
        }
        public object Read(INetworkReader r)
        {
            r.TryReadInt32(1, out var c);
            r.TryReadInt32(2, out var m);
            return new Health { Current = c, Max = m };
        }
    }

    public sealed class PlayerTagCodec : IComponentCodec
    {
        public System.Type ComponentType => typeof(PlayerTag);
        public void Write(object boxed, INetworkWriter w) => w.WriteBool(1, ((PlayerTag)boxed).IsDummy);
        public object Read(INetworkReader r)
        {
            r.TryReadBool(1, out var d);
            return new PlayerTag { IsDummy = d };
        }
    }

    public sealed class FacingCodec : IComponentCodec
    {
        public System.Type ComponentType => typeof(Facing);
        public void Write(object boxed, INetworkWriter w) => w.WriteFloat(1, ((Facing)boxed).Yaw);
        public object Read(INetworkReader r)
        {
            r.TryReadFloat(1, out var yaw);
            return new Facing { Yaw = yaw };
        }
    }

    // ---- 协议载荷 DTO ----

    /// <summary>输入上报（C2S）。线格式见 CONTRACT.md。</summary>
    public readonly struct InputState
    {
        public readonly float MoveX, MoveZ, Yaw;
        public readonly bool Jump;
        public InputState(float moveX, float moveZ, float yaw, bool jump)
        {
            MoveX = moveX; MoveZ = moveZ; Yaw = yaw; Jump = jump;
        }
    }

    /// <summary>本地玩家指派（S2C）：告诉客户端"你的玩家 NetworkId"。</summary>
    public readonly struct LocalAssigned
    {
        public readonly uint NetworkId;
        public LocalAssigned(uint networkId) => NetworkId = networkId;
    }

    // ---- 协议（稳定哈希 ID，Rule 16） ----

    public abstract class PlayerProtocolBase : INetworkProtocol
    {
        public abstract string Path { get; }
        public ProtocolId Id => ProtocolId.Of(PlayerMod.ModIdValue, Path);
        public ushort Version => 1;
        public abstract NetworkDirection Direction { get; }
        public int MaxSize => 128;
        public abstract void Encode(in object message, INetworkWriter writer);
        public abstract object Decode(INetworkReader reader);
    }

    /// <summary>player:input（C2S）：moveX/moveZ/yaw/jump。</summary>
    public sealed class InputProtocol : PlayerProtocolBase
    {
        public override string Path => "input";
        public override NetworkDirection Direction => NetworkDirection.ClientToServer;

        public override void Encode(in object message, INetworkWriter writer)
        {
            var m = (InputState)message;
            writer.WriteFloat(1, m.MoveX);
            writer.WriteFloat(2, m.MoveZ);
            writer.WriteFloat(3, m.Yaw);
            writer.WriteBool(4, m.Jump);
        }

        public override object Decode(INetworkReader reader)
        {
            reader.TryReadFloat(1, out var mx);
            reader.TryReadFloat(2, out var mz);
            reader.TryReadFloat(3, out var yaw);
            reader.TryReadBool(4, out var jump);
            return new InputState(mx, mz, yaw, jump);
        }
    }

    /// <summary>player:local（S2C）：networkId。</summary>
    public sealed class LocalProtocol : PlayerProtocolBase
    {
        public override string Path => "local";
        public override NetworkDirection Direction => NetworkDirection.ServerToClient;

        public override void Encode(in object message, INetworkWriter writer)
            => writer.WriteUInt32(1, ((LocalAssigned)message).NetworkId);

        public override object Decode(INetworkReader reader)
        {
            reader.TryReadUInt32(1, out var id);
            return new LocalAssigned(id);
        }
    }
}
