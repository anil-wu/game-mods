using System;

namespace Com.Game.Network
{
    /// <summary>
    /// Relay 线格式客户端侧独立实现（Rule 19：契约 = 文档，双方各自实现解析器，不共享程序集）。
    ///
    /// owner = relay_server（relay_server/src/RelayServer/Protocol/Envelope.cs 等）；
    /// Network.Mod 是消费方——按 CONTRACT.md（relay 契约）重复定义本文件，**不引用 relay_server 程序集**。
    /// 两端互通性由 RelayTests 在共享测试程序集里证明（两端源码都编进 TestRunner，同字节各自解析一致），
    /// 另由 RelayWireVectors 发布测试向量锁定线格式（§14.11.3 模式）。
    ///
    /// 报文（≤1400B payload）：[Magic u16 LE = 0x4D52 "MR"][Ver u8=1][Type u8][ConnId u16 LE] + payload
    /// Bind 载荷：roomId u64 LE + role u8 + expiry u64 ms LE + token 16B = 33B
    /// 类型：BindHost=1 BindClient=2 BindAck=3 BindFail=4 PeerJoined=5 PeerLeft=6
    ///       DataKcp=7 DataRaw=8 Ping=9 Pong=10 Leave=11
    ///
    /// 契约约定（§6.5 Session 模型）：
    ///   - ConnId 0 = Host（房主）；1 预留给 Host 本地客户端（§11.3 回环，不占 Relay 编号）；
    ///     Relay 为远端客户端分配 ConnId 从 2 起。
    ///   - 加密后 Relay 只见信封（§11.12）：DataRaw 的 payload 是 SecurityLayer 信封字节，
    ///     本层/relay 不解析、不参与路由，只看 SessionId（roomId）/ ConnectionId。
    /// </summary>
    public static class RelayWire
    {
        public const ushort Magic = 0x4D52;   // "MR"
        public const byte Version = 1;
        public const int HeaderSize = 6;      // magic u16 + ver u8 + type u8 + connId u16
        public const int TokenSize = 16;      // TokenCodec 截断 HMAC 长度
        public const int MaxPayload = 1400;
        public const int MaxDatagram = HeaderSize + MaxPayload;

        /// <summary>房主固定 ConnId（BindHost / 对 Host 的 Ping 等）。</summary>
        public const ushort HostConnId = 0;

        /// <summary>Host 本地客户端回环 ConnId（§11.3 硬规则；Relay 分配远端客户端从 2 起，避免冲突）。</summary>
        public const ushort LocalClientConnId = 1;

        /// <summary>Relay 分配远端客户端 ConnId 的起点（1 预留给 Host 本地客户端）。</summary>
        public const ushort FirstRemoteConnId = 2;

        public const byte RoleHost = 0;
        public const byte RoleClient = 1;

        public enum Type : byte
        {
            BindHost = 1,
            BindClient = 2,
            BindAck = 3,
            BindFail = 4,
            PeerJoined = 5,
            PeerLeft = 6,
            /// <summary>可靠流量（KCP 段，端到端）——原型期未启用，按 raw 语义转发。</summary>
            DataKcp = 7,
            /// <summary>不可靠流量（裸游戏报文/加密信封）。</summary>
            DataRaw = 8,
            Ping = 9,
            Pong = 10,
            /// <summary>退房（节点收到后移除 peer 并通知 Host PeerLeft，§6.4）。</summary>
            Leave = 11,
        }

        /// <summary>Relay 隧道报文（客户端侧独立实现，与 relay_server Envelope.Packet 同字节布局）。</summary>
        public readonly struct Packet
        {
            public readonly Type Type;
            public readonly ushort ConnId;
            public readonly byte[] Payload;

            public Packet(Type type, ushort connId, byte[] payload)
            {
                Type = type;
                ConnId = connId;
                Payload = payload ?? Array.Empty<byte>();
            }

            public byte[] Encode()
            {
                var buf = new byte[HeaderSize + Payload.Length];
                buf[0] = (byte)(Magic & 0xFF);
                buf[1] = (byte)(Magic >> 8);
                buf[2] = Version;
                buf[3] = (byte)Type;
                buf[4] = (byte)(ConnId & 0xFF);
                buf[5] = (byte)(ConnId >> 8);
                Buffer.BlockCopy(Payload, 0, buf, HeaderSize, Payload.Length);
                return buf;
            }

            public static bool TryDecode(byte[] data, out Packet packet)
            {
                packet = default;
                if (data is null || data.Length < HeaderSize) return false;
                if (data.Length > MaxDatagram) return false;
                var magic = (ushort)(data[0] | (data[1] << 8));
                if (magic != Magic) return false;
                if (data[2] != Version) return false;

                var payload = new byte[data.Length - HeaderSize];
                Buffer.BlockCopy(data, HeaderSize, payload, 0, payload.Length);
                packet = new Packet((Type)data[3], (ushort)(data[4] | (data[5] << 8)), payload);
                return true;
            }
        }

        /// <summary>编码 Bind 载荷：roomId u64 LE + role u8 + expiry u64 ms LE + token 16B = 33B。</summary>
        public static byte[] EncodeBindRequest(ulong roomId, byte role, long expiryMs, byte[] token)
        {
            if (token is null || token.Length != TokenSize)
                throw new ArgumentException($"token 必须为 {TokenSize} 字节（relay_server TokenCodec 签发）", nameof(token));
            var payload = new byte[33];
            WriteU64(payload, 0, roomId);
            payload[8] = role;
            WriteU64(payload, 9, (ulong)expiryMs);
            Buffer.BlockCopy(token, 0, payload, 17, TokenSize);
            return payload;
        }

        /// <summary>解析 Bind 载荷（客户端校验服务器返回/测试向量用）。</summary>
        public static bool TryParseBindRequest(byte[] payload, out ulong roomId, out byte role,
            out long expiryMs, out byte[] token)
        {
            roomId = 0;
            role = 0;
            expiryMs = 0;
            token = Array.Empty<byte>();
            if (payload is null || payload.Length < 33) return false;
            roomId = ReadU64(payload, 0);
            role = payload[8];
            expiryMs = (long)ReadU64(payload, 9);
            token = new byte[TokenSize];
            Buffer.BlockCopy(payload, 17, token, 0, TokenSize);
            return true;
        }

        /// <summary>Parse BindAck/Fail/PeerJoined/PeerLeft 的头部 connId（测试/诊断辅助）。</summary>
        public static ushort HeaderConnId(byte[] packetBytes)
        {
            if (packetBytes is null || packetBytes.Length < HeaderSize) return 0;
            return (ushort)(packetBytes[4] | (packetBytes[5] << 8));
        }

        // ---- LE 读写辅助（netstandard2.1 兼容，不用 BitConverter 依赖端序） ----

        private static void WriteU64(byte[] d, int o, ulong v)
        {
            for (var i = 0; i < 8; i++) d[o + i] = (byte)((v >> (8 * i)) & 0xFF);
        }

        private static ulong ReadU64(byte[] d, int o)
        {
            ulong v = 0;
            for (var i = 7; i >= 0; i--) v = (v << 8) | d[o + i];
            return v;
        }
    }
}
