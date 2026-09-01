using System;
using Game.ECS;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人周期系统（契约 §3，全部 SystemSide.Server：Host 单机 = 全逻辑端，无头测试用 UpdateAll）。
    /// 扣血不走系统：enemy:damage 在能力实现内同步执行并当场发消息（契约 §0.5/§4）；
    /// 系统只做周期行为（生成/追击/接触伤害/下沉清理）。Ranged（Clown）/Boss（Titan）为 M2，本版不实现。
    /// 更新顺序（按注册序）：Spawn → Chase → Attack → Health(死亡清理)。
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

        /// <summary>生成一个敌人实体（归属本 ModObject，卸载强制回收 §9.2）+ 视图静态桥通知（契约 §8）。</summary>
        private void SpawnEnemy(SystemContext ctx, EnemyKind kind, float x, float y, float z)
        {
            var st = EnemyConfig.Of(kind);
            var e = _context.Ecs.CreateEntity();
            ctx.World.Add(e, new EnemyType { Kind = (byte)kind });
            ctx.World.Add(e, new EnemyHealth { Current = st.Hp, Max = st.Hp, ScoreValue = st.ScoreValue });
            ctx.World.Add(e, new EnemyFlags { BlastImmunity = st.BlastImmunity });
            ctx.World.Add(e, new EnemyAttack
            {
                Kind = (byte)st.Attack,
                Damage = st.AttackDamage,
                Range = st.AttackRange,
                Interval = st.AttackInterval,
                Cooldown = 0f,
            });
            ctx.World.Add(e, new EnemyMovement { Speed = st.Speed, SpeedMul = 1f, EffectTimer = 0f });
            ctx.World.Add(e, new EnemyTarget { PlayerEntityId = EnemyMod.PlayerEntityId });
            ctx.World.Add(e, new EnemyPosition { X = x, Y = y, Z = z });
            EnemyMod.NotifyEnemySpawned(e.Id); // 宿主 EnemySpawnerView 实例化 prefab 并绑定 EntityId
        }
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
    /// Fireball/GroundTarget 类型在此不处理（Clown 火球 / Titan 地面锁定，M2，契约 §3 序 4/5）。
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

                var a = attack;
                a.Cooldown -= ctx.DeltaTime;

                if (a.Kind != (byte)AttackKind.Contact)
                {
                    // 远程/首领类别交给 Ranged/Boss（M2），接触系统只结算 Contact
                    ctx.World.Add(e, a);
                    continue;
                }
                if (a.Cooldown > 0f)
                {
                    ctx.World.Add(e, a);
                    continue;
                }

                var dx = player.Value.X - pos.X;
                var dz = player.Value.Z - pos.Z;
                if (dx * dx + dz * dz <= a.Range * a.Range)
                {
                    a.Cooldown = a.Interval;
                    CallPlayerDamage(e.Id, a.Damage); // player:damage([amount, source])，契约 §3 序 3 / player §3
                }
                ctx.World.Add(e, a);
            }
        }

        private void CallPlayerDamage(uint source, int amount)
        {
            try
            {
                _context.Mods.Call(EnemyMod.PlayerModId, EnemyMod.PlayerDamageCap,
                    DataCodec.Write(new object?[] { amount, source }));
            }
            catch (Exception)
            {
                // 玩家未加载（卸载竞态）：本次攻击落空，不中断系统
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
}
