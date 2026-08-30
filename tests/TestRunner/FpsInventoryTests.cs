using Com.Fps.Inventory;
using Com.Fps.Weapon;
using Com.Game.Network;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>com.fps.inventory 端到端：拾取物复制 / 拾取校验 / 效果（治疗/补弹）/ 销毁。</summary>
    public static class FpsInventoryTests
    {
        private static readonly ModId InventoryId = new("com.fps.inventory");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public World World => Host.World;
            public IModContext InventoryCtx = null!;
            public IModContext PlayerCtx = null!;
            public int PickedEvents;

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.network")), typeof(NetworkMod).Assembly);
                var runtime = NetworkMod.Current!;
                runtime.AttachTransport(new LoopbackTransport());
                runtime.Start(new NetworkConfig { Role = NetworkRole.Host });

                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.player")),
                    typeof(Com.Fps.Player.PlayerMod).Assembly);
                f.PlayerCtx = f.Host.Manager.Get(new ModId("com.fps.player"))!.Context;
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.weapon")),
                    typeof(WeaponMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.inventory")),
                    typeof(InventoryMod).Assembly);
                f.InventoryCtx = f.Host.Manager.Get(InventoryId)!.Context;
                return f;
            }

            public void Tick(int frames, float dt = 0.05f)
            {
                for (var i = 0; i < frames; i++) Host.Tick(dt);
            }

            public uint LocalPlayer => Com.Fps.Player.PlayerMod.LocalPlayerEntityId;
        }

        [Test]
        public static void Spawn_Pickups_Replicated()
        {
            var f = Fixture.Create();
            f.Tick(10);
            // 3 个拾取物（netId 4..6，前 3 是玩家实体）
            var runtime = NetworkMod.Current!;
            Assert.True(runtime.TryGetEntity(4u, out _));
            Assert.True(runtime.TryGetEntity(5u, out _));
            Assert.True(runtime.TryGetEntity(6u, out _));
        }

        [Test]
        public static void Pickup_Health_Heals_LocalPlayer()
        {
            var f = Fixture.Create();
            f.Tick(10);

            // 先打掉一半血（经能力）
            var local = new Entity(f.LocalPlayer);
            var hp = f.World.Get<Com.Fps.Player.Health>(local);
            hp.Current = 50;
            f.World.Add(local, hp);

            // 本地玩家在 (0,0.5,0)；治疗包在 (-5,0.5,3) 超出 2.5m → 校验失败不掉血不生效
            // 先传送到治疗包旁（本地玩家实体是服务端权威实体，直接改）
            var pos = f.World.Get<Com.Fps.Player.Position3>(local);
            pos.X = -5f; pos.Z = 3f;
            f.World.Add(local, pos);

            // 拾取物 netId 4 = 治疗包
            f.InventoryCtx.Network.SendToServer(new PickupProtocol().Id, new PickupRequest(4u));
            f.Tick(3);

            Assert.Equal(90, f.World.Get<Com.Fps.Player.Health>(local).Current); // 50 + 40
            // 拾取物已销毁（netId 4 不再存在）
            var runtime = NetworkMod.Current!;
            Assert.True(!runtime.TryGetEntity(4u, out _));
        }

        [Test]
        public static void Pickup_Ammo_Refills_Weapon()
        {
            var f = Fixture.Create();
            f.Tick(10);

            var local = new Entity(f.LocalPlayer);
            // 打空弹药
            f.World.Add(local, new WeaponState { Ammo = 0, MaxAmmo = WeaponConfig.MaxAmmo });

            // 传送到弹药箱旁（5,0.5,3），netId 5 = 弹药箱
            var pos = f.World.Get<Com.Fps.Player.Position3>(local);
            pos.X = 5f; pos.Z = 3f;
            f.World.Add(local, pos);

            f.InventoryCtx.Network.SendToServer(new PickupProtocol().Id, new PickupRequest(5u));
            f.Tick(3);

            Assert.Equal(15, f.World.Get<WeaponState>(local).Ammo); // 0 + 15
        }

        [Test]
        public static void Pickup_TooFar_Rejected()
        {
            var f = Fixture.Create();
            f.Tick(10);
            // 玩家在 (0,0.5,0)，不靠近任何拾取物 → 拾取失败，拾取物仍在
            f.InventoryCtx.Network.SendToServer(new PickupProtocol().Id, new PickupRequest(4u));
            f.Tick(3);
            var runtime = NetworkMod.Current!;
            Assert.True(runtime.TryGetEntity(4u, out _));
        }
    }
}
