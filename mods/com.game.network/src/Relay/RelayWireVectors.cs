using Game.Mod.Contract.Wire;

namespace Com.Game.Network
{
    /// <summary>
    /// Relay 线格式测试向量（§14.11.3 模式）：Bind/DataRaw 报文的"样例字段 + 规范编码字节"。
    /// owner = relay_server；本 Mod 是消费方——向量随本 Mod 发布，供未来跨端/跨版本回归锁定线格式：
    /// 消费方用自己的解析器（RelayWire）解析 <see cref="TestVector.Bytes"/>，应解出与
    /// <see cref="TestVector.Sample"/> 一致的字段；两端对同一字节解出一致结果，即"可重复定义"（Rule 19）未漂移。
    /// </summary>
    public static class RelayWireVectors
    {
        public const string BindHostContract = "relay:bind-host";
        public const string BindClientContract = "relay:bind-client";
        public const string DataRawContract = "relay:data-raw";
        public const string PeerLeftContract = "relay:peer-left";

        /// <summary>样例 token（16B，HMAC 截断长度）。</summary>
        private static readonly byte[] TokenSample =
        {
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
            0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
        };

        /// <summary>全部 Relay 向量。</summary>
        public static TestVector[] All() => new[] { BindHost(), BindClient(), DataRaw(), PeerLeft() };

        /// <summary>BindHost 向量：type=1 connId=0，载荷 = roomId(42) + role(0) + expiryMs + token。</summary>
        public static TestVector BindHost()
        {
            const long expiryMs = 1700000000000L;
            var payload = RelayWire.EncodeBindRequest(42, RelayWire.RoleHost, expiryMs, TokenSample);
            var bytes = new RelayWire.Packet(RelayWire.Type.BindHost, RelayWire.HostConnId, payload).Encode();
            return new TestVector(BindHostContract,
                new object?[] { "BindHost", (ushort)0, 42UL, (byte)RelayWire.RoleHost, expiryMs, "00112233-4455-6677-8899-aabbccddeeff" },
                bytes);
        }

        /// <summary>BindClient 向量：type=2 connId=0，载荷 role=1。</summary>
        public static TestVector BindClient()
        {
            const long expiryMs = 1700000000000L;
            var payload = RelayWire.EncodeBindRequest(42, RelayWire.RoleClient, expiryMs, TokenSample);
            var bytes = new RelayWire.Packet(RelayWire.Type.BindClient, RelayWire.HostConnId, payload).Encode();
            return new TestVector(BindClientContract,
                new object?[] { "BindClient", (ushort)0, 42UL, (byte)RelayWire.RoleClient, expiryMs, "00112233-4455-6677-8899-aabbccddeeff" },
                bytes);
        }

        /// <summary>DataRaw 向量：type=8 connId=7（远端客户端分配 id），payload = 4 字节样例信封。</summary>
        public static TestVector DataRaw()
        {
            var payload = new byte[] { 0x4E, 0x54, 0x00, 0x2A };
            var bytes = new RelayWire.Packet(RelayWire.Type.DataRaw, 7, payload).Encode();
            return new TestVector(DataRawContract,
                new object?[] { "DataRaw", (ushort)7, "4E 54 00 2A" },
                bytes);
        }

        /// <summary>PeerLeft 向量：type=6 connId=7（离开的客户端 id），payload = connId u16 LE。</summary>
        public static TestVector PeerLeft()
        {
            var payload = new byte[] { 0x07, 0x00 };
            var bytes = new RelayWire.Packet(RelayWire.Type.PeerLeft, 7, payload).Encode();
            return new TestVector(PeerLeftContract,
                new object?[] { "PeerLeft", (ushort)7 },
                bytes);
        }
    }
}
