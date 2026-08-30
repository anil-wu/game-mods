using System;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;

namespace Com.Game.Network
{
    /// <summary>
    /// 握手协议测试向量（§14.11.3 模式）：hello / result 的"样例字段 + 规范编码字节"。
    /// 网络 Mod 是框架基础设施，向量主要价值在回归锁定线格式——消费方（含未来跨端实现）
    /// 用自己重复定义的解析器解析 <see cref="TestVector.Bytes"/>，应解出与 <see cref="TestVector.Sample"/>
    /// 一致的字段；两端对同一字节解出一致结果，即"可重复定义"（Rule 19）未漂移。
    /// </summary>
    public static class HandshakeVectors
    {
        public const string HelloContract = "handshake:hello";
        public const string ResultContract = "handshake:result";

        // 样例（与 CONTRACT.md / §2.1 字段布局一致，勿改——改了就是线格式破坏性变更）
        private static readonly ProtocolVersionEntry[] HelloSampleEntries =
        {
            new() { Id = ProtocolId.Of("com.game.alpha:move"), Version = 3, ModId = "com.game.alpha", Path = "move" },
            new() { Id = ProtocolId.Of("com.game.beta:state"), Version = 1, ModId = "com.game.beta", Path = "state" },
        };

        private static readonly ProtocolVersionEntry[] AcceptedSample =
        {
            new() { Id = ProtocolId.Of("com.game.alpha:move"), Version = 3 },
            new() { Id = ProtocolId.Of("com.game.beta:state"), Version = 1 },
        };

        private static readonly ProtocolId[] DisabledSample =
        {
            ProtocolId.Of("com.game.opt:skin"),
        };

        /// <summary>全部握手向量。</summary>
        public static TestVector[] All() => new[] { Hello(), Result() };

        /// <summary>hello 向量：field 1 = entries 字节（ProtocolId u32 LE + Version u16 LE + ModIdLen u8 + ModId + PathLen u8 + Path）。</summary>
        public static TestVector Hello()
        {
            var payload = new PayloadWriter();
            payload.WriteBytes(1, HandshakeWire.EncodeEntries(HelloSampleEntries));
            return new TestVector(HelloContract,
                new object?[] { 2, "com.game.alpha:move v3", "com.game.beta:state v1" },
                payload.ToBuffer().ToArray());
        }

        /// <summary>result 向量：field1 accept(u8) + field2 reason(u32) + field3 accepted[] + field4 disabled[] + field5 missing(string)。</summary>
        public static TestVector Result()
        {
            var payload = new PayloadWriter();
            payload.WriteByte(1, 1);
            payload.WriteUInt32(2, 0);
            payload.WriteBytes(3, HandshakeWire.EncodeAccepted(AcceptedSample));
            payload.WriteBytes(4, HandshakeWire.EncodeDisabled(DisabledSample));
            payload.WriteString(5, "");
            return new TestVector(ResultContract,
                new object?[] { true, 0u, 2, 1, "" },
                payload.ToBuffer().ToArray());
        }

        /// <summary>用 PayloadReader 从向量字节解析 hello 条目（消费方解析器示例，字段级容错）。</summary>
        public static ProtocolVersionEntry[] ParseHelloEntries(byte[] bytes)
        {
            var reader = new PayloadReader(bytes);
            reader.TryReadBytes(1, out var data);
            return HandshakeWire.DecodeEntries(data ?? Array.Empty<byte>());
        }

        /// <summary>用 PayloadReader 从向量字节解析 result（消费方解析器示例）。</summary>
        public static HandshakeResult ParseResult(byte[] bytes)
        {
            var reader = new PayloadReader(bytes);
            reader.TryReadByte(1, out var accept);
            reader.TryReadUInt32(2, out var reason);
            reader.TryReadBytes(3, out var accBytes);
            reader.TryReadBytes(4, out var disBytes);
            reader.TryReadString(5, out var missing);
            return new HandshakeResult
            {
                Accept = accept != 0,
                Reason = reason,
                Accepted = HandshakeWire.DecodeAccepted(accBytes ?? Array.Empty<byte>()),
                Disabled = HandshakeWire.DecodeDisabled(disBytes ?? Array.Empty<byte>()),
                MissingMods = missing,
            };
        }
    }
}
