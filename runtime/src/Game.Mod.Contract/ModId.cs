using System;

namespace Game.Mod.Contract
{
    /// <summary>
    /// Mod 的唯一标识，反向域名格式：com.mycompany.mymod
    /// 小写、点分多段、段内 [a-z0-9]、段数 >= 2。
    /// 发布后永不改变，是内容/消息/资源的命名空间根。
    /// </summary>
    public readonly struct ModId : IEquatable<ModId>, IComparable<ModId>
    {
        public string Value { get; }

        public ModId(string value)
        {
            if (!IsValid(value))
                throw new ArgumentException($"非法 modId: '{value}'", nameof(value));
            Value = value;
        }

        public static bool TryParse(string? value, out ModId id)
        {
            if (!IsValid(value))
            {
                id = default;
                return false;
            }
            id = new ModId(value!);
            return true;
        }

        private static bool IsValid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var segments = value.Split('.');
            if (segments.Length < 2) return false;
            foreach (var s in segments)
            {
                if (s.Length == 0) return false;
                foreach (var c in s)
                {
                    if (!(IsLowerAscii(c) || IsDigitAscii(c)))
                        return false;
                }
            }
            return true;
        }

        // netstandard2.1 无 char.IsAsciiLetterLower/IsAsciiDigit（.NET 7+），手动判定
        private static bool IsLowerAscii(char c) => c >= 'a' && c <= 'z';
        private static bool IsDigitAscii(char c) => c >= '0' && c <= '9';

        public override string ToString() => Value;

        public bool Equals(ModId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is ModId m && Equals(m);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public int CompareTo(ModId other) => string.CompareOrdinal(Value, other.Value);

        public static bool operator ==(ModId a, ModId b) => a.Equals(b);
        public static bool operator !=(ModId a, ModId b) => !a.Equals(b);
    }

}
