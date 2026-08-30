using System;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// 帧编解码（§11.5，从 NetworkRuntimeImpl 迁出的瘦身层）：
    /// 12B 帧头 [ProtocolId u32][Ver u16][Flags u16][Payload Len u32] 读写 + MaxSize 校验（§11.12 UGC 防护）。
    /// </summary>
    internal sealed class FrameCodec
    {
        /// <summary>帧头：[ProtocolId u32][Ver u16][Flags u16][Payload Len u32]（§11.5）。</summary>
        public const int FrameHeaderSize = 12;

        /// <summary>
        /// 编码：协议 Encode → MaxSize 校验 → 12B 帧头 + 载荷。
        /// 帧头 ProtocolId 由调用方（注册表条目）指定——legacy codec 也必须以同一逻辑 ID 出帧（§11.6）。
        /// 超 MaxSize 抛 <see cref="NetworkException"/>（调用方计数 DroppedQuota，§11.12 UGC 防护）。
        /// </summary>
        public byte[] Encode(ProtocolId id, INetworkProtocol protocol, in object message, ushort version)
        {
            var writer = new PayloadWriter();
            protocol.Encode(in message, writer);
            var payload = writer.ToBuffer();

            if (payload.Length > protocol.MaxSize)
                throw new NetworkException(
                    $"协议 {id} 载荷 {payload.Length}B 超 MaxSize {protocol.MaxSize}B（§11.12 UGC 防护）");

            var frame = new byte[FrameHeaderSize + payload.Length];
            WriteU32(frame, 0, id.Value);
            WriteU16(frame, 4, version);
            WriteU16(frame, 6, 0); // Flags
            WriteU32(frame, 8, (uint)payload.Length);
            Array.Copy(payload.Data, payload.Offset, frame, FrameHeaderSize, payload.Length);
            return frame;
        }

        /// <summary>解析帧头：成功返回 true 并给出 id / version / 载荷偏移 / 载荷长度。</summary>
        public bool TryParseHeader(byte[] frame, out ProtocolId id, out ushort version,
            out int payloadOffset, out int payloadLen)
        {
            id = default;
            version = 0;
            payloadOffset = 0;
            payloadLen = 0;
            if (frame is null || frame.Length < FrameHeaderSize) return false;
            id = new ProtocolId(ReadU32(frame, 0));
            version = ReadU16(frame, 4);
            payloadLen = (int)ReadU32(frame, 8);
            if (payloadLen < 0 || FrameHeaderSize + payloadLen > frame.Length) return false;
            payloadOffset = FrameHeaderSize;
            return true;
        }

        internal static void WriteU16(byte[] d, int o, ushort v)
        {
            d[o] = (byte)(v & 0xFF);
            d[o + 1] = (byte)((v >> 8) & 0xFF);
        }

        internal static void WriteU32(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v & 0xFF);
            d[o + 1] = (byte)((v >> 8) & 0xFF);
            d[o + 2] = (byte)((v >> 16) & 0xFF);
            d[o + 3] = (byte)((v >> 24) & 0xFF);
        }

        internal static ushort ReadU16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));

        internal static uint ReadU32(byte[] d, int o) =>
            (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    }
}
