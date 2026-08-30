using System;
using System.IO;
using System.Text;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>客户端声明的协议版本条目（hello 载荷元素）。</summary>
    public sealed class ProtocolVersionEntry
    {
        public ProtocolId Id;
        public ushort Version;
        public string ModId = "";
        public string Path = "";
    }

    /// <summary>握手 hello 消息（C2S，§11.6）：客户端协议表。</summary>
    public sealed class HandshakeHello
    {
        public ProtocolVersionEntry[] Entries = Array.Empty<ProtocolVersionEntry>();
    }

    /// <summary>握手 result 消息（S2C，§11.6）：接受 / 拒绝 + 协商结果。</summary>
    public sealed class HandshakeResult
    {
        public bool Accept;

        /// <summary>0=ok / 1=core_no_intersection / 2=missing_required_mod / 3=other。</summary>
        public uint Reason;

        /// <summary>协商后版本表（ProtocolId + Version）。</summary>
        public ProtocolVersionEntry[] Accepted = Array.Empty<ProtocolVersionEntry>();

        /// <summary>降级禁用协议（版本无交集的表现层协议）。</summary>
        public ProtocolId[] Disabled = Array.Empty<ProtocolId>();

        /// <summary>缺失必需 Mod 清单（ModId 逗号分隔；Accept=false 时携带）。</summary>
        public string MissingMods = "";
    }

    /// <summary>
    /// 握手协议（Network.Mod 自有基础设施协议，§11.6）。
    /// 两条协议（hello C2S / result S2C）而非一条双向协议：复用现有 Direction 单值 +
    /// Dispatch 方向校验机制，不用为握手开洞。路径遵循 Rule 16（ProtocolId.Of(ModId, path)）。
    /// 载荷为字段编号线格式（Rule 14 同族）：可跳读、append-only 演化。
    /// </summary>
    internal static class HandshakeWire
    {
        /// <summary>hello 载荷：field 1 (bytes) = entries[]。</summary>
        public static byte[] EncodeEntries(ProtocolVersionEntry[] entries)
        {
            // 顺序写：ProtocolId u32 LE + Version u16 LE + ModIdLen u8 + ModId + PathLen u8 + Path
            using var ms = new MemoryStream();
            foreach (var e in entries)
            {
                WriteU32LE(ms, e.Id.Value);
                WriteU16LE(ms, e.Version);
                var mod = Encoding.UTF8.GetBytes(e.ModId ?? "");
                if (mod.Length > byte.MaxValue) throw new NetworkException("hello 条目 ModId 超 255B");
                ms.WriteByte((byte)mod.Length);
                ms.Write(mod, 0, mod.Length);
                var path = Encoding.UTF8.GetBytes(e.Path ?? "");
                if (path.Length > byte.MaxValue) throw new NetworkException("hello 条目 Path 超 255B");
                ms.WriteByte((byte)path.Length);
                ms.Write(path, 0, path.Length);
            }
            return ms.ToArray();
        }

        /// <summary>解析 hello field 1 的 entries 字节（客户端解析器，字段级容错）。</summary>
        public static ProtocolVersionEntry[] DecodeEntries(byte[] data)
        {
            var entries = new System.Collections.Generic.List<ProtocolVersionEntry>();
            var cursor = 0;
            while (cursor + 6 <= data.Length)
            {
                var id = ReadU32LE(data, ref cursor);
                var version = ReadU16LE(data, ref cursor);
                var modLen = data[cursor++];
                if (cursor + modLen > data.Length) break;
                var modId = Encoding.UTF8.GetString(data, cursor, modLen);
                cursor += modLen;
                if (cursor >= data.Length) break;
                var pathLen = data[cursor++];
                if (cursor + pathLen > data.Length) break;
                var path = Encoding.UTF8.GetString(data, cursor, pathLen);
                cursor += pathLen;
                entries.Add(new ProtocolVersionEntry { Id = new ProtocolId(id), Version = version, ModId = modId, Path = path });
            }
            return entries.ToArray();
        }

        /// <summary>accepted[] 编码：每项 = ProtocolId u32 LE + Version u16 LE。</summary>
        public static byte[] EncodeAccepted(ProtocolVersionEntry[] accepted)
        {
            using var ms = new MemoryStream();
            foreach (var e in accepted)
            {
                WriteU32LE(ms, e.Id.Value);
                WriteU16LE(ms, e.Version);
            }
            return ms.ToArray();
        }

        public static ProtocolVersionEntry[] DecodeAccepted(byte[] data)
        {
            var entries = new System.Collections.Generic.List<ProtocolVersionEntry>();
            var cursor = 0;
            while (cursor + 6 <= data.Length)
            {
                var id = ReadU32LE(data, ref cursor);
                var version = ReadU16LE(data, ref cursor);
                entries.Add(new ProtocolVersionEntry { Id = new ProtocolId(id), Version = version });
            }
            return entries.ToArray();
        }

        /// <summary>disabled[] 编码：每项 = ProtocolId u32 LE。</summary>
        public static byte[] EncodeDisabled(ProtocolId[] disabled)
        {
            using var ms = new MemoryStream();
            foreach (var id in disabled) WriteU32LE(ms, id.Value);
            return ms.ToArray();
        }

        public static ProtocolId[] DecodeDisabled(byte[] data)
        {
            var list = new System.Collections.Generic.List<ProtocolId>();
            var cursor = 0;
            while (cursor + 4 <= data.Length)
            {
                list.Add(new ProtocolId(ReadU32LE(data, ref cursor)));
            }
            return list.ToArray();
        }

        private static void WriteU32LE(Stream s, uint v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 24) & 0xFF));
        }

        private static void WriteU16LE(Stream s, ushort v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
        }

        private static uint ReadU32LE(byte[] d, ref int cursor)
        {
            var v = (uint)(d[cursor] | (d[cursor + 1] << 8) | (d[cursor + 2] << 16) | (d[cursor + 3] << 24));
            cursor += 4;
            return v;
        }

        private static ushort ReadU16LE(byte[] d, ref int cursor)
        {
            var v = (ushort)(d[cursor] | (d[cursor + 1] << 8));
            cursor += 2;
            return v;
        }
    }

    /// <summary>握手 hello 协议（C2S，§11.6）。</summary>
    public sealed class HandshakeHelloProtocol : INetworkProtocol
    {
        public const string Path = "handshake:hello";

        public ProtocolId Id => ProtocolId.Of(NetworkMod.ModIdValue, Path);
        public ushort Version => 1;
        public NetworkDirection Direction => NetworkDirection.ClientToServer;
        public int MaxSize => 4096;

        public void Encode(in object message, INetworkWriter writer)
        {
            var hello = (HandshakeHello)message;
            writer.WriteBytes(1, HandshakeWire.EncodeEntries(hello.Entries));
        }

        public object Decode(INetworkReader reader)
        {
            if (reader.TryReadBytes(1, out var bytes))
                return new HandshakeHello { Entries = HandshakeWire.DecodeEntries(bytes) };
            return new HandshakeHello();
        }
    }

    /// <summary>握手 result 协议（S2C，§11.6）。</summary>
    public sealed class HandshakeResultProtocol : INetworkProtocol
    {
        public const string Path = "handshake:result";

        public ProtocolId Id => ProtocolId.Of(NetworkMod.ModIdValue, Path);
        public ushort Version => 1;
        public NetworkDirection Direction => NetworkDirection.ServerToClient;
        public int MaxSize => 4096;

        public void Encode(in object message, INetworkWriter writer)
        {
            var result = (HandshakeResult)message;
            writer.WriteByte(1, result.Accept ? (byte)1 : (byte)0);
            writer.WriteUInt32(2, result.Reason);
            writer.WriteBytes(3, HandshakeWire.EncodeAccepted(result.Accepted));
            writer.WriteBytes(4, HandshakeWire.EncodeDisabled(result.Disabled));
            writer.WriteString(5, result.MissingMods ?? "");
        }

        public object Decode(INetworkReader reader)
        {
            reader.TryReadByte(1, out var accept);
            reader.TryReadUInt32(2, out var reason);
            reader.TryReadBytes(3, out var acceptedBytes);
            reader.TryReadBytes(4, out var disabledBytes);
            reader.TryReadString(5, out var missing);
            return new HandshakeResult
            {
                Accept = accept != 0,
                Reason = reason,
                Accepted = HandshakeWire.DecodeAccepted(acceptedBytes ?? Array.Empty<byte>()),
                Disabled = HandshakeWire.DecodeDisabled(disabledBytes ?? Array.Empty<byte>()),
                MissingMods = missing,
            };
        }
    }
}
