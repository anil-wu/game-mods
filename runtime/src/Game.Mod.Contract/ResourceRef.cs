using System;

namespace Game.Mod.Contract
{
    /// <summary>
    /// 资源寻址：
    ///   "assets/hero.png"                  → 本 Mod 资源
    ///   "com.other.mod:assets/hero.png"    → 外部 Mod 资源
    /// 判定规则：含 ':' 且前缀是合法 modId → 外部资源；否则视为本 Mod 的路径。
    /// </summary>
    public readonly struct ResourceRef : IEquatable<ResourceRef>
    {
        /// <summary>外部 Mod（本 Mod 资源为 null）。</summary>
        public ModId? ExternalMod { get; }

        /// <summary>资源在所属包内的相对路径，与 manifest files[].path 对应。</summary>
        public string Path { get; }

        public bool IsExternal => ExternalMod.HasValue;

        public ResourceRef(string path)
        {
            ExternalMod = null;
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public ResourceRef(ModId externalMod, string path)
        {
            ExternalMod = externalMod;
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public static ResourceRef Parse(string value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            var idx = value.IndexOf(':');
            if (idx > 0 && ModId.TryParse(value.Substring(0, idx), out var mod))
                return new ResourceRef(mod, value.Substring(idx + 1));
            return new ResourceRef(value);
        }

        public override string ToString() => ExternalMod.HasValue ? $"{ExternalMod.Value.Value}:{Path}" : Path;

        public bool Equals(ResourceRef other) =>
            ExternalMod.Equals(other.ExternalMod) && string.Equals(Path, other.Path, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ResourceRef r && Equals(r);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = ExternalMod?.GetHashCode() ?? 0;
                h = h * 397 ^ StringComparer.Ordinal.GetHashCode(Path);
                return h;
            }
        }

        public static bool operator ==(ResourceRef a, ResourceRef b) => a.Equals(b);
        public static bool operator !=(ResourceRef a, ResourceRef b) => !a.Equals(b);
    }

}
