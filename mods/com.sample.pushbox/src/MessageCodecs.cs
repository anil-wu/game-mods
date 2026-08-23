using Com.Game.Protocol;

namespace Com.Sample.PushBox
{
    /// <summary>PushInputMsg 的协议解析器（编解码）。</summary>
    public sealed class PushInputCodec : IMessageCodec<PushInputMsg>
    {
        public byte[] Encode(PushInputMsg m) => new[] { (byte)m.Side, (byte)(m.Pushing ? 1 : 0) };
        public PushInputMsg Decode(byte[] d) => new PushInputMsg { Side = (PlayerSide)d[0], Pushing = d[1] != 0 };
    }

    /// <summary>GameWonMsg 的协议解析器（编解码）。</summary>
    public sealed class GameWonCodec : IMessageCodec<GameWonMsg>
    {
        public byte[] Encode(GameWonMsg m) => new[] { (byte)m.Winner };
        public GameWonMsg Decode(byte[] d) => new GameWonMsg { Winner = (PlayerSide)d[0] };
    }
}
