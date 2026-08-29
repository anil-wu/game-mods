using System;
using System.Collections.Generic;
using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>
    /// ModObject 的池域实现（§9.1）：Core 提供 PoolId + 非泛型 Rent/Return 基础设施，
    /// 池规模由 Mod 自己决定，Core 不需要知道 Weapon 有多少 Bullet Pool。
    /// </summary>
    public sealed class ModPoolScope : IModPoolScope
    {
        private sealed class Pool
        {
            public IPoolFactory Factory = null!;
            public readonly Stack<object> Idle = new();
            public int Rented;
        }

        private readonly ModId _owner;
        private readonly Dictionary<PoolId, Pool> _pools = new();

        public ModPoolScope(ModId owner) => _owner = owner;

        public void RegisterPool(PoolId id, IPoolFactory factory, int prewarm = 0)
        {
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            if (id.Mod != _owner)
                throw new ModStateException($"Mod '{_owner}' 不能注册其他 Mod 命名空间的池 '{id}'");
            if (_pools.ContainsKey(id)) return;

            var pool = new Pool { Factory = factory };
            for (var i = 0; i < prewarm; i++)
                pool.Idle.Push(factory.Create());
            _pools[id] = pool;
        }

        public object Rent(PoolId id)
        {
            if (!_pools.TryGetValue(id, out var pool))
                throw new ModStateException($"池 '{id}' 未注册");
            pool.Rented++;
            return pool.Idle.Count > 0 ? pool.Idle.Pop() : pool.Factory.Create();
        }

        public void Return(PoolId id, object instance)
        {
            if (instance is null) throw new ArgumentNullException(nameof(instance));
            if (!_pools.TryGetValue(id, out var pool))
                throw new ModStateException($"池 '{id}' 未注册");
            pool.Factory.Reset(instance);
            pool.Rented--;
            pool.Idle.Push(instance);
        }

        public void Clear()
        {
            foreach (var pool in _pools.Values)
            {
                pool.Idle.Clear();
                pool.Rented = 0;
            }
        }

        public int PoolCount => _pools.Count;
    }
}
