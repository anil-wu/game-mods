using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>池对象工厂（非泛型，Rule 13）。</summary>
    public interface IPoolFactory
    {
        object Create();

        /// <summary>归还时重置实例状态。</summary>
        void Reset(object instance);
    }

    /// <summary>
    /// Mod 对象池域（§9.1）：Core 提供池基础设施，池规模由 Mod 自己决定；
    /// PoolId + 非泛型 Rent/Return（§15.3）。GameObject Pool 属于 Mod（VFX / UI / Prefab / Particle / Audio），
    /// ECS Entity 由 ECS Runtime 管理（§9.2）。卸载时 Core 强制 Clear()。
    /// </summary>
    public interface IModPoolScope
    {
        /// <summary>注册池（id 命名空间必须属于当前 Mod），可预热 prewarm 个实例。</summary>
        void RegisterPool(PoolId id, IPoolFactory factory, int prewarm = 0);

        /// <summary>租用实例。</summary>
        object Rent(PoolId id);

        /// <summary>归还实例（自动 Reset）。</summary>
        void Return(PoolId id, object instance);

        /// <summary>清空全部池（卸载流程由 Core 调用）。</summary>
        void Clear();
    }
}
