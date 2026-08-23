using System;

namespace Game.ECS
{
    /// <summary>
    /// 实体：纯 ID，跨网络稳定（uint）。
    /// 0 保留为 None。
    /// </summary>
    public readonly struct Entity : IEquatable<Entity>
    {
        public uint Id { get; }

        public Entity(uint id) => Id = id;

        public static readonly Entity None = new(0);
        public bool IsNone => Id == 0;

        public bool Equals(Entity other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is Entity e && Equals(e);
        public override int GetHashCode() => (int)Id;
        public override string ToString() => $"Entity({Id})";

        public static bool operator ==(Entity a, Entity b) => a.Equals(b);
        public static bool operator !=(Entity a, Entity b) => !a.Equals(b);
    }

}
