namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人行为参数（契约 §9 配置常量表；结构固定、数值可调，改动需升 CONTRACT 版本与测试向量，summary §0.8）。
    /// 数值来源（只读机制数值，不搬代码）：原版 EnemyManager.cs（生成器加权表/每类型冷却/并发上限/间隔衰减）、
    /// EnemyHealth.cs（生命/下沉 2f/计分）、EnemyMovement.cs（追击速度）、EnemyAttack.cs（接触伤害 0.5s 间隔）、
    /// M3 辅助行为：SelfDestruct.cs + ZomDuckk.prefab 序列化（BlaseDamage=99）、SpawnClown.cs + Clown.prefab
    /// （双 SpawnPoint/clownsSpawned）、EnemyRegen.cs + ZomBear/GiantZombear.prefab 序列化（cooldown×regenAmount）。
    /// 默认配置在资源迁移时按 prefab 校准（summary §0.8）。
    /// </summary>
    public static class EnemyConfig
    {
        // ---- 生成器全局参数（原版 EnemyManager 字段） ----
        /// <summary>全局并发上限（原版 maxTotalEnemies=50，契约 §3 序 1）。</summary>
        public const int MaxTotalEnemies = 50;

        /// <summary>基础生成间隔（原版 baseSpawnTime=3s）。</summary>
        public const float BaseSpawnTime = 3.0f;

        /// <summary>每次生成后间隔 ×0.99（原版 spawnTimeReduction）。</summary>
        public const float SpawnTimeReduction = 0.99f;

        /// <summary>间隔下限（原版 minimumSpawnTime=0.6s）。</summary>
        public const float MinSpawnTime = 0.6f;

        /// <summary>无可生成类型（全部冷却中）时的重试间隔。</summary>
        public const float RetryInterval = 0.25f;

        /// <summary>死亡下沉时长（原版 Destroy(gameObject, 2f)，契约 §3 序 6）。</summary>
        public const float SinkDuration = 2.0f;

        /// <summary>下沉速度（原版 EnemyHealth.sinkSpeed=2.5f，视图 EnemyView 用）。</summary>
        public const float SinkSpeed = 2.5f;

        /// <summary>接触攻击距离（触发 collider 半径的默认校准值，资源迁移时按 prefab 校准）。</summary>
        public const float ContactRange = 2.0f;

        // ---- 远程火球（契约 §3 序 4，M2；数值对齐原版 EnemyProjectile.prefab 字段与行为） ----
        /// <summary>火球飞行速度：原版 EnemyProjectile.prefab speed=4，但移动代码把 speed 乘了两次
        /// （Translate((0,0,speed)*speed*dt)，实际 16 m/s）——按实际行为对齐（25m 射程 ~1.6s 到达）。</summary>
        public const float FireballSpeed = 16f;

        /// <summary>投射物存活时长（原版 Destroy(gameObject, 10f)：超时销毁）。</summary>
        public const float ProjectileLifetime = 10f;

        /// <summary>火球命中判距（XZ 平面近似）：原版 OverlapSphere(0.6) 撞玩家 collider（胶囊半径≈0.5）→ 判定半径≈1.1，取 1.5 覆盖摆位误差。</summary>
        public const float ProjectileHitRadius = 1.5f;

        /// <summary>投射物出膛高度（逻辑 Y；视觉近似原版 Clown shootPoint 子对象高度，契约 §2 逻辑权威）。</summary>
        public const float ProjectileHeight = 1.2f;

        // ---- Titan 地面锁定（契约 §3 序 5，M2；数值对齐原版 EnemyTargetShooting / EnemyRocket Variant） ----
        // ---- 敌人辅助行为（M3 任务补全；数值对齐原版 prefab 序列化与脚本，改动需升 CONTRACT 版本与测试向量） ----

        /// <summary>
        /// ZomDuck 死亡自爆（SelfDestruct/IBlast，原版 SelfDestruct.cs）：爆炸点 = 死亡点，外圈半径
        /// OverlapSphere(transform.position, 3f)。原版内圈 DeadlyExplosionRadius=1f 的双层伤害循环被注释
        /// （死代码未执行）→ 只复刻执行中的 3m 单圈。
        /// </summary>
        public const float ZomDuckBlastRadius = 3f;

        /// <summary>ZomDuck 死亡自爆对玩家伤害（原版 ZomDuckk.prefab SelfDestruct.BlaseDamage=99）。</summary>
        public const int ZomDuckBlastPlayerDamage = 99;

        /// <summary>ZomDuck 死亡自爆对范围内其他敌人伤害（原版 BlaseDamage/6 = 99/6 = 16，int 整除）。</summary>
        public const int ZomDuckBlastEnemyDamage = 16;

        /// <summary>Clown 死亡召唤 MiniClown 延迟（原版 SpawnClown.cs：Invoke("Spawn", 1f)）。</summary>
        public const float ClownMiniSpawnDelay = 1f;

        /// <summary>召唤槽位偏移（原版 Clown.prefab 两个 SpawnPoint 子对象 local x ≈ ±0.8：-0.822/0.753，y 强制 0）。</summary>
        public const float ClownMiniSpawnOffsetX = 0.8f;

        /// <summary>每个槽位额外 +1 只的概率（原版 SpawnClown.Start：Random 0..100 ≤ 15 → clownsSpawned++）。</summary>
        public const double ClownMiniSpawnExtraChance = 0.15;

        /// <summary>准星追踪 lerp 速率（原版 EnemyTargetShooting：Vector3.Lerp(groundTarget, player, Time.deltaTime*5)）。</summary>
        public const float BossLockLerpRate = 5f;

        /// <summary>Titan 落点 AOE 半径（原版 EnemyProjectile rocket 爆炸半径 explosionRadius=3.0；契约 §3 序 5 落点 AOE）。</summary>
        public const float BossAoeRadius = 3f;

        /// <summary>Titan 火箭视图飞行速度（原版 EnemyRocket Variant speed=3.5 × 双乘 → 实际 12.25 m/s；纯视图表现，逻辑 AOE 即时）。</summary>
        public const float RocketVisualSpeed = 12.25f;

        // ---- 出生点（Rule 3 本 Mod 自持；与 game Mod Level 场景标记保持一致，契约 §9 同步约束） ----
        public static readonly (float X, float Y, float Z)[] SpawnPoints =
        {
            (14f, 0f, 14f), (-14f, 0f, 14f), (14f, 0f, -14f), (-14f, 0f, -14f),
            (0f, 0f, 18f), (18f, 0f, 0f), (0f, 0f, -18f), (-18f, 0f, 0f),
        };

        /// <summary>
        /// 每类型静态表（契约 §9：HP/ScoreValue/Speed/攻击 + 生成器字段 weight/min/max/maxConcurrent/groupSize/startBufferTime）。
        /// 结构固定、数值可调；MiniClown 权重 0（由 Clown 死亡生成，M2，原版 SpawnClown.cs，不经生成器）。
        /// </summary>
        public readonly struct KindStats
        {
            public readonly EnemyKind Kind;
            public readonly int Hp;
            public readonly int ScoreValue;
            public readonly float Speed;
            public readonly AttackKind Attack;
            public readonly int AttackDamage;
            public readonly float AttackRange;
            public readonly float AttackInterval;
            public readonly bool BlastImmunity;
            // 生成器（原版 EnemyManager.EnemySpawnData）
            public readonly int SpawnWeight;
            public readonly float MinSpawnTime;
            public readonly float MaxSpawnTime;
            public readonly int MaxConcurrent;
            public readonly int GroupMin;
            public readonly int GroupMax;
            public readonly float StartBufferTime;
            // 回血（M3：原版 EnemyRegen.cs，仅带"回血属性"的类型挂组件——ZomBear/GiantZomBear prefab 序列化 cooldown/regenAmount）
            public readonly bool HasRegen;
            public readonly float RegenInterval;
            public readonly int RegenAmount;

            public KindStats(EnemyKind kind, int hp, int score, float speed, AttackKind attack,
                int damage, float range, float interval, bool blastImmunity,
                int weight, float min, float max, int maxConcurrent, int groupMin, int groupMax, float buffer,
                bool hasRegen = false, float regenInterval = 0f, int regenAmount = 0)
            {
                Kind = kind;
                Hp = hp;
                ScoreValue = score;
                Speed = speed;
                Attack = attack;
                AttackDamage = damage;
                AttackRange = range;
                AttackInterval = interval;
                BlastImmunity = blastImmunity;
                SpawnWeight = weight;
                MinSpawnTime = min;
                MaxSpawnTime = max;
                MaxConcurrent = maxConcurrent;
                GroupMin = groupMin;
                GroupMax = groupMax;
                StartBufferTime = buffer;
                HasRegen = hasRegen;
                RegenInterval = regenInterval;
                RegenAmount = regenAmount;
            }
        }

        /// <summary>按 EnemyKind 编号索引（[0]=Zombunny … [9]=ZomDuck，契约 §1）。</summary>
        public static readonly KindStats[] Table =
        {
            new(EnemyKind.Zombunny,       100, 100, 3.0f, AttackKind.Contact,     10, ContactRange, 0.5f, false, 20, 1.0f,  5.0f,  20, 1, 2,  0f),
            // ZomBear 系回血（M3）：原版 ZomBear/GiantZombear.prefab 挂 EnemyRegen（cooldown 0.1/0.05s × regenAmount 1，prefab 序列化）
            new(EnemyKind.ZomBear,        200, 100, 2.5f, AttackKind.Contact,     15, ContactRange, 0.5f, false, 15, 1.5f,  6.0f,  15, 1, 1,  0f,  true, 0.1f,  1),
            new(EnemyKind.Hellephant,     300, 100, 2.0f, AttackKind.Contact,     20, ContactRange, 0.5f, false, 12, 2.0f,  7.0f,  10, 1, 1,  0f),
            new(EnemyKind.GiantZombunny,  500, 100, 2.2f, AttackKind.Contact,     25, ContactRange, 0.5f, false,  8, 2.5f,  8.0f,   6, 1, 1, 10f),
            new(EnemyKind.GiantZomBear,   700, 100, 1.8f, AttackKind.Contact,     30, ContactRange, 0.5f, false,  6, 3.0f,  9.0f,   5, 1, 1, 20f, true, 0.05f, 1),
            new(EnemyKind.GiantHellephant,900, 100, 1.5f, AttackKind.Contact,     35, ContactRange, 0.5f, false,  4, 3.5f, 10.0f,   4, 1, 1, 30f),
            new(EnemyKind.Titan,         2000, 500, 1.2f, AttackKind.GroundTarget, 60, 15f,         3.0f, true,  2, 5.0f, 12.0f,   1, 1, 1, 45f), // 首领：BlastImmunity + 地面锁定 AOE（契约 §3 序 5，M2）
            new(EnemyKind.Clown,          100, 100, 2.8f, AttackKind.Fireball,    40, 25f,         1.5f, false,  8, 2.0f,  7.0f,   4, 1, 1, 15f), // 远程（契约 §3 序 4，M2）
            new(EnemyKind.MiniClown,       20,  50, 4.0f, AttackKind.Contact,      5, ContactRange, 0.5f, false,  0, 1.0f,  4.0f,  10, 2, 3,  0f), // 由 Clown 生成（M2）
            new(EnemyKind.ZomDuck,        100, 100, 4.5f, AttackKind.Contact,     10, ContactRange, 0.5f, false, 14, 1.0f,  5.0f,  12, 1, 2,  0f), // ZomDuck 系死亡自爆（M3：SelfDestruct/IBlast，见 ZomDuckBlast* 常量）
        };

        /// <summary>按类别取静态配置。</summary>
        public static KindStats Of(EnemyKind kind) => Table[(int)kind];
    }
}
