using System;
using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人 Mod（com.zombtoy.enemy）：10 种类型 + 加权生成器 + 追击/接触伤害 + 远程火球（Clown，M2）
    /// + 首领地面锁定（Titan，M2）+ 生命/死亡 + 计数 + M3 辅助行为（ZomDuck 死亡自爆 / Clown 召唤 MiniClown /
    /// ZomBear 系回血）。
    /// 单机 Host（HasClient+HasServer）：逻辑全部注册为 SystemSide.Server（summary §0.1），
    /// 视图（MonoBehaviour）经静态桥与逻辑共享同一 World（同 player Mod 模式）。
    /// 扣血在 enemy:damage 能力内同步执行并当场发消息（契约 §4/§0.5）；系统只做周期行为（生成/追击/攻击/下沉清理）。
    /// 跨 Mod 能力调用（player:get_entity / player:get_position / player:damage）一律二进制 DataCodec（Rule 14）；
    /// 本 Mod 不引用 player 程序集，能力 ID 与行形状各自重复定义（Rule 12/19）。
    /// </summary>
    public sealed class EnemyMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.zombtoy.enemy");

        // 消息（谁定义谁发布，Rule 11；载荷字段布局见 CONTRACT.md §5）
        public static readonly MessageId EnemyKilledEvent = new(ModIdValue, "EnemyKilled");
        public static readonly MessageId ZombieCountChangedEvent = new(ModIdValue, "ZombieCountChanged");

        // 导出能力（契约 §4）
        public static readonly CapabilityId DamageCap = new(ModIdValue, "damage");
        public static readonly CapabilityId ApplySlowCap = new(ModIdValue, "apply_slow");
        public static readonly CapabilityId GetAllCap = new(ModIdValue, "get_all");
        public static readonly CapabilityId GetCountCap = new(ModIdValue, "get_count");
        public static readonly CapabilityId SetSpawningCap = new(ModIdValue, "set_spawning");
        public static readonly CapabilityId ResetCap = new(ModIdValue, "reset");

        // 消费的对端能力（契约 §7；不引用 player 程序集，Rule 12）
        public static readonly ModId PlayerModId = new("com.zombtoy.player");
        public static readonly CapabilityId PlayerGetEntityCap = new(PlayerModId, "get_entity");
        public static readonly CapabilityId PlayerGetPositionCap = new(PlayerModId, "get_position");
        public static readonly CapabilityId PlayerDamageCap = new(PlayerModId, "damage");

        // 静态桥（视图访问 / 测试，Mod 内部状态，FPS 先例）
        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;
        public static uint SpawnerEntityId { get; private set; }
        public static uint PlayerEntityId { get; private set; }

        private static EnemySpawnSystem? _spawnSystem;

        /// <summary>视图静态桥（契约 §8）：宿主 EnemySpawnerView 订阅实例化 prefab；EnemyView 销毁表现。</summary>
        public static event Action<uint>? OnEnemySpawned;
        public static event Action<uint>? OnEnemyKilled;
        public static event Action<uint>? OnEnemyDestroyed;

        /// <summary>投射物视图静态桥（契约 §3 序 4，M2）：宿主 EnemyProjectileView 订阅实例化原投射物 prefab。</summary>
        public static event Action<uint, byte>? OnProjectileSpawned;   // (投射物实体, EnemyProjectileKind)
        public static event Action<uint>? OnProjectileDestroyed;

        /// <summary>Boss 落点表现（契约 §3 序 5，M2）：(敌人实体, 落点X, 落点Z)——宿主 EnemyBossView 发射 Rocket Variant 视觉火箭。</summary>
        public static event Action<uint, float, float>? OnBossStrike;

        /// <summary>爆炸表现静态桥（契约 §3 序 7，M3 SelfDestruct）：(敌人实体, 爆炸点X, Y, Z)——
        /// 宿主 EnemyBlastView 实例化原 CFX_Explosion_B_Smoke+Text 爆炸 prefab 播一次。</summary>
        public static event Action<uint, float, float, float>? OnEnemyBlast;

        /// <summary>随机源（M3：Clown 召唤 MiniClown 的 15% +1 掷点，原版 Random.Range(0,100) ≤ 15）。</summary>
        private static readonly System.Random _rng = new();

        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;

            // 1. 组件注册（归属本 ModObject，卸载强制回收 §9.2；契约 §2）
            context.Ecs.RegisterComponent(typeof(EnemyType));
            context.Ecs.RegisterComponent(typeof(EnemyHealth));
            context.Ecs.RegisterComponent(typeof(EnemyFlags));
            context.Ecs.RegisterComponent(typeof(EnemyAttack));
            context.Ecs.RegisterComponent(typeof(EnemyMovement));
            context.Ecs.RegisterComponent(typeof(EnemyTarget));
            context.Ecs.RegisterComponent(typeof(EnemyPosition));
            context.Ecs.RegisterComponent(typeof(EnemySpawner));
            context.Ecs.RegisterComponent(typeof(EnemyProjectile)); // M2：远程火球（契约 §3 序 4）
            context.Ecs.RegisterComponent(typeof(EnemyBossLock));   // M2：Titan 地面锁定状态（契约 §3 序 5）
            context.Ecs.RegisterComponent(typeof(EnemyRegen));      // M3：回血（ZomBear 系，EnemyRegen.cs）
            context.Ecs.RegisterComponent(typeof(EnemyPendingMiniClowns)); // M3：Clown 死亡延迟召唤 MiniClown（SpawnClown.cs）

            // 2. 能力导出（§12.11；二进制 ABI Rule 14：DataCodec 编解码纯数据）
            context.Mods.Export(DamageCap, reader => DataCodec.Write(ApplyDamage(DataCodec.Read(reader))));
            context.Mods.Export(ApplySlowCap, reader => DataCodec.Write(ApplySlow(DataCodec.Read(reader))));
            context.Mods.Export(GetAllCap, _ => DataCodec.Write(GetAll()));
            context.Mods.Export(GetCountCap, _ => DataCodec.Write(GetCount()));
            context.Mods.Export(SetSpawningCap, reader => DataCodec.Write(SetSpawning(DataCodec.Read(reader))));
            context.Mods.Export(ResetCap, _ => DataCodec.Write(Reset()));

            if (context.HasServer)
            {
                // 3. 追击目标定位（契约 §7：player:get_entity，Register 时取一次）
                PlayerEntityId = FetchPlayerEntity();

                // 4. 系统（契约 §3 顺序：Spawn → Chase → Attack(接触) → Ranged(火球+投射物) → Boss(地面锁定)
                //    → Health(死亡清理) → Aux(M3：回血/延迟召唤)）
                _spawnSystem = new EnemySpawnSystem(context);
                context.Ecs.RegisterSystem(_spawnSystem, SystemSide.Server);
                context.Ecs.RegisterSystem(new EnemyChaseSystem(context), SystemSide.Server);
                context.Ecs.RegisterSystem(new EnemyAttackSystem(context), SystemSide.Server);
                context.Ecs.RegisterSystem(new EnemyRangedSystem(context), SystemSide.Server); // M2
                context.Ecs.RegisterSystem(new EnemyBossSystem(context), SystemSide.Server);   // M2
                context.Ecs.RegisterSystem(new EnemyHealthSystem(context), SystemSide.Server);
                context.Ecs.RegisterSystem(new EnemyAuxSystem(context), SystemSide.Server);    // M3

                // 5. 生成器实体（单实体状态机，契约 §2；Enabled 初始开：对齐原版 StartSpawning，
                //    game Mod 经 enemy:set_spawning 做相位开关，见 game CONTRACT §3）
                var spawner = context.Ecs.CreateEntity();
                context.Ecs.World.Add(spawner, new EnemySpawner
                {
                    Enabled = true,
                    Timer = EnemyConfig.BaseSpawnTime,
                    ActiveCount = 0,
                    SpawnInterval = EnemyConfig.BaseSpawnTime,
                });
                SpawnerEntityId = spawner.Id;
            }

            context.Log.Info($"敌人 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            // 对称撤销（Rule 20）：系统/实体/能力/订阅由 Core 按 ModObject 归属强制回收（§9.2/§13.5），
            // 本 Mod 只清静态桥与本地状态。
            OnEnemySpawned = null;
            OnEnemyKilled = null;
            OnEnemyDestroyed = null;
            OnProjectileSpawned = null;
            OnProjectileDestroyed = null;
            OnBossStrike = null;
            OnEnemyBlast = null; // M3：爆炸表现（宿主 EnemyBlastView）
            _spawnSystem = null;
            World = null!;
            Context = null!;
            SpawnerEntityId = 0;
            PlayerEntityId = 0;
            context.Log.Info($"敌人 Mod '{context.Info.Id}' 已注销");
        }

        // ---- 能力实现（契约 §4；同步执行 + 当场发消息，§0.5） ----

        /// <summary>enemy:damage：args=[target u32, amount i32, source u32, sourceKind u32?] → [killed bool]。</summary>
        private static object?[] ApplyDamage(object? args)
        {
            if (!CapabilityShapes.TryReadDamageArgs(args, out var target, out var amount, out var source, out var sourceKind))
                return new object?[] { false };
            var e = new Entity(target);
            if (!World.Exists(e) || !World.TryGet<EnemyHealth>(e, out var hp))
                return new object?[] { false }; // 实体不存在/非敌人 → [false]
            if (amount <= 0) return new object?[] { false };
            var flags = World.TryGet<EnemyFlags>(e, out var f) ? f : default;
            if (flags.IsDead) return new object?[] { false }; // 已死 → [false]

            // 爆炸免疫（契约 §4：sourceKind==1 且 BlastImmunity → 吸收；火箭筒对 Titan 无效）
            if (sourceKind == 1 && flags.BlastImmunity) return new object?[] { false };

            hp.Current = Math.Max(0, hp.Current - amount);
            World.Add(e, hp);
            if (hp.Current > 0) return new object?[] { false };

            // 致死：置 IsDead/IsSinking + 发 EnemyKilled + ZombieCountChanged(-1) + 视图死亡表现（契约 §3 注）
            flags.IsDead = true;
            flags.IsSinking = true;
            flags.SinkTimer = EnemyConfig.SinkDuration;
            World.Add(e, flags);
            PublishEnemyKilled(e);
            DecrementActiveCount();
            NotifyEnemyKilled(target);
            TriggerDeathBehavior(e); // M3：ZomDuck 死亡自爆 / Clown 死亡延迟召唤 MiniClown（致死即触发，见下）
            return new object?[] { true };
        }

        /// <summary>enemy:apply_slow：args=[target u32, speedMul f32, duration f32] → [applied bool]（冰手枪缓速，契约 §4）。</summary>
        private static object?[] ApplySlow(object? args)
        {
            if (!CapabilityShapes.TryReadSlowArgs(args, out var target, out var speedMul, out var duration))
                return new object?[] { false };
            var e = new Entity(target);
            if (!World.Exists(e) || !World.TryGet<EnemyMovement>(e, out var mv))
                return new object?[] { false };
            if (World.TryGet<EnemyFlags>(e, out var flags) && flags.IsDead)
                return new object?[] { false }; // 已死不施加
            mv.SpeedMul = speedMul;
            mv.EffectTimer = duration;
            World.Add(e, mv);
            return new object?[] { true };
        }

        /// <summary>enemy:get_all：[] → 行集 [id, x, y, z, kind u32, alive, hp, maxHp]（冰弹追踪/调试，M2）。</summary>
        private static object?[] GetAll()
        {
            var rows = new List<object?[]>();
            foreach (var (id, type, hp, flags, pos) in IterateEnemies())
                rows.Add(CapabilityShapes.GetAllRow(id, pos.X, pos.Y, pos.Z, type.Kind, !flags.IsDead, hp.Current, hp.Max));
            return rows.ToArray();
        }

        /// <summary>enemy:get_count：[] → [count int]（HUD 初始填充，存活敌人数）。</summary>
        private static object?[] GetCount()
        {
            var se = new Entity(SpawnerEntityId);
            var count = World.TryGet<EnemySpawner>(se, out var sp) ? sp.ActiveCount : 0;
            return CapabilityShapes.GetCountRow(count);
        }

        /// <summary>enemy:set_spawning：args=[enabled bool] → [ok bool]（game 相位控制，契约 §4）。</summary>
        private static object?[] SetSpawning(object? args)
        {
            if (!CapabilityShapes.TryReadSetSpawningArgs(args, out var enabled))
                return new object?[] { false };
            var se = new Entity(SpawnerEntityId);
            if (!World.TryGet<EnemySpawner>(se, out var spawner))
                return new object?[] { false };
            spawner.Enabled = enabled;
            if (!enabled)
            {
                spawner.Timer = 0f; // 清空 Timer（暂停，契约 §4）
            }
            else if (spawner.Timer <= 0f)
            {
                spawner.Timer = spawner.SpawnInterval; // 恢复：满间隔后首刷（对齐原版 StartSpawning）
            }
            World.Add(se, spawner);
            return new object?[] { true };
        }

        /// <summary>enemy:reset：[] → [ok bool]（game:start 调用，契约 §4：销毁全部敌人+投射物+视图、ActiveCount=0、
        /// 间隔=base 3s、发 ZombieCountChanged(0)）。</summary>
        private static object?[] Reset()
        {
            if (World is null || SpawnerEntityId == 0) return new object?[] { false };

            // 销毁全部敌人实体（含下沉尸体）+ 投射物实体（M2：飞行中的火球）并通知视图
            var stale = new List<uint>();
            foreach (var (id, _) in World.Store<EnemyType>().All()) stale.Add(id);
            foreach (var (id, _) in World.Store<EnemyProjectile>().All()) stale.Add(id);
            foreach (var id in stale)
            {
                var e = new Entity(id);
                if (World.Has<EnemyType>(e)) NotifyEnemyDestroyed(id);
                else NotifyProjectileDestroyed(id);
                World.Destroy(e);
            }

            var se = new Entity(SpawnerEntityId);
            if (World.TryGet<EnemySpawner>(se, out var spawner))
            {
                spawner.ActiveCount = 0;
                spawner.SpawnInterval = EnemyConfig.BaseSpawnTime;
                spawner.Timer = EnemyConfig.BaseSpawnTime;
                World.Add(se, spawner);
            }
            _spawnSystem?.ResetSession(); // 每类型缓冲/冷却复位
            PublishZombieCount(0);
            return new object?[] { true };
        }

        // ---- 内部 ----

        /// <summary>视图静态桥通知（事件只能在声明类内调用，C# 事件语义）：宿主 EnemySpawnerView / EnemyView 表现。</summary>
        internal static void NotifyEnemySpawned(uint id) => OnEnemySpawned?.Invoke(id);
        internal static void NotifyEnemyKilled(uint id) => OnEnemyKilled?.Invoke(id);
        internal static void NotifyEnemyDestroyed(uint id) => OnEnemyDestroyed?.Invoke(id);
        internal static void NotifyProjectileSpawned(uint id, byte kind) => OnProjectileSpawned?.Invoke(id, kind);
        internal static void NotifyProjectileDestroyed(uint id) => OnProjectileDestroyed?.Invoke(id);
        internal static void NotifyBossStrike(uint enemyId, float x, float z) => OnBossStrike?.Invoke(enemyId, x, z);
        internal static void NotifyEnemyBlast(uint enemyId, float x, float y, float z) => OnEnemyBlast?.Invoke(enemyId, x, y, z);

        /// <summary>调用 player:damage（二进制 ModCall，契约 §7；攻击归属 source=敌人实体）。
        /// 玩家未加载/解析失败时本次攻击落空，不中断系统（接触/远程/AOE 共用）。</summary>
        internal static void DamagePlayer(uint source, int amount)
        {
            try
            {
                Context.Mods.Call(PlayerModId, PlayerDamageCap, DataCodec.Write(new object?[] { amount, source }));
            }
            catch (Exception)
            {
                // 玩家未加载（卸载竞态）：本次攻击落空，不中断系统
            }
        }

        /// <summary>调用 player:get_position（追击目标点 + 存活判定）；玩家未加载/解析失败 → null。</summary>
        public static (float X, float Y, float Z, bool Alive)? TryGetPlayerPosition(IModContext ctx)
        {
            try
            {
                var buf = ctx.Mods.Call(PlayerModId, PlayerGetPositionCap, DataCodec.Write(null));
                var row = DataCodec.Read(new PayloadReader(buf));
                if (CapabilityShapes.TryReadPlayerPosition(row, out var x, out var y, out var z, out var alive))
                    return (x, y, z, alive);
                return null;
            }
            catch (Exception)
            {
                return null; // 玩家未加载/卸载后：调用方降级（停追/停生成/不攻击）
            }
        }

        /// <summary>Register 时取一次玩家实体（契约 §7）。</summary>
        private static uint FetchPlayerEntity()
        {
            try
            {
                var buf = Context.Mods.Call(PlayerModId, PlayerGetEntityCap, DataCodec.Write(null));
                var row = DataCodec.Read(new PayloadReader(buf));
                if (CapabilityShapes.TryReadPlayerEntity(row, out var id)) return id;
                return 0;
            }
            catch (Exception)
            {
                return 0; // 依赖已声明，正常不会发生；取不到则追击目标暂缺
            }
        }

        /// <summary>发布 enemy:EnemyKilled（enemyType/scoreValue/死亡位置，契约 §5）。</summary>
        private static void PublishEnemyKilled(Entity e)
        {
            if (!World.TryGet<EnemyType>(e, out var type)) return;
            if (!World.TryGet<EnemyHealth>(e, out var hp)) return;
            var pos = World.TryGet<EnemyPosition>(e, out var p) ? p : default;
            var buffer = EnemyMessagePayload.EnemyKilled(type.Kind, hp.ScoreValue, pos.X, pos.Y, pos.Z);
            Context.Messages.Publish(EnemyKilledEvent, 1, in buffer);
        }

        /// <summary>击杀后存活计数 -1 并发布 ZombieCountChanged（契约 §3 注 / §5）。</summary>
        private static void DecrementActiveCount()
        {
            if (SpawnerEntityId == 0) return;
            var se = new Entity(SpawnerEntityId);
            if (!World.TryGet<EnemySpawner>(se, out var spawner)) return;
            if (spawner.ActiveCount > 0) spawner.ActiveCount--;
            World.Add(se, spawner);
            PublishZombieCount(spawner.ActiveCount);
        }

        /// <summary>发布 enemy:ZombieCountChanged（生成/击杀/重置，契约 §5）。</summary>
        public static void PublishZombieCount(int count)
        {
            var buffer = EnemyMessagePayload.ZombieCountChanged(count);
            Context.Messages.Publish(ZombieCountChangedEvent, 1, in buffer);
        }

        private static IEnumerable<(uint Id, EnemyType Type, EnemyHealth Hp, EnemyFlags Flags, EnemyPosition Pos)> IterateEnemies()
        {
            foreach (var (id, type) in World.Store<EnemyType>().All())
            {
                var e = new Entity(id);
                if (!World.TryGet<EnemyHealth>(e, out var hp)) continue;
                var flags = World.TryGet<EnemyFlags>(e, out var fl) ? fl : default;
                var pos = World.TryGet<EnemyPosition>(e, out var p) ? p : default;
                yield return (id, type, hp, flags, pos);
            }
        }

        // ---- M3 辅助行为（任务：ZomDuck 死亡自爆 / Clown 召唤 MiniClown / 生成共用） ----

        /// <summary>
        /// 生成一个敌人实体（全组件，生成器/辅助召唤共用——任务：复用生成器逻辑/实体；归属本 ModObject，
        /// 卸载强制回收 §9.2）。视图经静态桥通知宿主 EnemySpawnerView 实例化原 prefab（契约 §8）。
        /// 带回血类型（EnemyConfig.KindStats.HasRegen）挂 EnemyRegen 组件（原版 prefab 带 EnemyRegen 组件，M3）。
        /// 注：不负责 ActiveCount/ZombieCountChanged 计数——生成器在批次结束统一 +N 并发消息（契约 §3 序 1），
        /// 辅助召唤（EnemyAuxSystem）自行 +N 并发消息；测试侧手动建敌人同样不经过计数。
        /// </summary>
        internal static uint BuildEnemyEntity(EnemyKind kind, float x, float y, float z)
        {
            var st = EnemyConfig.Of(kind);
            var e = Context.Ecs.CreateEntity();
            World.Add(e, new EnemyType { Kind = (byte)kind });
            World.Add(e, new EnemyHealth { Current = st.Hp, Max = st.Hp, ScoreValue = st.ScoreValue });
            World.Add(e, new EnemyFlags { BlastImmunity = st.BlastImmunity });
            World.Add(e, new EnemyAttack
            {
                Kind = (byte)st.Attack,
                Damage = st.AttackDamage,
                Range = st.AttackRange,
                Interval = st.AttackInterval,
                Cooldown = 0f,
            });
            World.Add(e, new EnemyMovement { Speed = st.Speed, SpeedMul = 1f, EffectTimer = 0f });
            World.Add(e, new EnemyTarget { PlayerEntityId = PlayerEntityId });
            World.Add(e, new EnemyPosition { X = x, Y = y, Z = z });
            if (st.HasRegen)
                World.Add(e, new EnemyRegen { Timer = st.RegenInterval }); // M3：回血计时（原版 EnemyRegen 组件）
            NotifyEnemySpawned(e.Id); // 宿主 EnemySpawnerView 实例化 prefab 并绑定 EntityId
            return e.Id;
        }

        /// <summary>
        /// 致死触发辅助行为（M3）：ZomDuck(9) → 死亡点爆炸（SelfDestruct/IBlast，对齐原版半径/伤害）；
        /// Clown(7) → 延迟召唤 MiniClown（SpawnClown，EnemyAuxSystem 计时执行）。其余类型无死亡辅助。
        /// 在致死标记/消息/计数之后调用：本函数内 AOE 击杀/延迟召唤产生的消息按各自规则正常发布。
        /// </summary>
        private static void TriggerDeathBehavior(Entity e)
        {
            if (!World.TryGet<EnemyType>(e, out var type)) return;
            switch ((EnemyKind)type.Kind)
            {
                case EnemyKind.ZomDuck:
                    DetonateZomDuck(e);
                    break;
                case EnemyKind.Clown:
                    ScheduleMiniClowns(e);
                    break;
            }
        }

        /// <summary>
        /// ZomDuck 死亡自爆（M3，原版 SelfDestruct.Explode）：以死亡点为爆心做 XZ 半径 3m 判定（近似原版
        /// OverlapSphere(3f)，本 Mod 距离口径统一 XZ 平面）：范围内其他敌人（非爆炸免疫）受 BlaseDamage/6=16
        /// 爆炸伤害（sourceKind=1 → 走 enemy:damage 内裁决，Titan BlastImmunity 吸收）；玩家在圈内受
        /// BlaseDamage=99（player:damage）；并通知视图在死亡点播原 CFX 爆炸 prefab（OnEnemyBlast）。
        /// 原版内圈 1f 双层循环为注释死代码（未执行），不另做内圈加成。
        /// </summary>
        private static void DetonateZomDuck(Entity duck)
        {
            if (!World.TryGet<EnemyPosition>(duck, out var pos)) return; // 无位置（防御）：不爆
            var radius = EnemyConfig.ZomDuckBlastRadius;
            var r2 = radius * radius;

            // 1) 周围敌人（快照先收集再逐个 ApplyDamage——递归致死安全，尸体/IsDead 由 enemy:damage 拒收）
            var targets = new List<uint>();
            foreach (var (id, _) in World.Store<EnemyType>().All())
            {
                if (id == duck.Id) continue;
                var t = new Entity(id);
                if (!World.TryGet<EnemyFlags>(t, out var tf) || tf.IsDead) continue; // 尸体不结算
                if (!World.TryGet<EnemyPosition>(t, out var tp)) continue;
                var dx = tp.X - pos.X;
                var dz = tp.Z - pos.Z;
                if (dx * dx + dz * dz <= r2) targets.Add(id);
            }
            foreach (var id in targets)
                ApplyDamage(CapabilityShapes.DamageArgs(id, EnemyConfig.ZomDuckBlastEnemyDamage, duck.Id, 1u));

            // 2) 玩家（存活且在圈内 → BlaseDamage=99）
            var player = TryGetPlayerPosition(Context);
            if (player is not null && player.Value.Alive)
            {
                var dx = player.Value.X - pos.X;
                var dz = player.Value.Z - pos.Z;
                if (dx * dx + dz * dz <= r2)
                    DamagePlayer(duck.Id, EnemyConfig.ZomDuckBlastPlayerDamage);
            }

            // 3) 表现（宿主 EnemyBlastView：原 CFX_Explosion_B_Smoke+Text.prefab，复刻宪法：原版资源）
            NotifyEnemyBlast(duck.Id, pos.X, pos.Y, pos.Z);
        }

        /// <summary>
        /// Clown 死亡：预掷召唤数并在死亡 Clown 实体挂 EnemyPendingMiniClowns（EnemyAuxSystem 延迟 1s 后执行，
        /// 原版 SpawnClown：Invoke("Spawn", 1f) + spawned 防重）。两个槽位各 1 只、15% +1（原版两个 SpawnPoint
        /// 各 clownsSpawned=1、Start 里 Random 0..100 ≤ 15 → ++），出生 XZ = 死亡点 ± ClownMiniSpawnOffsetX（y=0，
        /// 原版 ClownSpawnPoint.y 每帧压 0），在死亡瞬间冻结（尸体下沉/随后来回不追踪，与 SpawnPoint 随 Clown 一致）。
        /// 死亡即触发：计数在到时真正生成时再 +（EnemyAuxSystem），与本函数无涉。
        /// </summary>
        private static void ScheduleMiniClowns(Entity clown)
        {
            if (SpawnerEntityId == 0) return; // 生成器不存在（防御，本 Mod 未初始化）
            if (!World.TryGet<EnemyPosition>(clown, out var pos)) return;

            var off = EnemyConfig.ClownMiniSpawnOffsetX;
            var pending = new EnemyPendingMiniClowns
            {
                Timer = EnemyConfig.ClownMiniSpawnDelay,
                PendingA = RollMiniClownCount(),
                XA = pos.X - off,
                ZA = pos.Z,
                PendingB = RollMiniClownCount(),
                XB = pos.X + off,
                ZB = pos.Z,
            };
            World.Add(clown, pending);
        }

        /// <summary>每槽位召唤数：1 只，15% 概率 +1（原版 SpawnClown.Start）。</summary>
        private static int RollMiniClownCount()
            => _rng.NextDouble() < EnemyConfig.ClownMiniSpawnExtraChance ? 2 : 1;
    }
}
