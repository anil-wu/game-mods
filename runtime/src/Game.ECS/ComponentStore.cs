using System.Collections.Generic;

namespace Game.ECS
{
    /// <summary>组件存储的非泛型视图，供 World 统一销毁实体时调用。</summary>
    public interface IComponentStore
    {
        void RemoveEntity(uint id);
        int Count { get; }
        bool TryGetRaw(uint entity, out object component);
        /// <summary>装箱写入（Replication 客户端应用等非泛型路径）。</summary>
        void SetRaw(uint entity, object component);
        IEnumerable<(uint Entity, object Component)> RawAll();
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
            // 快照迭代：允许系统在查询期间写回组件（如 WinCheckSystem 写回 Session），
            // 避免 Mono 下“集合被修改导致枚举异常”。
            var snapshot = new List<KeyValuePair<uint, T>>(_data);
            foreach (var kv in snapshot)
                yield return (kv.Key, kv.Value);
        }

        public bool TryGetRaw(uint entity, out object component)
        {
            if (_data.TryGetValue(entity, out var c))
            {
                component = c;
                return true;
            }
            component = null!;
            return false;
        }

        public void SetRaw(uint entity, object component) => _data[entity] = (T)component;

        public IEnumerable<(uint Entity, object Component)> RawAll()
        {
            var snapshot = new List<KeyValuePair<uint, T>>(_data);
            foreach (var kv in snapshot)
                yield return (kv.Key, kv.Value);
        }

        public void RemoveEntity(uint id) => _data.Remove(id);
    }

}
