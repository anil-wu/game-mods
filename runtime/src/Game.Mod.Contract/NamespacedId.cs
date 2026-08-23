using System;

namespace Game.Mod.Contract
{
    /// <summary>
    /// 命名空间 ID："modId:localId"，用于内容 ID 与消息 ID。
    /// </summary>
    public readonly struct NamespacedId : IEquatable<NamespacedId>
    {
        public ModId Mod { get; }
        public string LocalId { get; }

        public NamespacedId(ModId mod, string localId)
        {
            if (string.IsNullOrWhiteSpace(localId))
                throw new ArgumentException("localId 不能为空", nameof(localId));
            Mod = mod;
            LocalId = localId;
        }

        public static bool TryParse(string? value, out NamespacedId id)
        {
            id = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var idx = value.IndexOf(':');
            if (idx <= 0 || idx == value.Length - 1) return false;
            if (!ModId.TryParse(value.Substring(0, idx), out var mod)) return false;
            id = new NamespacedId(mod, value.Substring(idx + 1));
            return true;
        }

        public override string ToString() => $"{Mod.Value}:{LocalId}";

        public bool Equals(NamespacedId other) =>
            Mod.Equals(other.Mod) && string.Equals(LocalId, other.LocalId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is NamespacedId n && Equals(n);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Mod.GetHashCode();
                h = h * 397 ^ StringComparer.Ordinal.GetHashCode(LocalId);
                return h;
            }
        }

        public static bool operator ==(NamespacedId a, NamespacedId b) => a.Equals(b);
        public static bool operator !=(NamespacedId a, NamespacedId b) => !a.Equals(b);
    }

}
