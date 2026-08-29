using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Sample.PushBox
{
    // ---- 协议载荷 DTO（纯数据，可二进制序列化，§14.4） ----

    /// <summary>玩家输入：推 / 不推（ClientToServer）。线格式见 CONTRACT.md。</summary>
    public readonly struct PushInput
    {
        public readonly PlayerSide Side;
        public readonly bool Pushing;

        public PushInput(PlayerSide side, bool pushing)
        {
            Side = side;
            Pushing = pushing;
        }
    }

    /// <summary>胜负事件（ServerToClient 广播）。</summary>
    public readonly struct GameWon
    {
        public readonly PlayerSide Winner;

        public GameWon(PlayerSide winner) => Winner = winner;
    }

    /// <summary>
    /// 协议基类：全局 ID = xxHash32("{ModId}:{ProtocolPath}")（Rule 16），两端各自计算天然一致。
    /// 编解码为手写字段编号读写（Source Generator 的等价生成物形态，§15.3 务实裁剪）。
    /// </summary>
    public abstract class PushBoxProtocolBase : INetworkProtocol
    {
        public abstract string Path { get; }
        public ProtocolId Id => ProtocolId.Of(PushBoxMod.ModIdValue, Path);
        public ushort Version => 1;
        public abstract NetworkDirection Direction { get; }
        public int MaxSize => 64;

        public abstract void Encode(in object message, INetworkWriter writer);
        public abstract object Decode(INetworkReader reader);
    }

    /// <summary>pushbox:input（ClientToServer）：field 1 = side (byte)，field 2 = pushing (bool)。</summary>
    public sealed class PushInputProtocol : PushBoxProtocolBase
    {
        public override string Path => "input";
        public override NetworkDirection Direction => NetworkDirection.ClientToServer;

        public override void Encode(in object message, INetworkWriter writer)
        {
            var m = (PushInput)message;
            writer.WriteByte(1, (byte)m.Side);
            writer.WriteBool(2, m.Pushing);
        }

        public override object Decode(INetworkReader reader)
        {
            reader.TryReadByte(1, out var side);
            reader.TryReadBool(2, out var pushing);
            return new PushInput((PlayerSide)side, pushing);
        }
    }

    /// <summary>pushbox:game_won（ServerToClient）：field 1 = winner (byte)。</summary>
    public sealed class GameWonProtocol : PushBoxProtocolBase
    {
        public override string Path => "game_won";
        public override NetworkDirection Direction => NetworkDirection.ServerToClient;

        public override void Encode(in object message, INetworkWriter writer)
        {
            var m = (GameWon)message;
            writer.WriteByte(1, (byte)m.Winner);
        }

        public override object Decode(INetworkReader reader)
        {
            reader.TryReadByte(1, out var winner);
            return new GameWon((PlayerSide)winner);
        }
    }
}
