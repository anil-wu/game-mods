using System;
using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人 Mod（com.zombtoy.enemy）：10 种类型 + 加权生成器 + 追击/接触伤害 + 生命/死亡 + 计数。
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

                // 4. 系统（契约 §3 顺序：Spawn → Chase → Attack → Health(死亡清理)；Ranged/Boss 为 M2）
                _spawnSystem = new EnemySpawnSystem(context);
                context.Ecs.RegisterSystem(_spawnSystem, SystemSide.Server);
                context.Ecs.RegisterSystem(new EnemyChaseSystem(context), SystemSide.Server);
                context.Ecs.RegisterSystem(new EnemyAttackSystem(context), SystemSide.Server);
                context.Ecs.RegisterSystem(new EnemyHealthSystem(context), SystemSide.Server);

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

        /// <summary>enemy:reset：[] → [ok bool]（game:start 调用，契约 §4：销毁全部敌人+视图、ActiveCount=0、
        /// 间隔=base 3s、发 ZombieCountChanged(0)）。</summary>
        private static object?[] Reset()
        {
            if (World is null || SpawnerEntityId == 0) return new object?[] { false };

            // 销毁全部敌人实体（含下沉尸体）+ 通知视图
            var stale = new List<uint>();
            foreach (var (id, _) in World.Store<EnemyType>().All()) stale.Add(id);
            foreach (var id in stale)
            {
                NotifyEnemyDestroyed(id);
                World.Destroy(new Entity(id));
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
    }
}
