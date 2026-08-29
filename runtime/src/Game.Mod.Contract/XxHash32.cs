using System;
using System.Text;

namespace Game.Mod.Contract
{
    /// <summary>
    /// xxHash32（seed = 0）：全局协议 ID 的稳定哈希（§11.5，Rule 16）。
    /// 两端各自对同一字符串计算，结果天然一致，不依赖加载顺序。
    /// </summary>
    public static class XxHash32
    {
        private const uint Prime1 = 2654435761U;
        private const uint Prime2 = 2246822519U;
        private const uint Prime3 = 3266489917U;
        private const uint Prime4 = 668265263U;
        private const uint Prime5 = 374761393U;

        public static uint Compute(string value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            return Compute(Encoding.UTF8.GetBytes(value));
        }

        public static uint Compute(byte[] data) => Compute(data, 0, data.Length);

        public static uint Compute(byte[] data, int offset, int length)
        {
            uint h;
            var i = offset;
            var end = offset + length;

            if (length >= 16)
            {
                uint v1 = unchecked(Prime1 + Prime2), v2 = Prime2, v3 = 0, v4 = unchecked(0U - Prime1);
                var limit = end - 16;
                do
                {
                    v1 = Round(v1, ReadU32(data, i)); i += 4;
                    v2 = Round(v2, ReadU32(data, i)); i += 4;
                    v3 = Round(v3, ReadU32(data, i)); i += 4;
                    v4 = Round(v4, ReadU32(data, i)); i += 4;
                } while (i <= limit);

                h = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
            }
            else
            {
                h = Prime5;
            }

            h += (uint)length;

            while (i + 4 <= end)
            {
                h += ReadU32(data, i) * Prime3;
                h = Rotl(h, 17) * Prime4;
                i += 4;
            }

            while (i < end)
            {
                h += data[i] * Prime5;
                h = Rotl(h, 11) * Prime1;
                i++;
            }

            h ^= h >> 15; h *= Prime2;
            h ^= h >> 13; h *= Prime3;
            h ^= h >> 16;
            return h;
        }

        private static uint Round(uint acc, uint input) => Rotl(acc + input * Prime2, 13) * Prime1;
        private static uint Rotl(uint v, int r) => (v << r) | (v >> (32 - r));

        private static uint ReadU32(byte[] d, int i) =>
            (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24));
    }
}
