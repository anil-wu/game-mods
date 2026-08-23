using System;
using System.Collections.Generic;

namespace Game.ECS
{
    /// <summary>
    /// ECS 世界：实体 + 组件存储。
    /// 状态只在组件里，逻辑只在系统里。
    /// </summary>
    public sealed class World
    {
        private uint _nextId = 1;
        private readonly HashSet<uint> _entities = new();
        private readonly Dictionary<Type, IComponentStore> _stores = new();

        public int EntityCount => _entities.Count;

        public Entity CreateEntity()
        {
            var id = _nextId++;
            _entities.Add(id);
            return new Entity(id);
        }

        public void Destroy(Entity entity)
        {
            if (!_entities.Remove(entity.Id)) return;
            foreach (var store in _stores.Values)
                store.RemoveEntity(entity.Id);
        }

        public bool Exists(Entity entity) => _entities.Contains(entity.Id);

        /// <summary>预注册组件类型（等价于 Store&lt;T&gt;()，语义化别名）。</summary>
        public void RegisterComponent<T>() where T : struct, IComponent => Store<T>();

        /// <summary>获取（或创建）某组件类型的存储。</summary>
        public ComponentStore<T> Store<T>() where T : struct, IComponent
        {
            var t = typeof(T);
            if (_stores.TryGetValue(t, out var existing))
                return (ComponentStore<T>)existing;
            var store = new ComponentStore<T>();
            _stores[t] = store;
            return store;
        }

        /// <summary>按类型获取组件存储（非泛型，供反射/工具层使用）。</summary>
        public IComponentStore? TryGetStore(Type componentType)
            => _stores.TryGetValue(componentType, out var s) ? s : null;

        public void Add<T>(Entity entity, in T component) where T : struct, IComponent
        {
            EnsureExists(entity);
            Store<T>().Set(entity.Id, component);
        }

        public bool TryGet<T>(Entity entity, out T component) where T : struct, IComponent
        {
            component = default;
            return _entities.Contains(entity.Id) && Store<T>().TryGet(entity.Id, out component);
        }

        public T Get<T>(Entity entity) where T : struct, IComponent
        {
            if (!TryGet(entity, out T component))
                throw new KeyNotFoundException($"实体 {entity} 缺少组件 {typeof(T).Name}");
            return component;
        }

        public bool Has<T>(Entity entity) where T : struct, IComponent
            => _entities.Contains(entity.Id) && Store<T>().Contains(entity.Id);

        public bool Remove<T>(Entity entity) where T : struct, IComponent
            => Store<T>().Remove(entity.Id);

        public IEnumerable<Entity> All()
        {
            foreach (var id in _entities)
                yield return new Entity(id);
        }

        private void EnsureExists(Entity entity)
        {
            if (!_entities.Contains(entity.Id))
                throw new ArgumentException($"实体不存在: {entity}");
        }
    }

}
