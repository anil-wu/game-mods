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

    /// <summary>攻击类别（契约 §1）：Contact 接触；Fireball=Clown 火球（远程）；GroundTarget=Titan 地面锁定（首领）。</summary>
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

    /// <summary>投射物类别（契约 §3 序 4/5；视图按此选原 prefab）：Fireball=Clown 火球
    /// → EnemyProjectile.prefab；Rocket=Titan 地面锁定 → EnemyRocket Variant.prefab（Boss 落点视图表现，
    /// 逻辑 AOE 由 EnemyBossSystem 直接结算，Rocket 不建逻辑实体）。</summary>
    public enum EnemyProjectileKind : byte
    {
        Fireball = 0,
        Rocket = 1,
    }

    /// <summary>
    /// 投射物逻辑实体（契约 §3 序 4：EnemyRangedSystem 生成，Clown 火球）。
    /// 直线飞行（原版 EnemyProjectile：transform.Translate 沿自身前方，无追踪）：命中玩家 → player:damage；
    /// 超时（原版 Destroy(gameObject, 10f)）/命中即销毁实体并通知视图（Rule 20：归属本 Mod，卸载强制回收）。
    /// 位置/方向在组件内自持（逻辑权威；视图每帧读取同步 GO transform，契约 §2 语义）。
    /// </summary>
    public struct EnemyProjectile : IComponent
    {
        public byte Kind;            // EnemyProjectileKind
        public int Damage;           // 命中伤害（契约 §9：Clown 火球 40；开火时从 EnemyAttack.Damage 固化）
        public uint Source;          // 开火敌人实体（伤害归属）
        public float Speed;          // 直线速度（m/s，EnemyConfig.FireballSpeed）
        public float Lifetime;       // 剩余存活秒（原版 Destroy(gameObject, 10f)）
        public float X, Y, Z;        // 当前逻辑位置
        public float DirX, DirY, DirZ; // 单位飞行方向（水平直线，无追踪）
    }

    /// <summary>
    /// 敌人回血（M3，原版 EnemyRegen.cs——仅"带回血属性"的类型在生成时挂本组件，
    /// 原版 ZomBear/GiantZombear prefab 序列化 cooldown=0.1/0.05、regenAmount=1）。
    /// Timer = 下次回血节拍倒计时（初始 = 类型 RegenInterval）：EnemyAuxSystem 每帧递减，到点 +RegenAmount
    /// （EnemyConfig 按类型读），封顶 EnemyHealth.Max；IsDead 不结算（原版 Regenerate 判 !isDead）。
    /// </summary>
    public struct EnemyRegen : IComponent { public float Timer; }

    /// <summary>
    /// Clown 死亡延迟召唤 MiniClown（M3，原版 SpawnClown.cs：死亡后 Invoke("Spawn", 1f)）。
    /// 挂死亡 Clown 实体（随尸体下沉/销毁取消，reset 清场一并清理）：Timer=召唤延迟；到点按两个槽位生成
    /// （Clown.prefab 双 SpawnPoint，各 clownsSpawned=1、15% +1）——生成计入 ActiveCount 并发布
    /// ZombieCountChanged（任务：计数纳入；生成器全局并发上限 50 满时不补位）。XA/XB 等为死亡瞬间冻结的出生 XZ。
    /// </summary>
    public struct EnemyPendingMiniClowns : IComponent
    {
        public float Timer;     // 召唤延迟（原版 1s）
        public int PendingA;    // 槽位 A 待生成数（1 + 15% +1）
        public float XA, ZA;    // 槽位 A 出生点
        public int PendingB;    // 槽位 B 待生成数
        public float XB, ZB;    // 槽位 B 出生点
    }

    /// <summary>
    /// Titan 地面锁定状态（契约 §3 序 5 EnemyBossSystem 专用，GroundTarget 类敌人懒建）。
    /// Tracking=射程内锁定中（视图 decal 显隐）；Charge=蓄力进度（≥ EnemyAttack.Interval 触发落点 AOE 并清零）；
    /// X/Z=准星（独立 decal）当前地面落点——向玩家 XZ 指数 lerp（原版 EnemyTargetShooting：Lerp(pos, player, dt*5)）。
    /// </summary>
    public struct EnemyBossLock : IComponent
    {
        public bool Tracking;
        public float Charge;
        public float X, Z;
    }
}
