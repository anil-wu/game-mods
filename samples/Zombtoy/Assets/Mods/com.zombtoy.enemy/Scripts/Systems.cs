using System;
using Game.ECS;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人周期系统（契约 §3，全部 SystemSide.Server：Host 单机 = 全逻辑端，无头测试用 UpdateAll）。
    /// 扣血不走系统：enemy:damage 在能力实现内同步执行并当场发消息（契约 §0.5/§4）；
    /// 系统只做周期行为（生成/追击/接触伤害/远程火球/首领锁定/下沉清理）。
    /// 更新顺序（按注册序）：Spawn → Chase → Attack(接触) → Ranged(Clown 火球+投射物) → Boss(Titan 地面锁定)
    /// → Health(死亡清理) → Aux(M3：回血/Clown 延迟召唤，序 7)。
    /// </summary>

    /// <summary>
    /// 生成器（序 1）：加权生成表 + 每类型冷却/startBuffer + 每类型并发上限 + 全局并发上限 50
    /// + 间隔衰减（3s→×0.99/次→下限 0.6s）；仅在 Enabled 且玩家存活时生成；生成即发 ZombieCountChanged。
    /// 状态：EnemySpawner 组件（单实体）+ 系统内每类型冷却（session 时钟，暂停时冻结）。
    /// </summary>
    public sealed class EnemySpawnSystem : ISystem
    {
        private readonly IModContext _context;
        private readonly System.Random _rng;
        private readonly float[] _startBuffer;      // 每类型 startBufferTime（Register/reset 时重灌）
        private readonly float[] _nextAvailableAt;  // 每类型可生成时刻（session 时钟：startBuffer + 每次生成后随机 min..max 冷却）
        private float _sessionTime;                 // 启用累计时钟（set_spawning(false)/玩家死亡时冻结）

        public EnemySpawnSystem(IModContext context)
        {
            _context = context;
            _rng = new System.Random();
            _startBuffer = new float[EnemyConfig.Table.Length];
            _nextAvailableAt = new float[EnemyConfig.Table.Length];
            for (var k = 0; k < _startBuffer.Length; k++)
            {
                _startBuffer[k] = EnemyConfig.Table[k].StartBufferTime;
                _nextAvailableAt[k] = _startBuffer[k];
            }
        }

        /// <summary>会话复位（enemy:reset 调用：时钟归零、每类型缓冲/冷却重灌，契约 §4）。</summary>
        public void ResetSession()
        {
            _sessionTime = 0f;
            for (var k = 0; k < _nextAvailableAt.Length; k++)
                _nextAvailableAt[k] = _startBuffer[k];
        }

        /// <summary>
        /// 权重抽样核心（表驱动，契约 §9 生成器加权表）：从全部权重&gt;0 的类型按权重抽取。
        /// 公开静态：系统内可用性过滤后调用；测试直接验证权重分布（无需依赖完整生成流程）。
        /// </summary>
        public static EnemyKind RollWeighted(System.Random rng)
        {
            var total = 0;
            foreach (var st in EnemyConfig.Table) total += st.SpawnWeight;
            var roll = rng.Next(total);
            var acc = 0;
            foreach (var st in EnemyConfig.Table)
            {
                acc += st.SpawnWeight;
                if (roll < acc) return st.Kind;
            }
            return EnemyConfig.Table[0].Kind; // 防御兜底（权重和>0 时不可达）
        }

        public void Update(SystemContext ctx)
        {
            if (EnemyMod.SpawnerEntityId == 0) return;
            var se = new Entity(EnemyMod.SpawnerEntityId);
            if (!ctx.World.TryGet<EnemySpawner>(se, out var spawner)) return;

            // 开关（set_spawning，契约 §4）：关闭时 Timer 冻结 = 暂停
            if (!spawner.Enabled) return;

            // 仅在玩家存活时生成（契约 §3 序 1：player:get_position 的 alive）
            var player = EnemyMod.TryGetPlayerPosition(_context);
            if (player is null || !player.Value.Alive) return;

            _sessionTime += ctx.DeltaTime;

            // 全局并发上限 50（契约 §3 序 1）
            if (spawner.ActiveCount >= EnemyConfig.MaxTotalEnemies) return;

            spawner.Timer -= ctx.DeltaTime;
            if (spawner.Timer > 0f)
            {
                ctx.World.Add(se, spawner);
                return;
            }

            // 加权选型（可用性过滤：每类型冷却 + startBuffer + 每类型并发上限）
            var kind = PickWeightedKind(ctx.World);
            if (kind is null)
            {
                spawner.Timer = EnemyConfig.RetryInterval; // 本 tick 无可用类型 → 稍后重试
                ctx.World.Add(se, spawner);
                return;
            }

            var st = EnemyConfig.Of(kind.Value);
            var groupSize = st.GroupMax > st.GroupMin
                ? st.GroupMin + _rng.Next(st.GroupMax - st.GroupMin + 1)
                : st.GroupMin;

            var spawned = 0;
            for (var i = 0; i < groupSize && spawner.ActiveCount + spawned < EnemyConfig.MaxTotalEnemies; i++)
            {
                var (px, py, pz) = EnemyConfig.SpawnPoints[_rng.Next(EnemyConfig.SpawnPoints.Length)];
                if (i > 0) // 组内抖动（原版 jitter）
                {
                    px += (float)(_rng.NextDouble() * 6.0 - 3.0);
                    pz += (float)(_rng.NextDouble() * 6.0 - 3.0);
                }
                SpawnEnemy(ctx, kind.Value, px, py, pz);
                spawned++;
            }
            if (spawned == 0)
            {
                spawner.Timer = EnemyConfig.RetryInterval;
                ctx.World.Add(se, spawner);
                return;
            }

            // 每类型冷却：本次生成后随机 min..max 秒（原版 nextAvailableTime，契约 §3 序 1）
            var wait = st.MinSpawnTime + (float)_rng.NextDouble() * Math.Max(0f, st.MaxSpawnTime - st.MinSpawnTime);
            _nextAvailableAt[(int)kind.Value] = _sessionTime + wait;

            // 全局间隔衰减（3s → ×0.99/次 → 下限 0.6s）
            spawner.ActiveCount += spawned;
            spawner.SpawnInterval = Math.Max(EnemyConfig.MinSpawnTime, spawner.SpawnInterval * EnemyConfig.SpawnTimeReduction);
            spawner.Timer = spawner.SpawnInterval;
            ctx.World.Add(se, spawner);

            // 生成即发 ZombieCountChanged（契约 §3 序 1 / §5）
            EnemyMod.PublishZombieCount(spawner.ActiveCount);
        }

        /// <summary>可用性过滤后的加权选型：返回 null = 全部类型冷却中或达并发上限。</summary>
        private EnemyKind? PickWeightedKind(World world)
        {
            var available = new bool[EnemyConfig.Table.Length];
            var total = 0;
            for (var k = 0; k < available.Length; k++)
            {
                var st = EnemyConfig.Table[k];
                if (st.SpawnWeight <= 0) continue;
                if (_sessionTime < _nextAvailableAt[k]) continue;          // 每类型冷却 / startBuffer
                if (AliveCountOf(world, st.Kind) >= st.MaxConcurrent) continue; // 每类型并发上限
                available[k] = true;
                total += st.SpawnWeight;
            }
            if (total <= 0) return null;

            var roll = _rng.Next(total);
            var acc = 0;
            for (var k = 0; k < available.Length; k++)
            {
                if (!available[k]) continue;
                acc += EnemyConfig.Table[k].SpawnWeight;
                if (roll < acc) return EnemyConfig.Table[k].Kind;
            }
            return null;
        }

        /// <summary>某类型存活（未死亡）敌人数量（下沉尸体不计入，与 ActiveCount 口径一致）。</summary>
        private static int AliveCountOf(World world, EnemyKind kind)
        {
            var n = 0;
            foreach (var (id, type) in world.Store<EnemyType>().All())
            {
                if (type.Kind != (byte)kind) continue;
                var e = new Entity(id);
                if (!world.TryGet<EnemyFlags>(e, out var flags) || !flags.IsDead) n++;
            }
            return n;
        }

        /// <summary>生成一个敌人实体：复用 EnemyMod.BuildEnemyEntity（生成器/辅助召唤共用，任务：复用生成器逻辑/实体）。
        /// 视图静态桥通知含在其内（宿主 EnemySpawnerView 实例化 prefab 并绑定 EntityId，契约 §8）；
        /// 计数由调用方（生成器批次 / EnemyAuxSystem 召唤）自行维护，本方法不发布消息。</summary>
        private void SpawnEnemy(SystemContext ctx, EnemyKind kind, float x, float y, float z)
            => EnemyMod.BuildEnemyEntity(kind, x, y, z);
    }

    /// <summary>
    /// 追击（序 2）：每帧取玩家位置（player:get_position）→ 计算敌人期望速度（SpeedMul 缓速生效，
    /// EffectTimer 衰减恢复）→ 写 EnemyMovement；逻辑位置向玩家积分（无头测试/逻辑权威），
    /// 视图 NavMesh 按 EnemyMovement 驱动并回写实际位置（契约 §2）。IsDead 不再追击（契约 §3 序 6）。
    /// </summary>
    public sealed class EnemyChaseSystem : ISystem
    {
        private readonly IModContext _context;

        public EnemyChaseSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            var player = EnemyMod.TryGetPlayerPosition(_context); // 每帧取一次（追击目标点 + 存活判定）

            foreach (var (e, pos, mv, flags) in ctx.Query<EnemyPosition, EnemyMovement, EnemyFlags>())
            {
                if (flags.IsDead) continue; // 死亡后不再追击

                var movement = mv;
                // 缓速状态衰减恢复（契约 §4 apply_slow：SpeedMul/EffectTimer 由能力写入，系统每帧结算）
                if (movement.EffectTimer > 0f)
                {
                    movement.EffectTimer -= ctx.DeltaTime;
                    if (movement.EffectTimer <= 0f)
                    {
                        movement.EffectTimer = 0f;
                        movement.SpeedMul = 1f;
                    }
                }

                if (player is null || !player.Value.Alive)
                {
                    ctx.World.Add(e, movement); // 玩家死亡/未获取：停追（速度写回、位置不动）
                    continue;
                }

                var dx = player.Value.X - pos.X;
                var dz = player.Value.Z - pos.Z;
                var dist = (float)Math.Sqrt(dx * dx + dz * dz);
                var np = pos;
                if (dist > 0.001f)
                {
                    var step = movement.Speed * movement.SpeedMul * ctx.DeltaTime;
                    if (step >= dist)
                    {
                        np.X = player.Value.X;
                        np.Z = player.Value.Z;
                    }
                    else
                    {
                        np.X += dx / dist * step;
                        np.Z += dz / dist * step;
                    }
                }
                ctx.World.Add(e, np);
                ctx.World.Add(e, movement);
            }
        }
    }

    /// <summary>
    /// 接触伤害（序 3）：EnemyFlags 存活 + 与玩家距离 ≤ Range + 冷却到 → 调 player:damage（二进制 ModCall）。
    /// 只处理 AttackKind.Contact；Fireball/GroundTarget 由 Ranged/Boss 系统各自结算（契约 §3 序 3/4/5），
    /// 本系统不触碰它们的 Cooldown（避免双系统同字段递减）。
    /// </summary>
    public sealed class EnemyAttackSystem : ISystem
    {
        private readonly IModContext _context;

        public EnemyAttackSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            var player = EnemyMod.TryGetPlayerPosition(_context); // 每帧取一次玩家位置（距离判定）
            if (player is null || !player.Value.Alive) return;

            foreach (var (e, attack, pos, flags) in ctx.Query<EnemyAttack, EnemyPosition, EnemyFlags>())
            {
                if (flags.IsDead) continue;
                if (attack.Kind != (byte)AttackKind.Contact) continue; // 远程/首领交给 Ranged/Boss（序 4/5）

                var a = attack;
                a.Cooldown -= ctx.DeltaTime;
                if (a.Cooldown <= 0f)
                {
                    var dx = player.Value.X - pos.X;
                    var dz = player.Value.Z - pos.Z;
                    if (dx * dx + dz * dz <= a.Range * a.Range)
                    {
                        a.Cooldown = a.Interval;
                        EnemyMod.DamagePlayer(e.Id, a.Damage); // player:damage([amount, source])，契约 §3 序 3 / player §3
                    }
                }
                ctx.World.Add(e, a);
            }
        }
    }

    /// <summary>
    /// 远程火球（序 4，Clown，AttackKind.Fireball）：射程内冷却到 → 生成 EnemyProjectile 逻辑实体
    /// （契约 §2/§3 序 4）+ 视图请求（静态桥）；投射物直线飞行（原版 EnemyProjectile 无追踪、穿墙），
    /// 命中玩家 → player:damage(开火时固化的 Damage)；超时（原版 10s）或命中后销毁实体并通知视图。
    /// 本系统同时扮演投射物模拟系统（契约 §3 序 4 职责含"投射物命中逻辑"，注册序即契约表序）。
    /// </summary>
    public sealed class EnemyRangedSystem : ISystem
    {
        private readonly IModContext _context;

        public EnemyRangedSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            var player = EnemyMod.TryGetPlayerPosition(_context); // 每帧取一次（开火判定 + 投射物命中判定）

            // 阶段一：Fireball 类敌人（Clown）开火
            if (player is not null && player.Value.Alive)
            {
                foreach (var (e, attack, pos, flags) in ctx.Query<EnemyAttack, EnemyPosition, EnemyFlags>())
                {
                    if (flags.IsDead) continue;
                    if (attack.Kind != (byte)AttackKind.Fireball) continue;

                    var a = attack;
                    a.Cooldown -= ctx.DeltaTime;
                    var dx = player.Value.X - pos.X;
                    var dz = player.Value.Z - pos.Z;
                    if (a.Cooldown <= 0f && dx * dx + dz * dz <= a.Range * a.Range)
                    {
                        a.Cooldown = a.Interval;
                        SpawnFireball(ctx, e.Id, a.Damage, pos, player.Value);
                    }
                    ctx.World.Add(e, a);
                }
            }

            // 阶段二：已有投射物直线飞行 / 命中 / 超时（与玩家死亡无关，飞行照常直到超时/命中）
            foreach (var (e, p) in ctx.Query<EnemyProjectile>())
            {
                var proj = p;
                proj.Lifetime -= ctx.DeltaTime;
                if (proj.Lifetime <= 0f)
                {
                    // 超时销毁（原版 Destroy(gameObject, 10f)：未命中自然消散）
                    EnemyMod.NotifyProjectileDestroyed(e.Id);
                    ctx.World.Destroy(e);
                    continue;
                }
                proj.X += proj.DirX * proj.Speed * ctx.DeltaTime;
                proj.Y += proj.DirY * proj.Speed * ctx.DeltaTime;
                proj.Z += proj.DirZ * proj.Speed * ctx.DeltaTime;

                // 命中玩家（直线无追踪；命中判距见 EnemyConfig.ProjectileHitRadius，原版 OverlapSphere 0.6 近似）
                if (player is not null && player.Value.Alive)
                {
                    var hx = player.Value.X - proj.X;
                    var hz = player.Value.Z - proj.Z;
                    if (hx * hx + hz * hz <= EnemyConfig.ProjectileHitRadius * EnemyConfig.ProjectileHitRadius)
                    {
                        EnemyMod.DamagePlayer(proj.Source, proj.Damage); // 命中 → player:damage（契约 §3 序 4）
                        EnemyMod.NotifyProjectileDestroyed(e.Id); // 命中销毁视图
                        ctx.World.Destroy(e);
                        continue;
                    }
                }
                ctx.World.Add(e, proj);
            }
        }

        /// <summary>生成一个火球投射物实体（归属本 ModObject）+ 视图请求（契约 §3 序 4 / §8）。
        /// 出膛点 = 敌人头顶出膛高度（EnemyConfig.ProjectileHeight），方向 = 水平指向玩家开火时位置（无追踪）。</summary>
        private void SpawnFireball(SystemContext ctx, uint source, int damage, in EnemyPosition from,
            in (float X, float Y, float Z, bool Alive) target)
        {
            var dx = target.X - from.X;
            var dz = target.Z - from.Z;
            var dist = (float)Math.Sqrt(dx * dx + dz * dz);
            if (dist < 0.001f) { dx = 0f; dz = 1f; dist = 1f; } // 玩家在脚下时向上方向退化（防御）

            var e = _context.Ecs.CreateEntity();
            ctx.World.Add(e, new EnemyProjectile
            {
                Kind = (byte)EnemyProjectileKind.Fireball,
                Damage = damage,
                Source = source,
                Speed = EnemyConfig.FireballSpeed,
                Lifetime = EnemyConfig.ProjectileLifetime,
                X = from.X,
                Y = EnemyConfig.ProjectileHeight,
                Z = from.Z,
                DirX = dx / dist,
                DirY = 0f,
                DirZ = dz / dist,
            });
            EnemyMod.NotifyProjectileSpawned(e.Id, (byte)EnemyProjectileKind.Fireball);
        }
    }

    /// <summary>
    /// 首领地面锁定（序 5，Titan，AttackKind.GroundTarget）：射程内准星（独立 decal，不复刻原版 groundTarget
    /// 绑地板/Floor-collider 的 bug）向玩家 XZ lerp 追踪 + 蓄力（蓄力时长 = Attack.Interval，原版 cooldown
    /// 语义：准星锁定期间到点即射）→ 蓄力完成在落点做 AOE：玩家距落点 ≤ BossAoeRadius（原版火箭爆炸半径 3.0）
    /// → 调 player:damage；并通知视图发射 EnemyRocket Variant 视觉火箭（契约 §3 序 5）。
    /// 玩家跑出 AOE 圈可躲（对齐原版"火箭落点可躲避"）；出射程/玩家死亡 → 停止追踪与蓄力（准星隐藏）。
    /// </summary>
    public sealed class EnemyBossSystem : ISystem
    {
        private readonly IModContext _context;

        public EnemyBossSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            var player = EnemyMod.TryGetPlayerPosition(_context);
            var alive = player is not null && player.Value.Alive;

            foreach (var (e, attack, pos, flags) in ctx.Query<EnemyAttack, EnemyPosition, EnemyFlags>())
            {
                if (flags.IsDead) continue;
                if (attack.Kind != (byte)AttackKind.GroundTarget) continue;

                // 锁定状态懒建：初始落点 = 敌人当前位置（准星从敌人脚下起步 lerp，原版 groundTarget 起始姿态）
                var st = ctx.World.TryGet<EnemyBossLock>(e, out var s)
                    ? s
                    : new EnemyBossLock { X = pos.X, Z = pos.Z };

                var inRange = false;
                if (alive)
                {
                    var rx = player!.Value.X - pos.X;
                    var rz = player.Value.Z - pos.Z;
                    inRange = rx * rx + rz * rz <= attack.Range * attack.Range;
                }
                st.Tracking = inRange; // 准星只在射程内（原版 targetActive = Range.inRange）

                if (inRange)
                {
                    // 蓄力 + 准星 lerp 追踪玩家（原版 EnemyTargetShooting：Lerp(groundTarget, playerPos, dt*5)）
                    st.Charge += ctx.DeltaTime;
                    var t = Math.Min(1f, ctx.DeltaTime * EnemyConfig.BossLockLerpRate);
                    st.X += (player!.Value.X - st.X) * t;
                    st.Z += (player.Value.Z - st.Z) * t;

                    if (st.Charge >= attack.Interval)
                    {
                        st.Charge = 0f; // 蓄力完成 → 落点 AOE（契约 §3 序 5）
                        var ax = player.Value.X - st.X;
                        var az = player.Value.Z - st.Z;
                        if (ax * ax + az * az <= EnemyConfig.BossAoeRadius * EnemyConfig.BossAoeRadius)
                            EnemyMod.DamagePlayer(e.Id, attack.Damage); // 玩家仍在落点 AOE 圈内 → 伤害
                        EnemyMod.NotifyBossStrike(e.Id, st.X, st.Z); // 视图：EnemyRocket Variant 视觉火箭（即使玩家躲开也表现）
                    }
                }
                ctx.World.Add(e, st);
            }
        }
    }

    /// <summary>
    /// 死亡后置（序 6）：IsSinking 下沉计时 → 到点销毁实体 + 通知视图销毁（视图下沉动画由 EnemyView 处理，契约 §8）。
    /// 扣血/致死标记在 enemy:damage 能力内同步完成（契约 §3 注 / §0.5），本系统只做下沉清理。
    /// </summary>
    public sealed class EnemyHealthSystem : ISystem
    {
        private readonly IModContext _context;

        public EnemyHealthSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            foreach (var (e, flags, _) in ctx.Query<EnemyFlags, EnemyHealth>())
            {
                if (!flags.IsDead || !flags.IsSinking) continue;

                var f = flags;
                f.SinkTimer -= ctx.DeltaTime;
                if (f.SinkTimer <= 0f)
                {
                    // 下沉到点：销毁实体 + 通知视图销毁（ComponentStore 快照迭代，销毁安全）
                    EnemyMod.NotifyEnemyDestroyed(e.Id);
                    ctx.World.Destroy(e);
                    continue;
                }
                ctx.World.Add(e, f);
            }
        }
    }

    /// <summary>
    /// 敌人辅助行为（序 7，M3 任务追加；挂在 Health 之后）。两类计时行为：
    /// 1) 回血（原版 EnemyRegen.cs，ZomBear/GiantZombear prefab 挂组件）：EnemyRegen 倒计时到点
    ///    → 按类型 +RegenAmount（ZomBear 0.1s/1、GiantZomBear 0.05s/1），封顶 EnemyHealth.Max、
    ///    IsDead 不结算（原版 Regenerate：currentHealth &lt; startingHealth &amp;&amp; !isDead）；
    /// 2) Clown 死亡延迟召唤 MiniClown（原版 SpawnClown.cs Invoke("Spawn",1f)）：死亡 Clown 上的
    ///    EnemyPendingMiniClowns 倒计时到点 → 按槽位生成（EnemyMod.BuildEnemyEntity 共用生成器产出路径），
    ///    每生成一只 ActiveCount+1 并发 ZombieCountChanged（任务：计数纳入）；全局并发上限 50 满时丢弃
    ///    本次剩余召唤（防御性资源上限；原版无并发概念，本 Mod 生成器上限语义同上限一致）。
    /// 死亡触发（ZomDuck 自爆/Clown 挂待召唤组件）在 enemy:damage 致死分支同步完成（契约 §0.5），本系统只做计时结算。
    /// </summary>
    public sealed class EnemyAuxSystem : ISystem
    {
        private readonly IModContext _context;

        public EnemyAuxSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            TickRegen(ctx);
            TickPendingMiniClowns(ctx);
        }

        /// <summary>回血计时：EnemyRegen 组件存在才结算（原版只有带组件的类型回血）。</summary>
        private static void TickRegen(SystemContext ctx)
        {
            foreach (var (e, regen, hp, flags, type) in ctx.Query<EnemyRegen, EnemyHealth, EnemyFlags, EnemyType>())
            {
                if (flags.IsDead) continue;
                var cfg = EnemyConfig.Of((EnemyKind)type.Kind);
                if (!cfg.HasRegen || hp.Current >= hp.Max) continue; // 满血不回（原版 currentHealth < startingHealth 门）

                var r = regen;
                r.Timer -= ctx.DeltaTime;
                var heal = 0;
                var guarded = 0;
                while (r.Timer <= 0f && hp.Current + heal < hp.Max && guarded++ < 64)
                {
                    heal += cfg.RegenAmount;
                    r.Timer += cfg.RegenInterval;
                    if (cfg.RegenInterval <= 0f) break; // 防御（配置错误：不死循环）
                }
                if (heal > 0)
                {
                    var h = hp;
                    h.Current = Math.Min(hp.Max, hp.Current + heal);
                    ctx.World.Add(e, h);
                }
                ctx.World.Add(e, r);
            }
        }

        /// <summary>Clown 死亡召唤计时：到点按槽位生成 MiniClown（快照迭代，先摘组件再生成）。</summary>
        private void TickPendingMiniClowns(SystemContext ctx)
        {
            foreach (var (e, pending) in ctx.Query<EnemyPendingMiniClowns>())
            {
                var p = pending;
                p.Timer -= ctx.DeltaTime;
                if (p.Timer > 0f)
                {
                    ctx.World.Add(e, p);
                    continue;
                }

                ctx.World.Remove<EnemyPendingMiniClowns>(e); // 先摘：到点即结算一次（防跨帧重复）
                var spawned = 0;
                spawned += SpawnSlot(ctx, p.PendingA, p.XA, p.ZA);
                spawned += SpawnSlot(ctx, p.PendingB, p.XB, p.ZB);
                if (spawned > 0) PublishAuxSpawnCount(ctx); // 批量生成后统一计数发布（一次消息，契约 §5）
            }
        }

        /// <summary>单槽位生成（全局并发上限 50 门控；返回本槽实际生成数）。</summary>
        private static int SpawnSlot(SystemContext ctx, int pending, float x, float z)
        {
            if (pending <= 0) return 0;
            var se = new Entity(EnemyMod.SpawnerEntityId);
            if (!ctx.World.TryGet<EnemySpawner>(se, out var sp)) return 0;

            var spawned = 0;
            for (var i = 0; i < pending; i++)
            {
                if (sp.ActiveCount + spawned >= EnemyConfig.MaxTotalEnemies) break; // 并发满：丢弃剩余（防御上限）
                EnemyMod.BuildEnemyEntity(EnemyKind.MiniClown, x, 0f, z); // 原版出生 y 强制 0（ClownSpawnPoint.y=0）
                spawned++;
            }
            if (spawned == 0) return 0;

            sp.ActiveCount += spawned;
            ctx.World.Add(se, sp);
            return spawned;
        }

        /// <summary>本次召唤完成后发布一次 ZombieCountChanged（计数纳入，契约 §5；生成即计数，同生成器口径）。</summary>
        private static void PublishAuxSpawnCount(SystemContext ctx)
        {
            var se = new Entity(EnemyMod.SpawnerEntityId);
            if (!ctx.World.TryGet<EnemySpawner>(se, out var sp)) return;
            EnemyMod.PublishZombieCount(sp.ActiveCount);
        }
    }
}
