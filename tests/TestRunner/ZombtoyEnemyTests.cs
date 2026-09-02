using System;
using Com.Game.Core;
using Com.Zombtoy.Enemy;
using Com.Zombtoy.Player;
using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// com.zombtoy.enemy 无头测试（单机 Host 路径，契约 §2：SystemSide.Server + UpdateAll）：
    /// 加权生成表（权重分布/startBuffer 门控/全局并发上限 50/Titan 每类型并发 1）、
    /// 能力（damage 扣血/致死/爆炸免疫、apply_slow、get_all、get_count、set_spawning、reset）、
    /// 消息（EnemyKilled / ZombieCountChanged 二进制字段）、
    /// 系统（追击目标获取与移动/缓速衰减/接触伤害/远程火球生成·命中·超时（M2）/Titan 锁定 AOE（M2）/下沉清理）、
    /// 契约测试向量自校验（§14.11.3）、卸载清理（Rule 20）。
    /// </summary>
    public static class ZombtoyEnemyTests
    {
        private static readonly ModId EnemyId = new("com.zombtoy.enemy");
        private static readonly ModId PlayerId = new("com.zombtoy.player");
        private static readonly ModId TestSubscriber = new("com.test.runner");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public World World => Host.World;
            public IModContext EnemyCtx = null!;
            public IModContext PlayerCtx = null!;

            // 消费方视角订阅（PayloadReader 字段解析，Rule 14；契约 §5）
            public int KilledEvents;
            public int LastKilledType;
            public int LastKilledScore;
            public float LastKilledX, LastKilledY, LastKilledZ;
            public int CountEvents;
            public int LastCount;

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };

                // 依赖（契约 §6）：core → player → enemy
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.core")), typeof(CoreMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.player")),
                    typeof(PlayerMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.player"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.enemy")),
                    typeof(EnemyMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.enemy"));
                f.EnemyCtx = f.Host.Manager.Get(EnemyId)!.Context;
                f.PlayerCtx = f.Host.Manager.Get(PlayerId)!.Context;

                f.Host.Messages.Subscribe(TestSubscriber, EnemyMod.EnemyKilledEvent,
                    (in MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.KilledEvents++;
                        if (reader.TryReadUInt32(1, out var t)) f.LastKilledType = (int)t;
                        if (reader.TryReadInt32(2, out var s)) f.LastKilledScore = s;
                        if (reader.TryReadFloat(3, out var x)) f.LastKilledX = x;
                        if (reader.TryReadFloat(4, out var y)) f.LastKilledY = y;
                        if (reader.TryReadFloat(5, out var z)) f.LastKilledZ = z;
                    });
                f.Host.Messages.Subscribe(TestSubscriber, EnemyMod.ZombieCountChangedEvent,
                    (in MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.CountEvents++;
                        if (reader.TryReadInt32(1, out var c)) f.LastCount = c;
                    });
                return f;
            }

            public void Tick(int frames, float dt = 0.05f)
            {
                for (var i = 0; i < frames; i++) Host.Tick(dt);
            }

            public Entity Player => new(PlayerMod.LocalPlayerEntityId);

            public object?[] Call(CapabilityId cap, params object?[] args)
                => ModCall.Invoke(EnemyCtx.Mods, EnemyId, cap, args);

            public bool CallBool(CapabilityId cap, params object?[] args)
                => ModCall.InvokeBool(EnemyCtx.Mods, EnemyId, cap, args);

            public object?[] CallPlayer(CapabilityId cap, params object?[] args)
                => ModCall.Invoke(PlayerCtx.Mods, PlayerId, cap, args);

            public bool CallPlayerBool(CapabilityId cap, params object?[] args)
                => ModCall.InvokeBool(PlayerCtx.Mods, PlayerId, cap, args);

            /// <summary>加速生成器：Timer=0 + 小间隔，让生成尽快发生（测试不依赖 3s 基础间隔）。</summary>
            public void SpeedUpSpawner()
            {
                var se = new Entity(EnemyMod.SpawnerEntityId);
                var sp = World.Get<EnemySpawner>(se);
                sp.Timer = 0f;
                sp.SpawnInterval = 0.05f;
                World.Add(se, sp);
            }
        }

        // ---- 生成辅助（等价生成器产出路径：全组件 + 归属无关，测试侧直接建） ----

        private static uint SpawnTestEnemy(Fixture f, EnemyKind kind, float x, float y, float z)
        {
            var cfg = EnemyConfig.Of(kind);
            var e = f.World.CreateEntity();
            f.World.Add(e, new EnemyType { Kind = (byte)kind });
            f.World.Add(e, new EnemyHealth { Current = cfg.Hp, Max = cfg.Hp, ScoreValue = cfg.ScoreValue });
            f.World.Add(e, new EnemyFlags { BlastImmunity = cfg.BlastImmunity });
            f.World.Add(e, new EnemyAttack
            {
                Kind = (byte)cfg.Attack,
                Damage = cfg.AttackDamage,
                Range = cfg.AttackRange,
                Interval = cfg.AttackInterval,
                Cooldown = 0f,
            });
            f.World.Add(e, new EnemyMovement { Speed = cfg.Speed, SpeedMul = 1f, EffectTimer = 0f });
            f.World.Add(e, new EnemyTarget { PlayerEntityId = PlayerMod.LocalPlayerEntityId });
            f.World.Add(e, new EnemyPosition { X = x, Y = y, Z = z });
            return e.Id;
        }

        private static uint FirstEnemy(Fixture f)
        {
            foreach (var (id, _) in f.World.Store<EnemyType>().All()) return id;
            throw new Exception("无敌人实体");
        }

        /// <summary>全部投射物实体（M2：EnemyRangedSystem 产出，契约 §3 序 4）。</summary>
        private static System.Collections.Generic.List<uint> AllProjectiles(Fixture f)
        {
            var list = new System.Collections.Generic.List<uint>();
            foreach (var (id, _) in f.World.Store<EnemyProjectile>().All()) list.Add(id);
            return list;
        }

        private static int AliveCountOf(Fixture f, EnemyKind kind)
        {
            var n = 0;
            foreach (var (id, type) in f.World.Store<EnemyType>().All())
            {
                if (type.Kind != (byte)kind) continue;
                var e = new Entity(id);
                if (!f.World.TryGet<EnemyFlags>(e, out var flags) || !flags.IsDead) n++;
            }
            return n;
        }

        // ---- 注册与生成器 ----

        [Test]
        public static void Register_SpawnerEntity_AndDefaults()
        {
            var f = Fixture.Create();
            Assert.True(EnemyMod.SpawnerEntityId != 0, "生成器实体未创建");
            var sp = f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId));
            Assert.True(sp.Enabled, "生成器初始应启用（对齐原版 StartSpawning，game 经 set_spawning 控制相位）");
            Assert.Equal(EnemyConfig.BaseSpawnTime, sp.Timer);
            Assert.Equal(0, sp.ActiveCount);
            Assert.Equal(EnemyConfig.BaseSpawnTime, sp.SpawnInterval);
            Assert.Equal(6, f.Host.Systems.CountOf(EnemyId)); // Spawn/Chase/Attack/Ranged/Boss/Health（契约 §3 序 1..6）
            // 追击目标定位（契约 §7：player:get_entity，Register 时取一次）
            Assert.Equal(PlayerMod.LocalPlayerEntityId, EnemyMod.PlayerEntityId);
        }

        [Test]
        public static void Spawn_GeneratesEnemies_PublishesCount()
        {
            var f = Fixture.Create();
            f.Tick(65); // 3.25s：基础间隔 3s 后首刷（契约 §3 序 1）
            Assert.True(f.World.Store<EnemyType>().Count > 0, "3s 后未生成敌人");
            var sp = f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId));
            Assert.True(sp.ActiveCount > 0, "ActiveCount 未增长");
            Assert.True(f.CountEvents > 0, "生成未发布 ZombieCountChanged");
            Assert.Equal(sp.ActiveCount, f.LastCount);
        }

        [Test]
        public static void SpawnTable_WeightDistribution()
        {
            // 权重抽样核心（表驱动）：10000 次抽样分布 ≈ 权重占比；权重 0 类型（MiniClown，由 Clown 生成 M2）永不出现
            var rng = new Random(42);
            const int n = 10000;
            var counts = new int[EnemyConfig.Table.Length];
            for (var i = 0; i < n; i++)
                counts[(int)EnemySpawnSystem.RollWeighted(rng)]++;

            var totalWeight = 0;
            foreach (var st in EnemyConfig.Table) totalWeight += st.SpawnWeight;

            foreach (var st in EnemyConfig.Table)
            {
                if (st.SpawnWeight <= 0)
                {
                    Assert.Equal(0, counts[(int)st.Kind], $"权重 0 类型不应被抽中: {st.Kind}");
                    continue;
                }
                var expected = (double)st.SpawnWeight / totalWeight;
                var observed = (double)counts[(int)st.Kind] / n;
                Assert.True(Math.Abs(observed - expected) < 0.02,
                    $"{st.Kind} 权重分布漂移: expected={expected:F3} observed={observed:F3}");
            }
        }

        [Test]
        public static void Spawn_RespectsStartBuffer_NoTitanEarly()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();
            f.Tick(200); // 10s：Titan startBuffer=45s 未到 → 绝不生成
            Assert.Equal(0, AliveCountOf(f, EnemyKind.Titan));
            Assert.True(f.World.Store<EnemyType>().Count > 0, "10s 内应有其他类型敌人");
        }

        [Test]
        public static void Spawn_RespectsGlobalCap50_AndPerTypeConcurrency()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();
            // 玩家放远处并每 chunk 换点（敌人追不上 → "玩家存活才生成"条件持续满足，测生成器上限本身）
            var maxObserved = 0;
            for (var chunk = 0; chunk < 80; chunk++)
            {
                f.CallPlayerBool(PlayerMod.ResetCap, 200f + chunk * 25f, 1f, 200f);
                f.Tick(100); // 5s
                var sp = f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId));
                maxObserved = Math.Max(maxObserved, sp.ActiveCount);
                Assert.True(sp.ActiveCount <= EnemyConfig.MaxTotalEnemies,
                    $"全局并发超过上限 {EnemyConfig.MaxTotalEnemies}: {sp.ActiveCount}");
                Assert.True(AliveCountOf(f, EnemyKind.Titan) <= EnemyConfig.Of(EnemyKind.Titan).MaxConcurrent,
                    "Titan 并发超过 1（每类型并发上限失效）");
                if (sp.ActiveCount >= EnemyConfig.MaxTotalEnemies) break;
            }
            Assert.Equal(EnemyConfig.MaxTotalEnemies,
                f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId)).ActiveCount);
        }

        // ---- 能力：damage（扣血/致死/爆炸免疫） ----

        [Test]
        public static void Damage_ReducesHealth_NoKill()
        {
            var f = Fixture.Create();
            var e = SpawnTestEnemy(f, EnemyKind.ZomBear, 5f, 0f, 5f); // HP 200
            var killed = f.CallBool(EnemyMod.DamageCap, e, 50, 3u, 0u);
            Assert.True(!killed);
            Assert.Equal(150, f.World.Get<EnemyHealth>(new Entity(e)).Current);
            Assert.Equal(0, f.KilledEvents);
            Assert.Equal(0, f.CountEvents); // 未致死不发布任何消息
        }

        [Test]
        public static void Damage_Lethal_Killed_PublishesEnemyKilledAndCount()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();
            f.Tick(30);
            var countBefore = f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId)).ActiveCount;
            Assert.True(countBefore >= 2, $"敌人不足: {countBefore}");

            var victim = FirstEnemy(f);
            var kind = (EnemyKind)f.World.Get<EnemyType>(new Entity(victim)).Kind;
            var cfg = EnemyConfig.Of(kind);
            var posBefore = f.World.Get<EnemyPosition>(new Entity(victim));

            var killed = f.CallBool(EnemyMod.DamageCap, victim, 999999, 7u, 0u);
            Assert.True(killed);
            Assert.Equal(1, f.KilledEvents);
            Assert.Equal((int)kind, f.LastKilledType);          // 字段1=enemyType（契约 §5）
            Assert.Equal(cfg.ScoreValue, f.LastKilledScore);    // 字段2=scoreValue（100 普通 / 500 首领）
            Assert.True(Math.Abs(f.LastKilledX - posBefore.X) < 0.001f, "字段3=x 与死亡位置不符");
            Assert.True(Math.Abs(f.LastKilledZ - posBefore.Z) < 0.001f, "字段5=z 与死亡位置不符");
            Assert.Equal(countBefore - 1, f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId)).ActiveCount);
            Assert.Equal(countBefore - 1, f.LastCount);         // ZombieCountChanged(-1)

            // 已死：再打 → [false]，不重复发消息
            var again = f.CallBool(EnemyMod.DamageCap, victim, 10, 0u, 0u);
            Assert.True(!again);
            Assert.Equal(1, f.KilledEvents);
        }

        [Test]
        public static void Damage_BlastImmunity_Titan()
        {
            var f = Fixture.Create();
            // Titan：BlastImmunity=true（契约 §9），爆炸免疫由 enemy:damage 内部裁决（契约 §4）
            var titan = SpawnTestEnemy(f, EnemyKind.Titan, 5f, 0f, 5f);
            var blast = f.CallBool(EnemyMod.DamageCap, titan, 800, 4u, 1u); // sourceKind=1 爆炸
            Assert.True(!blast, "Titan 不应被爆炸伤害");
            Assert.Equal(2000, f.World.Get<EnemyHealth>(new Entity(titan)).Current); // 吸收

            var normal = f.CallBool(EnemyMod.DamageCap, titan, 800, 4u, 0u); // 普通伤害有效
            Assert.True(!normal);
            Assert.Equal(1200, f.World.Get<EnemyHealth>(new Entity(titan)).Current);

            // 普通类型吃爆炸（可致死）
            var zom = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f);
            var blastKill = f.CallBool(EnemyMod.DamageCap, zom, 500, 4u, 1u);
            Assert.True(blastKill);
            Assert.Equal(1, f.KilledEvents);
            Assert.Equal((int)EnemyKind.Zombunny, f.LastKilledType);
        }

        [Test]
        public static void Damage_InvalidTargets_NoOp()
        {
            var f = Fixture.Create();
            // 实体不存在
            var noTarget = f.CallBool(EnemyMod.DamageCap, 99999u, 50, 1u, 0u);
            Assert.True(!noTarget);
            // amount<=0（契约 §4）
            var e = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f);
            var zero = f.CallBool(EnemyMod.DamageCap, e, 0, 1u, 0u);
            Assert.True(!zero);
            Assert.Equal(100, f.World.Get<EnemyHealth>(new Entity(e)).Current);
            var neg = f.CallBool(EnemyMod.DamageCap, e, -10, 1u, 0u);
            Assert.True(!neg);
            Assert.Equal(100, f.World.Get<EnemyHealth>(new Entity(e)).Current);
            Assert.Equal(0, f.KilledEvents);
            // 已死实体
            f.CallBool(EnemyMod.DamageCap, e, 999, 1u, 0u);
            var dead = f.CallBool(EnemyMod.DamageCap, e, 10, 1u, 0u);
            Assert.True(!dead);
            Assert.Equal(1, f.KilledEvents);
        }

        // ---- 能力：apply_slow / get_all / get_count / set_spawning / reset ----

        [Test]
        public static void ApplySlow_SetsMul_ExpiresByTimer()
        {
            var f = Fixture.Create();
            var e = SpawnTestEnemy(f, EnemyKind.ZomBear, 5f, 0f, 5f);
            var applied = f.CallBool(EnemyMod.ApplySlowCap, e, 0.6f, 1.5f);
            Assert.True(applied);
            var mv = f.World.Get<EnemyMovement>(new Entity(e));
            Assert.Equal(0.6f, mv.SpeedMul);
            Assert.True(mv.EffectTimer > 1.4f);

            f.Tick(20); // 1s：仍在缓速
            mv = f.World.Get<EnemyMovement>(new Entity(e));
            Assert.Equal(0.6f, mv.SpeedMul);

            f.Tick(20); // 再 1s：1.5s 到点恢复（EnemyChaseSystem 每帧衰减，契约 §4）
            mv = f.World.Get<EnemyMovement>(new Entity(e));
            Assert.Equal(1f, mv.SpeedMul);
            Assert.Equal(0f, mv.EffectTimer);

            // 已死敌人拒绝施加
            var dead = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f);
            f.CallBool(EnemyMod.DamageCap, dead, 999, 1u, 0u);
            var onDead = f.CallBool(EnemyMod.ApplySlowCap, dead, 0.5f, 1f);
            Assert.True(!onDead);
        }

        [Test]
        public static void GetAll_And_GetCount_Rows()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();
            f.Tick(30); // 生成器生成的敌人计入 get_count（ActiveCount）
            var sp = f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId));
            Assert.True(sp.ActiveCount >= 1, "无生成敌人");

            var countRow = f.Call(EnemyMod.GetCountCap);
            Assert.True(countRow.Length > 0 && countRow[0] is int c && c == sp.ActiveCount,
                $"get_count 与 ActiveCount 不符: {sp.ActiveCount}");

            // get_all 行集：[id, x, y, z, kind u32, alive, hp, maxHp]（契约 §10 行形状）
            var all = f.Call(EnemyMod.GetAllCap);
            Assert.Equal(f.World.Store<EnemyType>().Count, all.Length);
            var seenAlive = 0;
            foreach (var row in all)
            {
                Assert.True(Com.Zombtoy.Enemy.CapabilityShapes.TryReadGetAllRow(row,
                    out var id, out _, out _, out _, out var kind, out var alive, out var hp, out var maxHp),
                    "get_all 行解析失败");
                Assert.True(id != 0, "行缺少实体 id");
                var cfg = EnemyConfig.Of((EnemyKind)kind);
                Assert.Equal(cfg.Hp, maxHp);
                if (alive) seenAlive++;
            }
            Assert.Equal(sp.ActiveCount, seenAlive); // 未受伤：全部存活
        }

        [Test]
        public static void SetSpawning_TogglesPauseResume()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();

            var ok = f.CallBool(EnemyMod.SetSpawningCap, false);
            Assert.True(ok);
            Assert.True(!f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId)).Enabled);
            f.Tick(30);
            Assert.Equal(0, f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId)).ActiveCount); // 暂停不生成

            ok = f.CallBool(EnemyMod.SetSpawningCap, true);
            Assert.True(ok);
            f.Tick(10);
            Assert.True(f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId)).ActiveCount > 0,
                "恢复后未继续生成");
        }

        [Test]
        public static void Reset_ClearsAllEnemies_ResetsInterval_PublishesZero()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();
            f.Tick(30);
            Assert.True(f.World.Store<EnemyType>().Count > 0, "重置前应有敌人");

            var ok = f.CallBool(EnemyMod.ResetCap);
            Assert.True(ok);
            Assert.Equal(0, f.World.Store<EnemyType>().Count);   // 全部敌人实体销毁（契约 §4）
            var sp = f.World.Get<EnemySpawner>(new Entity(EnemyMod.SpawnerEntityId));
            Assert.Equal(0, sp.ActiveCount);
            Assert.Equal(EnemyConfig.BaseSpawnTime, sp.SpawnInterval); // 间隔复位 base 3s
            Assert.Equal(EnemyConfig.BaseSpawnTime, sp.Timer);
            Assert.Equal(0, f.LastCount);                           // 发 ZombieCountChanged(0)
        }

        // ---- 系统：追击 / 接触伤害 / 下沉 ----

        [Test]
        public static void Chase_MovesTowardPlayer_SpeedApplied()
        {
            var f = Fixture.Create();
            var e = SpawnTestEnemy(f, EnemyKind.Zombunny, 10f, 0f, 0f); // 玩家在 (0,1,0)
            var before = f.World.Get<EnemyPosition>(new Entity(e));

            f.Tick(20); // 1s：Zombunny speed 3.0 → 移动 ~3
            var after = f.World.Get<EnemyPosition>(new Entity(e));
            Assert.True(after.X < before.X - 2f, $"追击未向玩家移动: X {before.X}→{after.X}");
            Assert.Equal(0f, after.Z); // 纯 X 方向

            f.Tick(80); // 再 4s → 到达玩家附近（未超调）
            var near = f.World.Get<EnemyPosition>(new Entity(e));
            Assert.True(Math.Abs(near.X) < 0.5f && Math.Abs(near.Z) < 0.5f,
                $"追击未到达玩家: ({near.X},{near.Z})");
        }

        [Test]
        public static void Chase_StopsWhenPlayerDead()
        {
            var f = Fixture.Create();
            f.CallPlayerBool(PlayerMod.DamageCap, 999, 0u); // 击杀玩家
            var e = SpawnTestEnemy(f, EnemyKind.Zombunny, 10f, 0f, 0f);

            f.Tick(20);
            var pos = f.World.Get<EnemyPosition>(new Entity(e));
            Assert.Equal(10f, pos.X); // 玩家死亡 → 不追击
            Assert.Equal(0f, pos.Z);
        }

        [Test]
        public static void ContactAttack_DamagesPlayer_InRangeOnly()
        {
            var f = Fixture.Create();
            // 近身（0.5,1,0.5）：距离 ~0.71 ≤ Range 2.0 → 接触伤害（契约 §3 序 3）
            var e = SpawnTestEnemy(f, EnemyKind.Zombunny, 0.5f, 1f, 0.5f);
            f.Tick(20); // 1s：0.5s 间隔 → 2 次攻击（10/次）
            var hp = f.World.Get<PlayerHealth>(f.Player);
            Assert.True(hp.Current <= 90, $"近身未触发接触伤害: hp={hp.Current}");
            Assert.True(hp.Current >= 80, $"攻击次数超预期: hp={hp.Current}");

            // 远处（10,0,0）：超出 Range → 不伤害
            var f2 = Fixture.Create();
            var far = SpawnTestEnemy(f2, EnemyKind.Zombunny, 10f, 0f, 0f);
            f2.Tick(20);
            Assert.Equal(100, f2.World.Get<PlayerHealth>(f2.Player).Current);
            Assert.True(!f2.World.Get<EnemyFlags>(new Entity(far)).IsDead);
        }

        // ---- M2：远程火球（契约 §3 序 4，Clown AttackKind.Fireball）----

        [Test]
        public static void Ranged_Fireball_SpawnsProjectile_AndHitsPlayer()
        {
            var f = Fixture.Create();
            var clown = SpawnTestEnemy(f, EnemyKind.Clown, 10f, 0f, 0f); // 射程 25 内（契约 §9），冷却 0 → 首帧即发
            f.Tick(1);

            var shots = AllProjectiles(f);
            Assert.True(shots.Count >= 1, "射程内冷却到未生成火球");
            var p = f.World.Get<EnemyProjectile>(new Entity(shots[0]));
            Assert.Equal((byte)EnemyProjectileKind.Fireball, p.Kind);
            Assert.Equal(EnemyConfig.Of(EnemyKind.Clown).AttackDamage, p.Damage); // 40（契约 §9）
            Assert.Equal(EnemyConfig.FireballSpeed, p.Speed);                     // 对齐原版实际速率
            Assert.True(p.DirX < 0f && p.DirZ == 0f, $"方向未指向玩家: ({p.DirX},{p.DirZ})"); // 玩家在原点 → 沿 -X
            Assert.True(f.World.TryGet<EnemyBossLock>(new Entity(clown), out _) == false, "Clown 不应建 Boss 锁定状态");

            f.Tick(20); // 再 1s：16m/s × 10m 早已命中（~0.7s）→ 扣 40，投射物命中即销毁
            Assert.Equal(60, f.World.Get<PlayerHealth>(f.Player).Current); // 100-40（一次命中；1.05s 未到 1.5s 第二次开火）
            Assert.Equal(0, AllProjectiles(f).Count);                     // 命中后销毁实体
            Assert.True(!f.World.Get<EnemyFlags>(new Entity(clown)).IsDead);
        }

        [Test]
        public static void Ranged_Fireball_OutOfRange_NoFire()
        {
            var f = Fixture.Create();
            SpawnTestEnemy(f, EnemyKind.Clown, 35f, 0f, 0f); // 射程 25 外（契约 §9）
            f.Tick(40); // 2s：追击 2.8m/s 推进 ~5.6m → 仍 29.4m 外 → 不生成火球
            Assert.Equal(0, AllProjectiles(f).Count);
            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current);

            f.Tick(200); // 再 10s：追击进入射程（25m，~71 帧）后开火 → 火球命中扣血
            Assert.True(f.World.Get<PlayerHealth>(f.Player).Current < 100, "进入射程后未发生火球伤害");
        }

        [Test]
        public static void Projectile_Timeout_Destroyed()
        {
            var f = Fixture.Create();
            SpawnTestEnemy(f, EnemyKind.Clown, 5f, 0f, 0f);
            f.Tick(1); // 首帧开火（火球朝玩家当前位置飞行）
            Assert.Equal(1, AllProjectiles(f).Count);

            f.CallPlayerBool(PlayerMod.ResetCap, 300f, 1f, 300f); // 玩家瞬移远点：火球直线无追踪 → 永不命中
            f.Tick(220); // 再 11s > 存活期 10s（原版 Destroy(gameObject, 10f)）→ 超时销毁
            Assert.Equal(0, AllProjectiles(f).Count);
            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current);
        }

        // ---- M2：首领 Titan 地面锁定（契约 §3 序 5，AttackKind.GroundTarget）----

        [Test]
        public static void Boss_Titan_LockAoe_DamagesPlayer_AfterCharge()
        {
            var f = Fixture.Create();
            var titan = SpawnTestEnemy(f, EnemyKind.Titan, 10f, 0f, 0f); // 射程 15 内（契约 §9）
            f.Tick(20); // 1s：准星锁定 + 蓄力中（Interval 3.0s），未到点不伤害
            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current);
            Assert.True(f.World.TryGet<EnemyBossLock>(new Entity(titan), out var lock1)
                        && lock1.Tracking, "射程内未进入锁定（Tracking=false）");
            Assert.True(lock1.Charge > 0f && lock1.Charge < 3f, $"蓄力进度异常: {lock1.Charge}");
            Assert.True(lock1.X < 9f, $"准星未向玩家追踪: X={lock1.X}"); // 从 x=10 向玩家 x=0 lerp（dt*5）

            f.Tick(60); // 再 3s：蓄力 3.0s 到点 → 落点 AOE 60（契约 §9 Titan 60）
            var hp = f.World.Get<PlayerHealth>(f.Player).Current;
            Assert.Equal(40, hp); // 100-60（首击；4s < 6s 未二次开火）
            Assert.True(f.World.TryGet<EnemyBossLock>(new Entity(titan), out var lock2)
                        && lock2.Charge < 3f, "蓄力完成后未清零（应重新蓄力）");
            Assert.Equal(0, AllProjectiles(f).Count); // Titan 落点是 AOE（契约），不产生 EnemyProjectile 实体
        }

        [Test]
        public static void Boss_Titan_OutOfRange_NoLockNoDamage()
        {
            var f = Fixture.Create();
            var titan = SpawnTestEnemy(f, EnemyKind.Titan, 30f, 0f, 0f); // 射程 15 外（契约 §9）
            f.Tick(40); // 2s：追击 1.2m/s 推进 ~2.4m → 仍 27.6m 外 → 不锁定不蓄力不伤害
            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current);
            Assert.True(f.World.TryGet<EnemyBossLock>(new Entity(titan), out var lockState)
                        && !lockState.Tracking, "射程外仍在锁定（Tracking=true）");
            Assert.Equal(0f, lockState.Charge); // 射程外不蓄力
        }

        [Test]
        public static void Boss_Titan_StrikeOutOfAoeCircle_Evades()
        {
            var f = Fixture.Create();
            var titan = SpawnTestEnemy(f, EnemyKind.Titan, 10f, 0f, 0f);
            f.Tick(57); // 2.85s：准星已收敛到玩家（原点），蓄力将满（Interval 3.0s）
            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current);

            // 蓄力完成前玩家横向跑位：仍在 Titan 射程 15 内，但离准星落点（≈原点）> AOE 3m → 落点伤害落空
            f.CallPlayerBool(PlayerMod.ResetCap, 0f, 1f, 12f);
            f.Tick(3); // 帧 60 = 蓄力 3.0s 到点开火；准星 3 帧只 lerp 到 z≈6.9，距玩家 12m > AOE 3m
            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current); // AOE 未罩住跑位后的玩家
            Assert.True(f.World.TryGet<EnemyBossLock>(new Entity(titan), out var lockState)
                        && lockState.Tracking, "跑位后仍在射程内应继续锁定");
            Assert.True(lockState.Charge < 3f, "蓄力到点开火后未清零（应重新蓄力）");
        }

        [Test]
        public static void Sink_DestroysCorpseAfterTimer()
        {
            var f = Fixture.Create();
            var e = SpawnTestEnemy(f, EnemyKind.Zombunny, 3f, 0f, 3f);
            var killed = f.CallBool(EnemyMod.DamageCap, e, 9999, 1u, 0u);
            Assert.True(killed);
            var flags = f.World.Get<EnemyFlags>(new Entity(e));
            Assert.True(flags.IsDead && flags.IsSinking, "致死未置 IsDead/IsSinking");
            Assert.Equal(EnemyConfig.SinkDuration, flags.SinkTimer);

            f.Tick(30); // 1.5s：仍在（下沉中，契约 §3 序 6）
            Assert.True(f.World.Exists(new Entity(e)), "下沉未到点不应销毁");
            f.Tick(20); // 再 1s：到点销毁
            Assert.True(!f.World.Exists(new Entity(e)), "下沉到点未销毁实体");
        }

        // ---- 契约测试向量（§14.11.3）：owner 规范字节 ↔ 样例一致 ----

        [Test]
        public static void TestVectors_DataCodecShapes_RoundTrip()
        {
            foreach (var v in EnemyTestVectors.All())
            {
                if (v.ContractId is EnemyTestVectors.KilledMsgContract or EnemyTestVectors.CountMsgContract)
                    continue; // 消息向量不是 DataCodec 形状，走下一测试

                var fields = v.DecodeFields(); // owner 规范字节 → 字段元组
                Assert.Equal(v.Sample.Length, fields.Length, $"{v.ContractId} 字段数漂移");
                for (var i = 0; i < v.Sample.Length; i++)
                    Assert.Equal(v.Sample[i], fields[i], $"{v.ContractId}[{i}] 与样例不一致");
            }
        }

        [Test]
        public static void TestVectors_Messages_ConsumerStyleParsers()
        {
            // 消费方（score.ScoreSystem / ui.HudZombies）各自重复定义的解析器（Rule 19）
            // 解析 owner 规范字节，与样例一致 → "可重复定义未漂移"（§14.11.3）
            foreach (var v in EnemyTestVectors.All())
            {
                switch (v.ContractId)
                {
                    case EnemyTestVectors.KilledMsgContract:
                        Assert.True(ScoreSystem.TryRead(v.Bytes, out var t, out var s, out var x, out var y, out var z),
                            $"解析失败: {v}");
                        Assert.Equal((uint)v.Sample[0]!, t);
                        Assert.Equal((int)v.Sample[1]!, s);
                        Assert.Equal((float)v.Sample[2]!, x);
                        Assert.Equal((float)v.Sample[3]!, y);
                        Assert.Equal((float)v.Sample[4]!, z);
                        break;
                    case EnemyTestVectors.CountMsgContract:
                        Assert.True(HudZombies.TryRead(v.Bytes, out var c), $"解析失败: {v}");
                        Assert.Equal((int)v.Sample[0]!, c);
                        break;
                }
            }
        }

        // ---- 卸载清理（Rule 20 / §9.2 / §13.5） ----

        [Test]
        public static void Unload_CleansSystemsEntitiesCapabilities()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();
            f.Tick(20);
            Assert.True(f.World.Store<EnemyType>().Count > 0, "卸载前应有敌人");
            var spawnerId = EnemyMod.SpawnerEntityId;
            Assert.True(spawnerId != 0);
            Assert.Equal(6, f.Host.Systems.CountOf(EnemyId));

            f.Host.Manager.Unload(EnemyId);

            Assert.True(!f.Host.Manager.IsLoaded(EnemyId));
            Assert.Equal(0, f.Host.Systems.CountOf(EnemyId));            // 系统移除（§9.2）
            Assert.Equal(0, f.World.Store<EnemyType>().Count);           // 敌人实体随卸载强制销毁（§9.2）
            Assert.True(!f.World.Exists(new Entity(spawnerId)));         // 生成器实体销毁
            Assert.Equal(1, f.World.EntityCount);                        // 仅剩玩家实体（player 仍加载）
            Assert.Equal(0, f.Host.Messages.SubscriptionCount(EnemyId)); // 本 Mod 无订阅残留（只发布）
        }

        /// <summary>声明依赖 enemy 的探针 Mod（验证卸载后能力调用 → NoModException，§12.11 规则 2）。</summary>
        public sealed class EnemyProbeMod : IMod
        {
            public static readonly ModId Id = new("com.test.enemyprobe");
            public static Exception? CallError;

            public void Register(IModContext context) { }
            public void Unregister(IModContext context) { }

            public static void TryCall(IModContext ctx)
            {
                try { ModCall.Invoke(ctx.Mods, EnemyId, EnemyMod.GetCountCap); }
                catch (Exception e) { CallError = e; }
            }
        }

        [Test]
        public static void Unload_CapabilityCall_ThrowsNoMod()
        {
            var f = Fixture.Create();
            f.Host.Manager.Unload(EnemyId);

            // 卸载后加载探针（声明对 enemy 的依赖）→ 能力调用得到 NoModException
            f.Host.Load(new ModManifest
            {
                ModId = EnemyProbeMod.Id,
                Version = new ModVersion(1, 0, 0),
                Entry = typeof(EnemyProbeMod).FullName!,
                SharedModule = "test.dll",
                Dependencies = { new ModDependency { Id = EnemyId, Version = VersionRange.Any, Optional = false } },
            }, typeof(EnemyProbeMod).Assembly);
            var probeCtx = f.Host.Manager.Get(EnemyProbeMod.Id)!.Context;

            EnemyProbeMod.CallError = null;
            EnemyProbeMod.TryCall(probeCtx);
            Assert.True(EnemyProbeMod.CallError is NoModException,
                $"期望 NoModException，实际 {EnemyProbeMod.CallError}");
        }

        // ---- 消费方风格解析器（Rule 19：结构相同、定义独立，模拟 score/ui 各自重复定义） ----

        /// <summary>消费方 score.ScoreSystem：enemy:EnemyKilled 解析器（[1]=type [2]=score [3..5]=xyz）。</summary>
        public sealed class ScoreSystem
        {
            public static bool TryRead(byte[] bytes, out uint enemyType, out int scoreValue,
                out float x, out float y, out float z)
            {
                enemyType = 0; scoreValue = 0; x = 0; y = 0; z = 0;
                var reader = new PayloadReader(bytes);
                if (!reader.TryReadUInt32(1, out enemyType)) return false;
                if (!reader.TryReadInt32(2, out scoreValue)) return false;
                if (!reader.TryReadFloat(3, out x)) return false;
                if (!reader.TryReadFloat(4, out y)) return false;
                if (!reader.TryReadFloat(5, out z)) return false;
                return true;
            }
        }

        /// <summary>消费方 ui.HudZombies：enemy:ZombieCountChanged 解析器（[1]=count）。</summary>
        public sealed class HudZombies
        {
            public static bool TryRead(byte[] bytes, out int count)
            {
                count = 0;
                var reader = new PayloadReader(bytes);
                return reader.TryReadInt32(1, out count);
            }
        }
    }
}
