using System;
using System.IO;
using System.Text;

namespace Game.Mod.Contract.Wire
{
    /// <summary>线格式字段类型（§14.11.1）。</summary>
    public enum WireType : byte
    {
        Varint = 0,           // u64 varint（有符号数经 zigzag）
        Fixed32 = 1,
        Fixed64 = 2,
        LengthDelimited = 3,  // u32 长度 + bytes（string / bytes / 嵌套）
    }

    /// <summary>
    /// 载荷写入器：字段编号 + 自描述分帧（§14.11）。
    /// 直写缓冲、无反射、无装箱（§14.12）；生成代码与手写解析共用同一原语。
    /// </summary>
    public sealed class PayloadWriter : INetworkWriter
    {
        private readonly MemoryStream _stream;

        public PayloadWriter() => _stream = new MemoryStream(256);

        public int Length => (int)_stream.Length;

        public void Reset() => _stream.SetLength(0);

        public PayloadBuffer ToBuffer() => new PayloadBuffer(_stream.ToArray());

        public void WriteInt32(int fieldId, int value) { Header(fieldId, WireType.Varint); WriteVarint(Zigzag32(value)); }
        public void WriteUInt32(int fieldId, uint value) { Header(fieldId, WireType.Varint); WriteVarint(value); }
        public void WriteInt64(int fieldId, long value) { Header(fieldId, WireType.Varint); WriteVarint(Zigzag64(value)); }
        public void WriteUInt64(int fieldId, ulong value) { Header(fieldId, WireType.Varint); WriteVarint(value); }
        public void WriteBool(int fieldId, bool value) { Header(fieldId, WireType.Varint); WriteVarint(value ? 1u : 0u); }
        public void WriteByte(int fieldId, byte value) { Header(fieldId, WireType.Varint); WriteVarint(value); }

        public void WriteFloat(int fieldId, float value)
        {
            Header(fieldId, WireType.Fixed32);
            var bits = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            WriteRawU32(bits);
        }

        public void WriteString(int fieldId, string value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            WriteBytes(fieldId, Encoding.UTF8.GetBytes(value));
        }

        public void WriteBytes(int fieldId, byte[] value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            Header(fieldId, WireType.LengthDelimited);
            WriteRawU32((uint)value.Length);
            _stream.Write(value, 0, value.Length);
        }

        private void Header(int fieldId, WireType type)
        {
            if (fieldId <= 0 || fieldId > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(fieldId), "fieldId 必须在 1..65535");
            _stream.WriteByte((byte)(fieldId & 0xFF));
            _stream.WriteByte((byte)((fieldId >> 8) & 0xFF));
            _stream.WriteByte((byte)type);
        }

        private void WriteVarint(ulong v)
        {
            while (v >= 0x80)
            {
                _stream.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            _stream.WriteByte((byte)v);
        }

        private void WriteRawU32(uint v)
        {
            _stream.WriteByte((byte)(v & 0xFF));
            _stream.WriteByte((byte)((v >> 8) & 0xFF));
            _stream.WriteByte((byte)((v >> 16) & 0xFF));
            _stream.WriteByte((byte)((v >> 24) & 0xFF));
        }

        internal static uint Zigzag32(int v) => (uint)((v << 1) ^ (v >> 31));
        internal static ulong Zigzag64(long v) => (ulong)((v << 1) ^ (v >> 63));
        internal static int Unzigzag32(uint v) => (int)(v >> 1) ^ -(int)(v & 1);
        internal static long Unzigzag64(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);
    }
}
