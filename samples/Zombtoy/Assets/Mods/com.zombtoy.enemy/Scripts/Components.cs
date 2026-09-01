using Game.ECS;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人 ECS 组件（契约 §2，仅本 Mod 读写，其他 Mod 禁止触碰 World）。
    /// 单机 Host：无 [NetworkReplicated] 标签（summary §0.1）。
    /// 组件类型在 Register 里经 context.Ecs.RegisterComponent 注册；
    /// 敌人实体由本 Mod 创建/销毁（生成器），视图（EnemyView）挂 EntityId（summary §0.6），
    /// 武器射线命中 collider → GetComponentInParent&lt;EnemyView&gt;().EntityId → enemy:damage（契约 §8）。
    /// </summary>

    /// <summary>敌人类别（契约 §1：编号即契约，各 Mod 重复定义；跨边界传 u32 varint，数字即契约 §0.4）。</summary>
    public enum EnemyKind : byte
    {
        Zombunny = 0,
        ZomBear = 1,
        Hellephant = 2,
        GiantZombunny = 3,
        GiantZomBear = 4,
        GiantHellephant = 5,
        Titan = 6,
        Clown = 7,
        MiniClown = 8,
        ZomDuck = 9,
    }

    /// <summary>攻击类别（契约 §1）：Contact 接触；Fireball=Clown 火球（M2）；GroundTarget=Titan 地面锁定（M2）。</summary>
    public enum AttackKind : byte
    {
        Contact = 0,
        Fireball = 1,
        GroundTarget = 2,
    }

    /// <summary>敌人类型（EnemyKind 编号，byte 存储；跨边界传 u32）。</summary>
    public struct EnemyType : IComponent { public byte Kind; }

    /// <summary>生命/计分（死亡计分值 ScoreValue，契约 §3 注：EnemyKilled 携带）。</summary>
    public struct EnemyHealth : IComponent { public int Current; public int Max; public int ScoreValue; }

    /// <summary>标记：IsDead 死亡 / IsSinking 下沉中 / BlastImmunity 爆炸免疫（Titan）/ SinkTimer 下沉计时。</summary>
    public struct EnemyFlags : IComponent
    {
        public bool IsDead;
        public bool IsSinking;
        public bool BlastImmunity;
        public float SinkTimer;
    }

    /// <summary>攻击：Kind 类别 / Damage 伤害 / Range 距离 / Interval 间隔 / Cooldown 冷却。</summary>
    public struct EnemyAttack : IComponent
    {
        public byte Kind;
        public int Damage;
        public float Range;
        public float Interval;
        public float Cooldown;
    }

    /// <summary>移动：Speed 基础速度 / SpeedMul 缓速倍率（冰）/ EffectTimer 缓速效果计时（契约 §4 apply_slow）。</summary>
    public struct EnemyMovement : IComponent
    {
        public float Speed;
        public float SpeedMul;
        public float EffectTimer;
    }

    /// <summary>追击目标（经 player:get_entity 填充，Register 时取一次，契约 §7）。</summary>
    public struct EnemyTarget : IComponent { public uint PlayerEntityId; }

    /// <summary>逻辑位置（逻辑权威；视图 NavMesh 同步回写，契约 §2）。</summary>
    public struct EnemyPosition : IComponent { public float X, Y, Z; }

    /// <summary>生成器状态机（单实体，契约 §2）：Enabled 开关 / Timer 下次生成倒计时 /
    /// ActiveCount 存活敌人计数 / SpawnInterval 当前全局间隔（3s→×0.99→下限 0.6s 衰减）。</summary>
    public struct EnemySpawner : IComponent
    {
        public bool Enabled;
        public float Timer;
        public int ActiveCount;
        public float SpawnInterval;
    }
}
