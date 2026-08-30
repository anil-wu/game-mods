using Com.Fps.Weapon;
using Com.Game.Network;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// com.fps.weapon 端到端：开火（冷却/弹药）→ 服务端 ray-sphere 命中 →
    /// 伤害经 ModCall → 死亡/重生；换弹计时；武器状态与开火事件广播。
    /// </summary>
    public static class FpsWeaponTests
    {
        private static readonly ModId PlayerId = new("com.fps.player");
        private static readonly ModId WeaponId = new("com.fps.weapon");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public World World => Host.World;
            public IModContext WeaponCtx = null!;
            public IModContext PlayerCtx = null!;
            public int ShotBusEvents;
            public uint LastShotHit;

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.network")),
                    typeof(NetworkMod).Assembly);
                var runtime = NetworkMod.Current!;
                runtime.AttachTransport(new LoopbackTransport());
                runtime.Start(new NetworkConfig { Role = NetworkRole.Host });

                // player 在前（weapon 声明依赖）
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.player")),
                    typeof(Com.Fps.Player.PlayerMod).Assembly,
                    RepoPaths.SampleModDir("MirrorUnityFPS", "com.fps.player"));
                f.PlayerCtx = f.Host.Manager.Get(PlayerId)!.Context;
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.weapon")),
                    typeof(WeaponMod).Assembly,
                    RepoPaths.SampleModDir("MirrorUnityFPS", "com.fps.weapon"));
                f.WeaponCtx = f.Host.Manager.Get(WeaponId)!.Context;

                f.Host.Messages.Subscribe(new ModId("com.test.runner"), WeaponMod.ShotFiredEvent,
                    (in Game.Messaging.MessageEnvelope _, Game.Mod.Contract.Wire.PayloadReader reader) =>
                    {
                        f.ShotBusEvents++;
                        if (reader.TryReadUInt32(2, out var hit)) f.LastShotHit = hit;
                    });
                return f;
            }

            public void Tick(int frames, float dt = 0.05f)
            {
                for (var i = 0; i < frames; i++) Host.Tick(dt);
            }

            public void Fire(float yaw)
                => WeaponCtx.Network.SendToServer(new FireProtocol().Id, new FireRequest(yaw));

            public uint LocalPlayerId => Com.Fps.Player.PlayerMod.LocalPlayerEntityId;

            /// <summary>把本地玩家传送到 (x, z)（对齐靶标弹道）。</summary>
            public void TeleportLocalPlayer(float x, float z)
            {
                var e = new Entity(LocalPlayerId);
                var p = World.Get<Com.Fps.Player.Position3>(e);
                p.X = x; p.Z = z;
                World.Add(e, p);
            }

            /// <summary>取一个靶标实体 id（非本地玩家）。</summary>
            public uint DummyId()
            {
                var all = ModCall.Invoke(PlayerCtx.Mods, PlayerId, Com.Fps.Player.PlayerMod.GetAllPositionsCap);
                foreach (var row in all)
                {
                    var a = (object?[])row!;
                    if ((uint)a[0]! != LocalPlayerId) return (uint)a[0]!;
                }
                return 0;
            }
        }

        [Test]
        public static void Fire_HitsDummy_AmmoDecreases_StateBroadcast()
        {
            var f = Fixture.Create();
            f.Tick(10); // WeaponState 懒挂 + player:local 指派

            // 传送到靶标 A(-3, Z=5) 正后方，朝 +Z 开火必中
            f.TeleportLocalPlayer(-3f, 0f);
            f.Fire(0f);
            f.Tick(3);

            // 弹药 30 → 29（WeaponState 在本地玩家实体上）
            var state = f.World.Get<WeaponState>(new Entity(f.LocalPlayerId));
            Assert.Equal(WeaponConfig.MaxAmmo - 1, state.Ammo);

            // 靶标掉血（34）
            var dummy = new Entity(f.DummyId());
            var hp = f.World.Get<Com.Fps.Player.Health>(dummy);
            Assert.Equal(100 - WeaponConfig.Damage, hp.Current);

            // weapon:shot 广播（hitNetId != 0）与 weapon:state 广播已落地客户端缓存
            Assert.True(WeaponMod.ClientWeaponStates.ContainsKey(1u));
            Assert.Equal(WeaponConfig.MaxAmmo - 1, WeaponMod.ClientWeaponStates[1u].Ammo);

            // 总线事件（hit = 靶标实体）
            Assert.Equal(1, f.ShotBusEvents);
            Assert.Equal(dummy.Id, f.LastShotHit);
        }

        [Test]
        public static void Fire_Cooldown_Enforced()
        {
            var f = Fixture.Create();
            f.Tick(10);

            f.Fire(0f);
            f.Tick(1);
            f.Fire(0f); // 冷却 0.25s 内 → 忽略
            f.Tick(1);

            var state = f.World.Get<WeaponState>(new Entity(f.LocalPlayerId));
            Assert.Equal(WeaponConfig.MaxAmmo - 1, state.Ammo); // 只打出 1 发

            f.Tick(5); // 过冷却
            f.Fire(0f);
            f.Tick(1);
            state = f.World.Get<WeaponState>(new Entity(f.LocalPlayerId));
            Assert.Equal(WeaponConfig.MaxAmmo - 2, state.Ammo);
        }

        [Test]
        public static void Fire_Miss_NoDamage()
        {
            var f = Fixture.Create();
            f.Tick(10);

            // 朝 -Z（背向靶标）开火
            f.Fire(180f);
            f.Tick(3);

            var dummy = new Entity(f.DummyId());
            var hp = f.World.Get<Com.Fps.Player.Health>(dummy);
            Assert.Equal(100, hp.Current); // 未命中不掉血
            Assert.Equal(0u, f.LastShotHit); // 事件 hit=0
        }

        [Test]
        public static void Kill_Dummy_Then_Respawns()
        {
            var f = Fixture.Create();
            f.Tick(10);

            f.TeleportLocalPlayer(-3f, 0f);
            var dummyId = f.DummyId();
            // 3 发 × 34 = 102 > 100 → 击杀（间隔过冷却）
            for (var i = 0; i < 3; i++)
            {
                f.Fire(0f);
                f.Tick(6);
            }

            var dummy = new Entity(dummyId);
            var hp = f.World.Get<Com.Fps.Player.Health>(dummy);
            Assert.Equal(0, hp.Current);

            f.Tick(80); // 重生 3s
            hp = f.World.Get<Com.Fps.Player.Health>(dummy);
            Assert.Equal(hp.Max, hp.Current);
        }

        [Test]
        public static void Reload_Refills_After_Duration()
        {
            var f = Fixture.Create();
            f.Tick(10);

            f.Fire(0f);
            f.Tick(2);
            f.WeaponCtx.Network.SendToServer(new ReloadProtocol().Id, ReloadRequest.Instance);
            f.Tick(3);

            // 换弹中（未满）
            var state = f.World.Get<WeaponState>(new Entity(f.LocalPlayerId));
            Assert.Equal(WeaponConfig.MaxAmmo - 1, state.Ammo);

            f.Tick(25); // 1s+ 换弹完成
            state = f.World.Get<WeaponState>(new Entity(f.LocalPlayerId));
            Assert.Equal(WeaponConfig.MaxAmmo, state.Ammo);
            Assert.Equal(WeaponConfig.MaxAmmo, WeaponMod.ClientWeaponStates[1u].Ammo); // 广播同步
        }

        [Test]
        public static void Dead_Shooter_CannotFire()
        {
            var f = Fixture.Create();
            f.Tick(10);

            // 直接击杀本地玩家（ModCall）
            ModCall.Invoke(f.PlayerCtx.Mods, PlayerId, Com.Fps.Player.PlayerMod.ApplyDamageCap,
                f.LocalPlayerId, 999, 0u);

            var ammoBefore = f.World.Get<WeaponState>(new Entity(f.LocalPlayerId)).Ammo;
            f.Fire(0f);
            f.Tick(3);
            var ammoAfter = f.World.Get<WeaponState>(new Entity(f.LocalPlayerId)).Ammo;
            Assert.Equal(ammoBefore, ammoAfter); // 尸体不开枪
        }
    }
}
