using Game.ECS;

namespace Com.Zombtoy.Player
{
    /// <summary>
    /// 玩家 ECS 组件（契约 §1，仅本 Mod 读写，其他 Mod 禁止触碰 World）。
    /// 单机 Host：无 [NetworkReplicated] 标签（summary §0.1）。
    /// 组件类型在 Register 里经 context.Ecs.RegisterComponent 注册；
    /// 玩家主实体经 context.Ecs.CreateEntity() 创建（归属本 ModObject，卸载强制回收 §9.2）。
    /// </summary>

    /// <summary>世界坐标（逻辑权威；视图每帧读它同步 Transform）。</summary>
    public struct Position3 : IComponent { public float X, Y, Z; }

    /// <summary>期望速度（逻辑；视图驱动 Rigidbody.MovePosition 平滑移动）。</summary>
    public struct Velocity3 : IComponent { public float X, Y, Z; }

    /// <summary>朝向（度，绕 Y；鼠标射线打 Floor 得到，视图写入）。</summary>
    public struct Facing : IComponent { public float Yaw; }

    /// <summary>生命（Current &lt;= 0 = 死亡）。伤害/治疗在能力内同步修改（契约 §3）。</summary>
    public struct PlayerHealth : IComponent { public int Current; public int Max; }

    /// <summary>体力 [0, Max]（冲刺消耗 / 非冲刺恢复，由 PlayerStaminaSystem 结算）。</summary>
    public struct Stamina : IComponent { public float Current; public float Max; }

    /// <summary>最新输入（视图写入：WASD + Shift）。</summary>
    public struct PlayerInput : IComponent
    {
        public float MoveX;   // 左右 [-1,1]
        public float MoveZ;   // 前后 [-1,1]
        public bool Sprint;   // 冲刺（Shift）
    }

    /// <summary>玩家标记：IsDead = 死亡（Enemy 追击 / Item 拾取的判断依据）。</summary>
    public struct PlayerFlags : IComponent { public bool IsDead; }
}
