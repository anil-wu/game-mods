using System.Collections.Generic;

namespace Game.ECS
{
    /// <summary>组件存储的非泛型视图，供 World 统一销毁实体时调用。</summary>
    public interface IComponentStore
    {
        void RemoveEntity(uint id);
        int Count { get; }
    }

    /// <summary>
    /// 单一组件类型的存储（稀疏映射：entityId → 组件值）。
    /// </summary>
    public sealed class ComponentStore<T> : IComponentStore where T : struct, IComponent
    {
        private readonly Dictionary<uint, T> _data = new();

        public int Count => _data.Count;

        public void Set(uint entity, in T component) => _data[entity] = component;
        public bool Remove(uint entity) => _data.Remove(entity);
        public bool TryGet(uint entity, out T component) => _data.TryGetValue(entity, out component);
        public T Get(uint entity) => _data[entity];
        public bool Contains(uint entity) => _data.ContainsKey(entity);

        public IEnumerable<(uint Entity, T Component)> All()
        {
            foreach (var kv in _data)
                yield return (kv.Key, kv.Value);
        }

        public void RemoveEntity(uint id) => _data.Remove(id);
    }

}
