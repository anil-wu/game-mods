namespace Game.Mod.Runtime
{
    /// <summary>
    /// 视图↔实体映射标记（Core 中介的视图层抽象，Rule 12）：持有 uint EntityId 的 MonoBehaviour
    /// （EnemyView / PlayerView / ItemView 等）实现本接口；其他 Mod 的视图（如 WeaponView 射线命中）
    /// 经 GetComponentsInParent&lt;MonoBehaviour&gt;() 检索本接口读取目标实体 id——零跨 Mod 类型引用、
    /// 零反射、零全局发现（仅限命中 collider 自身层级，§0.6）。
    /// 非泛型（Rule 13）：只暴露 uint EntityId。
    /// </summary>
    public interface IEntityView
    {
        /// <summary>视图绑定的逻辑实体 id（0 = 未绑定）。</summary>
        uint EntityId { get; }
    }
}
