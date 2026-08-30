using System;
using System.Text;

namespace Game.Mod.Contract.Wire
{
    /// <summary>
    /// 载荷读取器：buffer 上的游标（§14.12.2 懒解码 + 字段级跳读）。
    /// 每次 TryRead 从头扫描到目标字段；不认识的字段按 wireType 长度 O(1) 跳过。
    /// 越界 / 类型不匹配抛 <see cref="PayloadFormatException"/>，绝不产生内存错误（UGC 防护）。
    /// </summary>
    public sealed class PayloadReader : INetworkReader
    {
        private readonly PayloadBuffer _buffer;

        public PayloadReader(in PayloadBuffer buffer) => _buffer = buffer;
        public PayloadReader(byte[] data) => _buffer = new PayloadBuffer(data);

        public int Length => _buffer.Length;

        public bool TryReadInt32(int fieldId, out int value)
        {
            value = 0;
            if (!TryFind(fieldId, WireType.Varint, out _)) return false;
            value = PayloadWriter.Unzigzag32((uint)ReadVarint(ref _cursor));
            return true;
        }

        public bool TryReadUInt32(int fieldId, out uint value)
        {
            value = 0;
            if (!TryFind(fieldId, WireType.Varint, out _)) return false;
            value = (uint)ReadVarint(ref _cursor);
            return true;
        }

        public bool TryReadInt64(int fieldId, out long value)
        {
            value = 0;
            if (!TryFind(fieldId, WireType.Varint, out _)) return false;
            value = PayloadWriter.Unzigzag64(ReadVarint(ref _cursor));
            return true;
        }

        public bool TryReadUInt64(int fieldId, out ulong value)
        {
            value = 0;
            if (!TryFind(fieldId, WireType.Varint, out _)) return false;
            value = ReadVarint(ref _cursor);
            return true;
        }

        public bool TryReadBool(int fieldId, out bool value)
        {
            value = false;
            if (!TryFind(fieldId, WireType.Varint, out _)) return false;
            value = ReadVarint(ref _cursor) != 0;
            return true;
        }

        public bool TryReadByte(int fieldId, out byte value)
        {
            value = 0;
            if (!TryFind(fieldId, WireType.Varint, out _)) return false;
            value = (byte)ReadVarint(ref _cursor);
            return true;
        }

        public bool TryReadFloat(int fieldId, out float value)
        {
            value = 0;
            if (!TryFind(fieldId, WireType.Fixed32, out var end)) return false;
            if (end - _cursor != 4) throw new PayloadFormatException($"字段 {fieldId} Fixed32 长度错误");
            value = BitConverter.ToSingle(_buffer.Data, _buffer.Offset + _cursor);
            return true;
        }

        public bool TryReadDouble(int fieldId, out double value)
        {
            value = 0;
            if (!TryFind(fieldId, WireType.Fixed64, out var end)) return false;
            if (end - _cursor != 8) throw new PayloadFormatException($"字段 {fieldId} Fixed64 长度错误");
            var lo = ReadRawU32(ref _cursor);
            var hi = ReadRawU32(ref _cursor);
            value = BitConverter.ToDouble(BitConverter.GetBytes(((ulong)hi << 32) | lo), 0); // netstandard2.1 无 UInt64BitsToDouble
            return true;
        }

        public bool TryReadString(int fieldId, out string value)
        {
            value = "";
            if (!TryReadLengthDelimited(fieldId, out var start, out var len)) return false;
            value = Encoding.UTF8.GetString(_buffer.Data, _buffer.Offset + start, len);
            return true;
        }

        public bool TryReadBytes(int fieldId, out byte[] value)
        {
            value = Array.Empty<byte>();
            if (!TryReadLengthDelimited(fieldId, out var start, out var len)) return false;
            value = new byte[len];
            Array.Copy(_buffer.Data, _buffer.Offset + start, value, 0, len);
            return true;
        }

        /// <summary>读取 LengthDelimited 字段的内容视图（零拷贝）。</summary>
        public bool TryReadView(int fieldId, out PayloadBuffer view)
        {
            view = default;
            if (!TryReadLengthDelimited(fieldId, out var start, out var len)) return false;
            view = new PayloadBuffer(_buffer.Data, _buffer.Offset + start, len);
            return true;
        }

        // ---- 内部扫描 ----

        private int _cursor;

        private bool TryReadLengthDelimited(int fieldId, out int start, out int len)
        {
            start = 0; len = 0;
            if (!TryFind(fieldId, WireType.LengthDelimited, out var end)) return false;
            var l = ReadRawU32(ref _cursor);
            if (l > int.MaxValue || _cursor + (int)l != end)
                throw new PayloadFormatException($"字段 {fieldId} 长度前缀与分帧不一致");
            start = _cursor;
            len = (int)l;
            return true;
        }

        /// <summary>扫描定位字段：找到后 _cursor 指向数据起点，end 指向字段末尾（下一段头）。</summary>
        private bool TryFind(int fieldId, WireType expected, out int end)
        {
            end = -1;
            _cursor = 0;
            while (_cursor < _buffer.Length)
            {
                var fid = ReadRawU16(ref _cursor);
                if (_cursor >= _buffer.Length) throw new PayloadFormatException("字段头截断");
                var type = (WireType)_buffer[_cursor++];
                var dataStart = _cursor;
                SkipData(type);
                if (fid == fieldId)
                {
                    if (type != expected)
                        throw new PayloadFormatException($"字段 {fieldId} 类型不匹配: 期望 {expected}，实际 {type}");
                    end = _cursor;
                    _cursor = dataStart;
                    return true;
                }
            }
            return false;
        }

        private void SkipData(WireType type)
        {
            switch (type)
            {
                case WireType.Varint:
                    ReadVarint(ref _cursor);
                    break;
                case WireType.Fixed32:
                    Require(4);
                    _cursor += 4;
                    break;
                case WireType.Fixed64:
                    Require(8);
                    _cursor += 8;
                    break;
                case WireType.LengthDelimited:
                    var len = ReadRawU32(ref _cursor);
                    if (len > int.MaxValue) throw new PayloadFormatException("长度前缀越界");
                    Require((int)len);
                    _cursor += (int)len;
                    break;
                default:
                    throw new PayloadFormatException($"未知 wireType: {(byte)type}");
            }
        }

        private ulong ReadVarint(ref int cursor)
        {
            ulong result = 0;
            var shift = 0;
            while (true)
            {
                if (cursor >= _buffer.Length) throw new PayloadFormatException("varint 截断");
                var b = _buffer[cursor++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift > 63) throw new PayloadFormatException("varint 过长");
            }
        }

        private ushort ReadRawU16(ref int cursor)
        {
            Require(cursor, 2);
            var v = (ushort)(_buffer[cursor] | (_buffer[cursor + 1] << 8));
            cursor += 2;
            return v;
        }

        private uint ReadRawU32(ref int cursor)
        {
            Require(cursor, 4);
            var v = (uint)(_buffer[cursor] | (_buffer[cursor + 1] << 8) |
                           (_buffer[cursor + 2] << 16) | (_buffer[cursor + 3] << 24));
            cursor += 4;
            return v;
        }

        private void Require(int count) => Require(_cursor, count);

        private void Require(int cursor, int count)
        {
            if (cursor + count > _buffer.Length)
                throw new PayloadFormatException("载荷截断");
        }
    }

    /// <summary>载荷格式错误（越界 / 类型不匹配 / 截断）。</summary>
    public sealed class PayloadFormatException : Exception
    {
        public PayloadFormatException(string message) : base(message) { }
    }
}
