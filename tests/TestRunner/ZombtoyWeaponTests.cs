using System;
using Com.Game.Core;
using Com.Zombtoy.Enemy;
using Com.Zombtoy.Player;
using Com.Zombtoy.Weapon;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// com.zombtoy.weapon 无头测试（单机 Host 路径，契约 §2：SystemSide.Server + UpdateAll）：
    /// 弹药消耗/冷却/换弹（弹匣与备弹结算）、add_ammo 补弹（MaxReserve 封顶）、武器切换（槽位/换弹中拒绝）、
    /// 开火 → enemy 伤害链路（FireSystem 二进制 ModCall enemy:damage，含击杀闭环）、
    /// 投射物（火箭直击+内圈/外圈 AOE+爆炸免疫吸收、冰弹追踪+伤害+缓速、龙卷风 tick 伤害+CD）、
    /// 能力（get_state / fire）、契约测试向量自校验（§14.11.3）、卸载清理（Rule 20）。
    /// 敌人由测试直接建（等价生成器产出路径，静止速度=0 免追击漂移）；生成器经 set_spawning(false) 关闭避免干扰。
    /// </summary>
    public static class ZombtoyWeaponTests
    {
        private static readonly ModId WeaponId = new("com.zombtoy.weapon");
        private static readonly ModId PlayerId = new("com.zombtoy.player");
        private static readonly ModId EnemyId = new("com.zombtoy.enemy");
        private static readonly ModId TestSubscriber = new("com.test.runner");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public World World => Host.World;
            public IModContext WeaponCtx = null!;
            public IModContext PlayerCtx = null!;
            public IModContext EnemyCtx = null!;

            // 消费方视角订阅（PayloadReader 字段解析，Rule 14；契约 §5）
            public int AmmoEvents;
            public uint LastAmmoSlot;
            public int LastInMag, LastReserve, LastMaxMag;
            public int SwitchedEvents;
            public uint LastSwitchedSlot;
            // enemy:EnemyKilled（击杀闭环，enemy Mod 发布，契约 §5）
            public int KilledEvents;

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };

                // 依赖（契约 §6）：core → player → enemy → weapon
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.core")), typeof(CoreMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.player")),
                    typeof(PlayerMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.player"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.enemy")),
                    typeof(EnemyMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.enemy"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.weapon")),
                    typeof(WeaponMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.weapon"));
                f.WeaponCtx = f.Host.Manager.Get(WeaponId)!.Context;
                f.PlayerCtx = f.Host.Manager.Get(PlayerId)!.Context;
                f.EnemyCtx = f.Host.Manager.Get(EnemyId)!.Context;

                // 关闭敌人生成器（weapon 测试聚焦武器链路；enemy 生成/追击/攻击由 enemy 测试覆盖）
                ModCall.InvokeBool(f.EnemyCtx.Mods, EnemyId, EnemyMod.SetSpawningCap, false);

                f.Host.Messages.Subscribe(TestSubscriber, WeaponMod.AmmoChangedEvent,
                    (in Game.Messaging.MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.AmmoEvents++;
                        if (reader.TryReadUInt32(1, out var slot)) f.LastAmmoSlot = slot;
                        if (reader.TryReadInt32(2, out var m)) f.LastInMag = m;
                        if (reader.TryReadInt32(3, out var r)) f.LastReserve = r;
                        if (reader.TryReadInt32(4, out var mx)) f.LastMaxMag = mx;
                    });
                f.Host.Messages.Subscribe(TestSubscriber, WeaponMod.WeaponSwitchedEvent,
                    (in Game.Messaging.MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.SwitchedEvents++;
                        if (reader.TryReadUInt32(1, out var slot)) f.LastSwitchedSlot = slot;
                    });
                f.Host.Messages.Subscribe(TestSubscriber, EnemyMod.EnemyKilledEvent,
                    (in Game.Messaging.MessageEnvelope _, PayloadReader reader) => f.KilledEvents++);
                return f;
            }

            public void Tick(int frames, float dt = 0.05f)
            {
                for (var i = 0; i < frames; i++) Host.Tick(dt);
            }

            public object?[] Call(CapabilityId cap, params object?[] args)
                => ModCall.Invoke(WeaponCtx.Mods, WeaponId, cap, args);

            public bool CallBool(CapabilityId cap, params object?[] args)
                => ModCall.InvokeBool(WeaponCtx.Mods, WeaponId, cap, args);

            public Entity Domain => new(WeaponMod.ActiveEntityId);

            public Entity Slot(int index) => new(WeaponMod.SlotEntityIds[index]);

            /// <summary>当前槽位弹药（测试读组件）。</summary>
            public WeaponAmmo CurrentAmmo()
            {
                var active = World.Get<WeaponActive>(Domain);
                return World.Get<WeaponAmmo>(Slot(active.CurrentIndex));
            }

            public void WriteShot(uint hitEntity, bool isHit, float hitX, float hitY, float hitZ, byte kind, uint shooter = 0)
                => World.Add(Domain, new ShotRequest { ShooterId = shooter, HitEntityId = hitEntity, IsHit = isHit, HitX = hitX, HitY = hitY, HitZ = hitZ, Kind = kind });

            public void SwitchTo(byte index)
                => World.Add(Domain, new SwitchRequest { Index = index });

            public void StartReload(byte slot)
            {
                var e = Slot(slot);
                var ammo = World.Get<WeaponAmmo>(e);
                ammo.IsReloading = true;
                ammo.ReloadTimer = WeaponConfig.Slots[slot].ReloadTime;
                World.Add(e, ammo);
            }
        }

        // ---- 生成辅助（等价 enemy 生成器产出路径：全组件；Speed=0 静止免追击漂移） ----

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
            f.World.Add(e, new EnemyMovement { Speed = 0f, SpeedMul = 1f, EffectTimer = 0f }); // 测试靶标：静止
            f.World.Add(e, new EnemyTarget { PlayerEntityId = PlayerMod.LocalPlayerEntityId });
            f.World.Add(e, new EnemyPosition { X = x, Y = y, Z = z });
            return e.Id;
        }

        private static EnemyHealth Hp(Fixture f, uint id) => f.World.Get<EnemyHealth>(new Entity(id));

        // ---- 注册与默认状态 ----

        [Test]
        public static void Register_DefaultState_FiveSystems()
        {
            var f = Fixture.Create();
            Assert.True(WeaponMod.ActiveEntityId != 0, "武器域实体未创建");
            Assert.Equal(5, f.Host.Systems.CountOf(WeaponId)); // Cooldown/Fire/Reload/Switch/Projectile（任务约定 + 契约 §3）

            var active = f.World.Get<WeaponActive>(f.Domain);
            Assert.Equal(0, active.CurrentIndex);
            Assert.Equal(4, active.Count);
            var owner = f.World.Get<WeaponOwner>(f.Domain);
            Assert.Equal(PlayerMod.LocalPlayerEntityId, owner.PlayerEntityId); // 契约 §7：player:get_entity 关联
            Assert.Equal(PlayerMod.LocalPlayerEntityId, WeaponMod.PlayerEntityId);

            // 槽位默认弹药（契约 §9：机关枪 25/50/25）
            var a0 = f.World.Get<WeaponAmmo>(f.Slot(0));
            Assert.Equal(25, a0.InMag);
            Assert.Equal(50, a0.Reserve);
            Assert.Equal(25, a0.MaxMag);
            Assert.True(!a0.IsReloading);
            // get_state（契约 §4）
            var row = f.Call(WeaponMod.GetStateCap);
            Assert.Equal(0u, (uint)row[0]!);
            Assert.Equal(4u, (uint)row[1]!);
            Assert.Equal(25, (int)row[2]!);
            Assert.Equal(50, (int)row[3]!);
            Assert.Equal(25, (int)row[4]!);
        }

        // ---- 开火：弹药消耗 / 冷却 / 校验 ----

        [Test]
        public static void Fire_MachineGun_ConsumesAmmo_DamagesEnemy_PublishesAmmoChanged()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f); // HP 100
            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.MachineGun);
            f.Tick(1);

            // 扣弹 1（契约 §3 序 1：AmmoPerShot=1）+ AmmoChanged([0u, 24, 50, 25]，契约 §5）
            var a0 = f.World.Get<WeaponAmmo>(f.Slot(0));
            Assert.Equal(24, a0.InMag);
            Assert.Equal(WeaponConfig.Slots[0].Interval, a0.FireCooldown); // 冷却计时
            Assert.Equal(1, f.AmmoEvents);
            Assert.Equal(0u, f.LastAmmoSlot);
            Assert.Equal(24, f.LastInMag);
            Assert.Equal(50, f.LastReserve);
            Assert.Equal(25, f.LastMaxMag);

            // 伤害链路：FireSystem → enemy:damage（二进制 ModCall）→ 敌人扣血 60（契约 §3 序 1 / §4）
            Assert.Equal(40, Hp(f, enemy).Current);
            Assert.Equal(0, f.KilledEvents); // 未致死
        }

        [Test]
        public static void Fire_Shotgun_ConsumesFive_Damage50_SingleTarget()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f); // HP 100
            f.SwitchTo(1);
            f.Tick(1);
            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.Shotgun);
            f.Tick(1);

            // 扣弹 5（契约 §9 霰弹 AmmoPerShot=5）；伤害 10×5=50（契约 §9"5 射线各 10"总量等值，单请求聚合）
            var a1 = f.World.Get<WeaponAmmo>(f.Slot(1));
            Assert.Equal(3, a1.InMag);
            Assert.Equal(50, Hp(f, enemy).Current);
            Assert.Equal(1u, f.LastAmmoSlot);
            Assert.Equal(3, f.LastInMag);
        }

        [Test]
        public static void Fire_Cooldown_Enforced()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f);
            f.SwitchTo(1); // Shotgun：间隔 0.45s，AmmoPerShot=5（瞬时伤害便于验证冷却门控）
            f.Tick(1);
            var a1 = f.World.Get<WeaponAmmo>(f.Slot(1));
            a1.InMag = 10; // 抬弹匣以便连测两发（10→5→0）
            f.World.Add(f.Slot(1), a1);

            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.Shotgun);
            f.Tick(1); // 第 1 发（InMag 10→5，冷却 0.45）
            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.Shotgun);
            f.Tick(1); // 冷却中 → 忽略
            Assert.Equal(5, f.World.Get<WeaponAmmo>(f.Slot(1)).InMag);
            Assert.Equal(50, Hp(f, enemy).Current); // 只吃 1 发 50

            f.Tick(9); // 0.45s：过冷却（CooldownSystem 计时）
            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.Shotgun);
            f.Tick(1); // 第 2 发
            Assert.Equal(0, f.World.Get<WeaponAmmo>(f.Slot(1)).InMag);
            Assert.Equal(0, Hp(f, enemy).Current); // 100-50×2 → 致死
            Assert.Equal(1, f.KilledEvents);
        }

        [Test]
        public static void Fire_EmptyMag_NoShot()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f);
            var a0 = f.World.Get<WeaponAmmo>(f.Slot(0));
            a0.InMag = 0; // 弹匣打空（视图触发换弹；此处验证打空不可开火）
            f.World.Add(f.Slot(0), a0);

            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.MachineGun);
            f.Tick(1);
            Assert.Equal(100, Hp(f, enemy).Current); // 无伤害
            Assert.Equal(0, f.AmmoEvents);           // 无 AmmoChanged（未开火）
        }

        [Test]
        public static void Fire_WhileReloading_NoShot()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f);
            f.StartReload(0);

            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.MachineGun);
            f.Tick(1);
            Assert.Equal(100, Hp(f, enemy).Current); // 换弹中不可开火（契约 §3 序 1）
            Assert.Equal(25, f.World.Get<WeaponAmmo>(f.Slot(0)).InMag);
            Assert.Equal(0, f.AmmoEvents);
        }

        [Test]
        public static void Fire_PlayerDead_NoShot()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f);
            ModCall.InvokeBool(f.PlayerCtx.Mods, PlayerId, PlayerMod.DamageCap, 999, 0u); // 击杀玩家

            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.MachineGun);
            f.Tick(1);
            Assert.Equal(100, Hp(f, enemy).Current); // 玩家死亡 → 不开火（契约 §3 序 1）
            Assert.Equal(25, f.World.Get<WeaponAmmo>(f.Slot(0)).InMag);
        }

        // ---- 换弹（弹匣/备弹结算，契约 §3 序 2） ----

        [Test]
        public static void Reload_RefillsFromReserve_PublishesAmmoChanged()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f);
            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.MachineGun);
            f.Tick(1); // InMag 24

            f.StartReload(0); // 换弹 1.5s
            f.Tick(29); // 1.45s：仍在换弹
            var mid = f.World.Get<WeaponAmmo>(f.Slot(0));
            Assert.True(mid.IsReloading);
            Assert.Equal(24, mid.InMag);

            f.Tick(2); // 1.55s：到点补弹
            var done = f.World.Get<WeaponAmmo>(f.Slot(0));
            Assert.True(!done.IsReloading);
            Assert.Equal(25, done.InMag);   // 补满
            Assert.Equal(49, done.Reserve); // 备弹 -1
            // AmmoChanged（开火 1 次 + 换弹完成 1 次）
            Assert.Equal(2, f.AmmoEvents);
            Assert.Equal(25, f.LastInMag);
            Assert.Equal(49, f.LastReserve);
        }

        [Test]
        public static void Reload_PartialRefill_WhenReserveLow()
        {
            var f = Fixture.Create();
            var a0 = f.World.Get<WeaponAmmo>(f.Slot(0));
            a0.InMag = 3;
            a0.Reserve = 2; // 备弹不足 → 全补（契约 §3 序 2）
            f.World.Add(f.Slot(0), a0);
            f.StartReload(0);
            f.Tick(31); // 1.55s

            var done = f.World.Get<WeaponAmmo>(f.Slot(0));
            Assert.True(!done.IsReloading);
            Assert.Equal(5, done.InMag);    // 3+2
            Assert.Equal(0, done.Reserve);  // 备弹清空
        }

        [Test]
        public static void Reload_FullMag_NoLossOnCompletion()
        {
            var f = Fixture.Create();
            // 满弹匣强制换弹（测试侧绕过视图守卫）：ReloadSystem 到点补弹量=0，不损失弹药（契约 §3 序 2）
            f.StartReload(0);
            f.Tick(31);
            var done = f.World.Get<WeaponAmmo>(f.Slot(0));
            Assert.True(!done.IsReloading);
            Assert.Equal(25, done.InMag); // 满弹匣补弹量 0
            Assert.Equal(50, done.Reserve);
        }

        // ---- add_ammo（弹药箱拾取入口，契约 §4） ----

        [Test]
        public static void AddAmmo_RefillsReserve_CappedAtMaxReserve_PublishesPerSlot()
        {
            var f = Fixture.Create();
            var ok = f.CallBool(WeaponMod.AddAmmoCap, 1); // 每槽 +1 满弹匣
            Assert.True(ok);
            Assert.Equal(75, f.World.Get<WeaponAmmo>(f.Slot(0)).Reserve); // 50+25（MaxReserve=100 未达）
            Assert.Equal(32, f.World.Get<WeaponAmmo>(f.Slot(1)).Reserve); // 24+8
            Assert.Equal(4, f.AmmoEvents); // ×槽（契约 §4）

            f.CallBool(WeaponMod.AddAmmoCap, 10); // 大量补弹 → 封顶 MaxReserve
            Assert.Equal(100, f.World.Get<WeaponAmmo>(f.Slot(0)).Reserve); // 75+250 → min(100)
            Assert.Equal(48, f.World.Get<WeaponAmmo>(f.Slot(1)).Reserve);  // 32+80 → min(48)

            var mags = f.AmmoEvents;
            f.CallBool(WeaponMod.AddAmmoCap, 0); // ≤0 = 每槽 +1 满弹匣（契约 §4）
            Assert.Equal(100, f.World.Get<WeaponAmmo>(f.Slot(0)).Reserve); // 已封顶 MaxReserve=100
            Assert.Equal(48, f.World.Get<WeaponAmmo>(f.Slot(1)).Reserve);  // 已封顶 MaxReserve=48
            Assert.True(f.AmmoEvents > mags);
        }

        // ---- 武器切换（契约 §3 序 3） ----

        [Test]
        public static void Switch_UpdatesActive_PublishesWeaponSwitched_GetState()
        {
            var f = Fixture.Create();
            f.SwitchTo(1);
            f.Tick(1);
            Assert.Equal(1, f.World.Get<WeaponActive>(f.Domain).CurrentIndex);
            Assert.Equal(1, f.SwitchedEvents);
            Assert.Equal(1u, f.LastSwitchedSlot); // 字段1=slot（契约 §5）

            // get_state 反映当前槽（霰弹 8/24/8，契约 §4）
            var row = f.Call(WeaponMod.GetStateCap);
            Assert.Equal(1u, (uint)row[0]!);
            Assert.Equal(4u, (uint)row[1]!);
            Assert.Equal(8, (int)row[2]!);
            Assert.Equal(24, (int)row[3]!);
            Assert.Equal(8, (int)row[4]!);
        }

        [Test]
        public static void Switch_InvalidIndex_Rejected()
        {
            var f = Fixture.Create();
            f.SwitchTo(9); // 越界（契约 §3 序 3：槽位范围校验）
            f.Tick(1);
            Assert.Equal(0, f.World.Get<WeaponActive>(f.Domain).CurrentIndex);
            Assert.Equal(0, f.SwitchedEvents);
        }

        [Test]
        public static void Switch_ToReloadingTarget_Rejected()
        {
            var f = Fixture.Create();
            f.StartReload(1); // 槽 1 换弹中
            f.SwitchTo(1);
            f.Tick(1);
            Assert.Equal(0, f.World.Get<WeaponActive>(f.Domain).CurrentIndex); // 目标槽换弹中拒绝切入（契约 §3"非换弹中"）
            Assert.Equal(0, f.SwitchedEvents);
        }

        // ---- weapon:reset（新对局重置，契约 §4） ----

        [Test]
        public static void Reset_RestoresDefaults_Publishes()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f);
            f.SwitchTo(1);
            f.Tick(1);
            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.Shotgun);
            f.Tick(1); // 槽 1 打 1 发（InMag 3）
            f.StartReload(1); // 槽 1 进入换弹
            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.Shotgun);
            f.Tick(1); // 无请求残留

            var ok = f.CallBool(WeaponMod.ResetCap);
            Assert.True(ok);
            // 全部槽位复位默认值 + CurrentIndex=0（契约 §4）
            Assert.Equal(25, f.World.Get<WeaponAmmo>(f.Slot(0)).InMag);
            Assert.Equal(50, f.World.Get<WeaponAmmo>(f.Slot(0)).Reserve);
            Assert.Equal(8, f.World.Get<WeaponAmmo>(f.Slot(1)).InMag);
            Assert.Equal(24, f.World.Get<WeaponAmmo>(f.Slot(1)).Reserve);
            Assert.True(!f.World.Get<WeaponAmmo>(f.Slot(1)).IsReloading);
            Assert.Equal(0, f.World.Get<WeaponActive>(f.Domain).CurrentIndex);
            Assert.True(!f.World.Has<ShotRequest>(f.Domain), "reset 后 ShotRequest 应清空");
            Assert.True(!f.World.Has<SwitchRequest>(f.Domain), "reset 后 SwitchRequest 应清空");
            // 发 AmmoChanged×4 + WeaponSwitched(0)（契约 §4）
            Assert.True(f.AmmoEvents >= 4);
            Assert.Equal(0u, f.LastSwitchedSlot);
            Assert.True(f.SwitchedEvents >= 1);
        }

        // ---- 开火 → enemy 伤害链路（ModCall 二进制）+ 击杀闭环 ----

        [Test]
        public static void Fire_KillChain_TwoShotsKill_PublishesEnemyKilled()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f); // HP 100，机关枪 60/发
            for (var i = 0; i < 2; i++)
            {
                f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.MachineGun);
                f.Tick(1); // 机关枪间隔 0.015s：每 tick 冷却已过，可连发
            }
            Assert.Equal(0, Hp(f, enemy).Current); // 60×2=120 ≥ 100 → 致死
            Assert.True(f.World.Get<EnemyFlags>(new Entity(enemy)).IsDead, "致死未置 IsDead");
            Assert.Equal(1, f.KilledEvents); // enemy:EnemyKilled（score 计分入口，链路闭环）
            // 已死实体再打 → 无重复消息（enemy:damage 拒绝已死）
            f.WriteShot(enemy, true, 5f, 0f, 5f, (byte)WeaponKind.MachineGun);
            f.Tick(1);
            Assert.Equal(1, f.KilledEvents);
        }

        // ---- 投射物（M2，契约 §3 序 4 / §9） ----

        [Test]
        public static void Rocket_Explodes_DirectInnerOuter_DamageBands()
        {
            var f = Fixture.Create();
            var direct = SpawnTestEnemy(f, EnemyKind.Zombunny, 0f, 0f, 5f);   // 直击
            var inner = SpawnTestEnemy(f, EnemyKind.Zombunny, 0f, 0f, 7f);   // 距爆心 ~3m → 内圈 80
            var outer = SpawnTestEnemy(f, EnemyKind.Zombunny, 0f, 0f, 9f);   // 距爆心 ~5m → 外圈 40
            f.SwitchTo(2); // RocketLauncher
            f.Tick(1);
            // 瞄准 +Z 射程终点（IsHit=false，HitX/Y/Z=瞄准点，契约 §8：未命中=射程终点）
            f.WriteShot(0, false, 0f, 2f, 60f, (byte)WeaponKind.RocketLauncher);
            f.Tick(1);
            Assert.Equal(1, f.World.Store<Projectile>().Count); // 生成投射物实体（契约 §2）

            f.Tick(30); // 火箭 3.5/s：1.15s 飞到直击目标 → 爆炸
            Assert.Equal(0, f.World.Store<Projectile>().Count); // 爆炸后销毁
            Assert.Equal(0, Hp(f, direct).Current); // 直击 100 → 致死（契约 §9：直击 100）
            Assert.Equal(20, Hp(f, inner).Current); // 内圈 80（契约 §9：内圈 80@3m）
            Assert.Equal(60, Hp(f, outer).Current); // 外圈 40（契约 §9：外圈 40@6m）
            Assert.Equal(1, f.KilledEvents);        // 直击致死（AOE 未致死）
        }

        [Test]
        public static void Rocket_Titan_BlastImmunity_Absorbed()
        {
            var f = Fixture.Create();
            var titan = SpawnTestEnemy(f, EnemyKind.Titan, 0f, 0f, 5f); // BlastImmunity（契约 §9）
            f.SwitchTo(2);
            f.Tick(1);
            f.WriteShot(0, false, 0f, 2f, 60f, (byte)WeaponKind.RocketLauncher);
            f.Tick(30); // 命中爆炸，sourceKind=Blast → enemy:damage 内裁决吸收（契约 §4/§0.7）

            Assert.Equal(2000, Hp(f, titan).Current); // 爆炸被吸收，不掉血
            Assert.Equal(0, f.World.Store<Projectile>().Count); // 仍碰撞销毁
        }

        [Test]
        public static void IceBullet_HomesToNearest_DamagesAndSlows()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 0f, 0f, 3f); // HP 100
            f.SwitchTo(3); // CryoPistol
            f.Tick(1);
            f.WriteShot(0, false, 0f, 2f, 10f, (byte)WeaponKind.CryoPistol); // 瞄准 +Z（偏离目标 → 验证追踪）
            f.Tick(20); // 冰弹 5/s 追踪命中

            Assert.Equal(0, f.World.Store<Projectile>().Count); // 命中后销毁
            Assert.Equal(70, Hp(f, enemy).Current); // 30 伤害（契约 §9：CryoPistol 30）
            var mv = f.World.Get<EnemyMovement>(new Entity(enemy));
            Assert.Equal(0.6f, mv.SpeedMul); // 缓速 40%（契约 §9，enemy:apply_slow 链路）
            Assert.True(mv.EffectTimer > 1.0f, $"缓速时长不足: {mv.EffectTimer}");
        }

        [Test]
        public static void Tornado_Spawn_TickDamage_Expires_Cooldown()
        {
            var f = Fixture.Create();
            var inner = SpawnTestEnemy(f, EnemyKind.Zombunny, 0f, 0f, 2f); // 距龙卷风 ≤4m → 8/tick
            var outer = SpawnTestEnemy(f, EnemyKind.Zombunny, 0f, 0f, 9f); // 龙卷风路径全程保持在 4-10m 外圈 → 2/tick
            f.World.Add(f.Domain, new TornadoRequest { X = 0f, Y = 1f, Z = 0f, DirX = 0f, DirZ = 1f }); // 副手 Fire2（契约 §8）
            f.Tick(20); // 1s：0.2s tick ×5

            Assert.Equal(1, f.World.Store<Projectile>().Count); // Tornado 实体（M2）
            Assert.Equal(60, Hp(f, inner).Current); // 8×5=40（契约 §9：8/tick(0.2s)）
            Assert.Equal(90, Hp(f, outer).Current); // 2×5=10（契约 §9：外圈 2）

            // CD 30s：立即再发 → 拒绝（契约 §9：CD 30s）
            f.World.Add(f.Domain, new TornadoRequest { X = 0f, Y = 1f, Z = 0f, DirX = 0f, DirZ = 1f });
            f.Tick(1);
            Assert.Equal(1, f.World.Store<Projectile>().Count);

            // 生命倒计时（契约 §3 序 4：Life 到点销毁；原版 lifespan=4.7s）
            f.Tick(80); // 再 4s：总计 ~5s > 4.7s
            Assert.Equal(0, f.World.Store<Projectile>().Count);
        }

        // ---- weapon:fire 能力（可选，经输入触发，契约 §10 weapon_shot_request 形状） ----

        [Test]
        public static void FireCapability_WritesShotRequest_DamagesEnemy()
        {
            var f = Fixture.Create();
            var enemy = SpawnTestEnemy(f, EnemyKind.Zombunny, 5f, 0f, 5f);
            var ok = f.CallBool(WeaponMod.FireCap, 0u, enemy, true, 5f, 0f, 5f, (uint)WeaponKind.MachineGun);
            Assert.True(ok, "weapon:fire 应写 ShotRequest");
            Assert.True(f.World.Has<ShotRequest>(f.Domain));
            f.Tick(1);
            Assert.Equal(40, Hp(f, enemy).Current); // FireSystem 消费 → enemy:damage（契约 §3）

            // 非法入参 → [false]（形状校验）
            var bad = f.Call(WeaponMod.FireCap, 1u);
            Assert.True(bad.Length > 0 && bad[0] is bool b && !b, "非法入参应返回 [false]");
        }

        // ---- 契约测试向量（§14.11.3）：owner 规范字节 ↔ 样例一致 ----

        [Test]
        public static void TestVectors_DataCodecShapes_RoundTrip()
        {
            foreach (var v in WeaponTestVectors.All())
            {
                if (v.ContractId is WeaponTestVectors.AmmoMsgContract or WeaponTestVectors.SwitchedMsgContract)
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
            // 消费方（ui.HudAmmo / ui.HudGunText / item.ItemPickup）各自重复定义的解析器（Rule 19）
            // 解析 owner 规范字节，与样例一致 → "可重复定义未漂移"（§14.11.3）
            foreach (var v in WeaponTestVectors.All())
            {
                switch (v.ContractId)
                {
                    case WeaponTestVectors.AmmoMsgContract:
                        Assert.True(HudAmmo.TryRead(v.Bytes, out var slot, out var inMag, out var reserve, out var maxMag),
                            $"解析失败: {v}");
                        Assert.Equal((uint)v.Sample[0]!, slot);
                        Assert.Equal((int)v.Sample[1]!, inMag);
                        Assert.Equal((int)v.Sample[2]!, reserve);
                        Assert.Equal((int)v.Sample[3]!, maxMag);
                        break;
                    case WeaponTestVectors.SwitchedMsgContract:
                        Assert.True(HudGunText.TryRead(v.Bytes, out var s), $"解析失败: {v}");
                        Assert.Equal((uint)v.Sample[0]!, s);
                        break;
                }
            }
        }

        // ---- 卸载清理（Rule 20 / §9.2 / §13.5） ----

        [Test]
        public static void Unload_CleansSystemsEntitiesCapabilities()
        {
            var f = Fixture.Create();
            var domainId = WeaponMod.ActiveEntityId;
            var slotId = WeaponMod.SlotEntityIds[0];
            Assert.True(domainId != 0 && slotId != 0);
            Assert.Equal(5, f.Host.Systems.CountOf(WeaponId));
            // 实体：player(1) + enemy 生成器(1) + weapon 域(1) + 槽位(4) = 7
            Assert.Equal(7, f.World.EntityCount);

            f.Host.Manager.Unload(WeaponId);

            Assert.True(!f.Host.Manager.IsLoaded(WeaponId));
            Assert.Equal(0, f.Host.Systems.CountOf(WeaponId));       // 系统移除（§9.2）
            Assert.True(!f.World.Exists(new Entity(domainId)));      // 域实体销毁
            Assert.True(!f.World.Exists(new Entity(slotId)));        // 槽位实体销毁
            Assert.Equal(0, f.World.Store<Projectile>().Count);      // 投射物清理
            Assert.Equal(2, f.World.EntityCount);                    // 仅剩 player + enemy 生成器
            Assert.Equal(0, f.Host.Messages.SubscriptionCount(WeaponId)); // 本 Mod 无订阅残留（只发布）
            Assert.Equal(0u, WeaponMod.ActiveEntityId);              // 静态桥清空
        }

        /// <summary>声明依赖 weapon 的探针 Mod（验证卸载后能力调用 → NoModException，§12.11 规则 2）。</summary>
        public sealed class WeaponProbeMod : IMod
        {
            public static readonly ModId Id = new("com.test.weaponprobe");
            public static Exception? CallError;

            public void Register(IModContext context) { }
            public void Unregister(IModContext context) { }

            public static void TryCall(IModContext ctx)
            {
                try { ModCall.Invoke(ctx.Mods, WeaponId, WeaponMod.GetStateCap); }
                catch (Exception e) { CallError = e; }
            }
        }

        [Test]
        public static void Unload_CapabilityCall_ThrowsNoMod()
        {
            var f = Fixture.Create();
            f.Host.Manager.Unload(WeaponId);

            f.Host.Load(new ModManifest
            {
                ModId = WeaponProbeMod.Id,
                Version = new ModVersion(1, 0, 0),
                Entry = typeof(WeaponProbeMod).FullName!,
                SharedModule = "test.dll",
                Dependencies = { new ModDependency { Id = WeaponId, Version = VersionRange.Any, Optional = false } },
            }, typeof(WeaponProbeMod).Assembly);
            var probeCtx = f.Host.Manager.Get(WeaponProbeMod.Id)!.Context;

            WeaponProbeMod.CallError = null;
            WeaponProbeMod.TryCall(probeCtx);
            Assert.True(WeaponProbeMod.CallError is NoModException,
                $"期望 NoModException，实际 {WeaponProbeMod.CallError}");
        }

        // ---- 消费方风格解析器（Rule 19：结构相同、定义独立，模拟 ui/item 各自重复定义） ----

        /// <summary>消费方 ui.HudAmmo：weapon:AmmoChanged 解析器（[1]=slot [2]=inMag [3]=reserve [4]=maxMag）。</summary>
        public sealed class HudAmmo
        {
            public static bool TryRead(byte[] bytes, out uint slot, out int inMag, out int reserve, out int maxMag)
            {
                slot = 0; inMag = 0; reserve = 0; maxMag = 0;
                var reader = new PayloadReader(bytes);
                if (!reader.TryReadUInt32(1, out slot)) return false;
                if (!reader.TryReadInt32(2, out inMag)) return false;
                if (!reader.TryReadInt32(3, out reserve)) return false;
                if (!reader.TryReadInt32(4, out maxMag)) return false;
                return true;
            }
        }

        /// <summary>消费方 ui.HudGunText：weapon:WeaponSwitched 解析器（[1]=slot）。</summary>
        public sealed class HudGunText
        {
            public static bool TryRead(byte[] bytes, out uint slot)
            {
                slot = 0;
                var reader = new PayloadReader(bytes);
                return reader.TryReadUInt32(1, out slot);
            }
        }
    }
}
