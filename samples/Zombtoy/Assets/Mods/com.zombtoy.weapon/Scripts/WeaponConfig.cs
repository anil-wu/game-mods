namespace Com.Zombtoy.Weapon
{
    /// <summary>
    /// 武器行为参数（契约 §9 配置常量表；结构固定、数值可调，改动需升 CONTRACT 版本与测试向量，summary §0.8）。
    /// 数值来源（只读机制数值，不搬代码）：原版 PlayerShooting.cs（射线 60/0.015/100）、
    /// Ammo.cs（弹匣/备弹/换弹）、Rocket.cs（直击/内圈/外圈爆炸）、IceBullet.cs（追踪/缓速）、
    /// Tornado.cs（持续伤害 tick + 拉拽）。默认配置在资源迁移时按 prefab 校准（summary §0.8）。
    /// </summary>
    public static class WeaponConfig
    {
        /// <summary>武器槽位数（契约 §9：机关枪/霰弹/火箭筒/冰手枪 ×4）。</summary>
        public const int SlotCount = 4;

        /// <summary>枪口高度（玩家脚底上方；投射物生成原点，契约 §8 视图约定对齐射线原点）。</summary>
        public const float MuzzleHeight = 1.0f;

        /// <summary>
        /// 每槽位武器参数（契约 §9 表：Damage/Interval/Range/MagSize/StartReserve/ReloadTime/AmmoPerShot）。
        /// MaxReserve 为 add_ammo 备弹封顶（契约 §4：Reserve=min(MaxReserve, Reserve+MaxMag*magazines)），
        /// 默认取 StartReserve×2（结构固定、数值可在资源迁移时校准）。
        /// </summary>
        public readonly struct WeaponDef
        {
            public readonly WeaponKind Kind;
            public readonly string DisplayName;
            public readonly int Damage;         // 每弹丸伤害
            public readonly float Interval;     // 开火间隔（冷却）
            public readonly float Range;        // 射线射程
            public readonly int MagSize;        // 弹匣容量（MaxMag）
            public readonly int StartReserve;   // 初始备弹
            public readonly int MaxReserve;     // 备弹上限（add_ammo 封顶）
            public readonly float ReloadTime;   // 换弹时长
            public readonly int AmmoPerShot;    // 每次开火消耗

            public WeaponDef(WeaponKind kind, string name, int damage, float interval, float range,
                int magSize, int startReserve, float reloadTime, int ammoPerShot)
            {
                Kind = kind;
                DisplayName = name;
                Damage = damage;
                Interval = interval;
                Range = range;
                MagSize = magSize;
                StartReserve = startReserve;
                MaxReserve = startReserve * 2;
                ReloadTime = reloadTime;
                AmmoPerShot = ammoPerShot;
            }
        }

        /// <summary>槽位表（索引 = 槽位号 0..3，契约 §9）。</summary>
        public static readonly WeaponDef[] Slots =
        {
            new(WeaponKind.MachineGun,     "Machine Gun",     60, 0.015f, 100, 25, 50, 1.5f, 1), // 射线（原值 60/0.015/100）
            new(WeaponKind.Shotgun,        "Shotgun",         10, 0.45f,  100,  8, 24, 1.0f, 5), // 10×5（每枪 5 条射线各 10）
            new(WeaponKind.RocketLauncher, "Rocket Launcher", 100, 1.0f,   60,   1,  6, 2.0f, 1), // 直击 100 / 爆炸 AOE（见下）
            new(WeaponKind.CryoPistol,     "Cryo Pistol",     30, 0.4f,    80,  10, 40, 1.2f, 1), // 追踪弹 + 缓速（见下）
        };

        /// <summary>按武器类别取参数（Tornado 副手无槽位表，单独常量）。</summary>
        public static WeaponDef Of(WeaponKind kind)
        {
            foreach (var def in Slots)
                if (def.Kind == kind) return def;
            return Slots[0]; // 防御兜底（不可达）
        }

        // ---- 火箭筒（契约 §9 行 2：直击 100 / 内圈 80@3m / 外圈 40@6m，爆炸 sourceKind=Blast；Titan 免疫由 enemy 裁决） ----

        /// <summary>飞行速度（原版 Rocket.speed=3.5）。</summary>
        public const float RocketSpeed = 3.5f;

        /// <summary>直击伤害（契约 §9：直击 100）。</summary>
        public const int RocketDirectDamage = 100;

        /// <summary>内圈伤害与半径（契约 §9：内圈 80@3m）。</summary>
        public const int RocketInnerDamage = 80;
        public const float RocketInnerRadius = 3f;

        /// <summary>外圈伤害与半径（契约 §9：外圈 40@6m）。</summary>
        public const int RocketOuterDamage = 40;
        public const float RocketOuterRadius = 6f;

        /// <summary>命中判定半径（原版 SphereCast 0.3 的 XZ 平面逻辑等价值）。</summary>
        public const float RocketCollisionRadius = 1.0f;

        /// <summary>未命中自毁时间（原版 Destroy(gameObject, 30f)）。</summary>
        public const float RocketLife = 30f;

        // ---- 冰手枪（契约 §9 行 3：30 伤害、追踪弹 + 缓速 40%/1.5s，M2） ----

        /// <summary>飞行速度（原版 IceBullet.speed=5）。</summary>
        public const float IceSpeed = 5f;

        /// <summary>命中判定半径。 </summary>
        public const float IceHitRadius = 0.5f;

        /// <summary>未命中自毁时间。</summary>
        public const float IceLife = 10f;

        /// <summary>缓速倍率与时长（原版 SlowEffect(0.6f)/SlowEffect_Duration(1.5f)，契约 §9 缓速 40%/1.5s）。</summary>
        public const float IceSlowMul = 0.6f;
        public const float IceSlowDuration = 1.5f;

        // ---- 龙卷风（契约 §9 行 4，副手特殊能力：8/tick(0.2s) + 外圈 2，拉拽 + 持续伤害，CD 30s，M2） ----

        /// <summary>副手冷却（契约 §9：CD 30s）。</summary>
        public const float TornadoCooldown = 30f;

        /// <summary>移动速度（原版 Tornado.speed=4）。</summary>
        public const float TornadoSpeed = 4f;

        /// <summary>持续时长（原版 Tornado.lifespan=4.7）。</summary>
        public const float TornadoLifespan = 4.7f;

        /// <summary>内圈半径（契约 §9：4m）与外圈半径（契约 §9：10m）。</summary>
        public const float TornadoInnerRadius = 4f;
        public const float TornadoOuterRadius = 10f;

        /// <summary>持续伤害：内圈 8/tick(0.2s)、外圈 2（契约 §9）。</summary>
        public const int TornadoTickDamage = 8;
        public const int TornadoOuterDamage = 2;
        public const float TornadoTickInterval = 0.2f;
    }
}
