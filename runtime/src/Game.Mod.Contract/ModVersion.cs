using System;

namespace Game.Mod.Contract
{
    /// <summary>
    /// 语义化版本（semver）：major.minor.patch[-prerelease]。
    /// </summary>
    public readonly struct ModVersion : IEquatable<ModVersion>, IComparable<ModVersion>
    {
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public string? PreRelease { get; }

        public ModVersion(int major, int minor, int patch, string? preRelease = null)
        {
            if (major < 0 || minor < 0 || patch < 0)
                throw new ArgumentOutOfRangeException(nameof(major), "版本号不能为负");
            Major = major;
            Minor = minor;
            Patch = patch;
            PreRelease = preRelease;
        }

        public static bool TryParse(string? text, out ModVersion v)
        {
            v = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string core = text;
            string? pre = null;
            var dash = text.IndexOf('-');
            if (dash >= 0)
            {
                core = text.Substring(0, dash);
                pre = text.Substring(dash + 1);
                if (pre.Length == 0) return false;
            }

            var parts = core.Split('.');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out var major)) return false;
            if (!int.TryParse(parts[1], out var minor)) return false;
            if (!int.TryParse(parts[2], out var patch)) return false;

            v = new ModVersion(major, minor, patch, pre);
            return true;
        }

        public static ModVersion Parse(string text) =>
            TryParse(text, out var v) ? v : throw new FormatException($"非法版本号: '{text}'");

        public int CompareTo(ModVersion other)
        {
            var c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            c = Minor.CompareTo(other.Minor);
            if (c != 0) return c;
            c = Patch.CompareTo(other.Patch);
            if (c != 0) return c;

            bool aRelease = string.IsNullOrEmpty(PreRelease);
            bool bRelease = string.IsNullOrEmpty(other.PreRelease);
            if (aRelease && bRelease) return 0;
            if (aRelease) return 1;   // 正式版 > 预发布版
            if (bRelease) return -1;
            return string.CompareOrdinal(PreRelease, other.PreRelease);
        }

        public override string ToString() =>
            PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

        public bool Equals(ModVersion other) => CompareTo(other) == 0;

        public override bool Equals(object? obj) => obj is ModVersion v && Equals(v);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Major;
                h = h * 397 ^ Minor;
                h = h * 397 ^ Patch;
                h = h * 397 ^ (PreRelease is null ? 0 : StringComparer.Ordinal.GetHashCode(PreRelease));
                return h;
            }
        }

        public static bool operator ==(ModVersion a, ModVersion b) => a.Equals(b);
        public static bool operator !=(ModVersion a, ModVersion b) => !a.Equals(b);
        public static bool operator <(ModVersion a, ModVersion b) => a.CompareTo(b) < 0;
        public static bool operator <=(ModVersion a, ModVersion b) => a.CompareTo(b) <= 0;
        public static bool operator >(ModVersion a, ModVersion b) => a.CompareTo(b) > 0;
        public static bool operator >=(ModVersion a, ModVersion b) => a.CompareTo(b) >= 0;
    }

}
