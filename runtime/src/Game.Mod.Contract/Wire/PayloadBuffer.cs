using System;

namespace Game.Mod.Contract.Wire
{
    /// <summary>
    /// 二进制载荷缓冲：跨 Mod 边界唯一合法的通信介质（Rule 14）。
    /// bytes 里不可能藏对象引用——热卸载边界由机制保证（§14.5）。
    /// </summary>
    public readonly struct PayloadBuffer
    {
        public readonly byte[] Data;
        public readonly int Offset;
        public readonly int Length;

        public PayloadBuffer(byte[] data) : this(data, 0, data?.Length ?? 0) { }

        public PayloadBuffer(byte[] data, int offset, int length)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            if (offset < 0 || length < 0 || offset + length > data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            Offset = offset;
            Length = length;
        }

        public byte this[int i]
        {
            get
            {
                if (i < 0 || i >= Length) throw new ArgumentOutOfRangeException(nameof(i));
                return Data[Offset + i];
            }
        }

        public byte[] ToArray()
        {
            var copy = new byte[Length];
            Array.Copy(Data, Offset, copy, 0, Length);
            return copy;
        }

        public override string ToString() => $"Payload({Length} bytes)";
    }
}
