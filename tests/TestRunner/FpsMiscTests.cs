using Com.Fps.Console;
using Com.Fps.MapGen;
using Com.Fps.Npc;
using Com.Fps.Weapon;
using Com.Game.Network;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>mapgen / console / npc 端到端。</summary>
    public static class FpsMiscTests
    {
        private static readonly ModId PlayerId = new("com.fps.player");
        private static readonly ModId NpcId = new("com.fps.npc");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public World World => Host.World;

            public static Fixture Create(bool withNpc = true, bool withConsole = true)
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.network")), typeof(NetworkMod).Assembly);
                var runtime = NetworkMod.Current!;
                runtime.AttachTransport(new LoopbackTransport());
                runtime.Start(new NetworkConfig { Role = NetworkRole.Host });

                if (withConsole)
                    f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.console")),
                        typeof(ConsoleMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.player")),
                    typeof(Com.Fps.Player.PlayerMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.weapon")),
                    typeof(WeaponMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.mapgen")),
                    typeof(MapGenMod).Assembly);
                if (withNpc)
                    f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.npc")),
                        typeof(NpcMod).Assembly);
                return f;
            }

            public void Tick(int frames, float dt = 0.05f)
            {
                for (var i = 0; i < frames; i++) Host.Tick(dt);
            }

            public uint LocalPlayer => Com.Fps.Player.PlayerMod.LocalPlayerEntityId;
        }

        private static string[] EnumerateNpcRows(Fixture f)
        {
            var rows = new System.Collections.Generic.List<string>();
            var raw = (object[])f.Host.Manager.Get(NpcId)!.Context.Mods.Call(NpcId, NpcMod.GetAllNpcsCap, null)!;
            foreach (var row in raw) rows.Add(string.Join("/", (object[])row));
            return rows.ToArray();
        }

        // ---- mapgen ----

        [Test]
        public static void MapGen_Deterministic_SameSeedSameLayout()
        {
            var a = MapGenMod.GenerateBoxes(12345);
            var b = MapGenMod.GenerateBoxes(12345);
            Assert.Equal(a.Length, b.Length);
            for (var i = 0; i < a.Length; i++)
                Assert.Equal(a[i].X, b[i].X); // 同 seed 完全一致
            var c = MapGenMod.GenerateBoxes(1);
            Assert.True(a[0].X != c[0].X); // 不同 seed 不同布局
        }

        [Test]
        public static void MapGen_SeedBroadcast_ClientReceives()
        {
            var f = Fixture.Create(withNpc: false, withConsole: false);
            f.Tick(10);
            Assert.Equal(MapGenMod.DefaultSeed, MapGenMod.ClientSeed);
        }

        // ---- console ----

        [Test]
        public static void Console_RegisteredCommand_Executes()
        {
            var f = Fixture.Create(withNpc: false);
            f.Tick(10);

            // 打空弹药 → give_ammo 命令回满 15
            var local = new Entity(f.LocalPlayer);
            f.World.Add(local, new WeaponState { Ammo = 0, MaxAmmo = WeaponConfig.MaxAmmo });

            f.Host.Manager.Get(new ModId("com.fps.console"))!.Context.Network
                .SendToServer(new CommandProtocol().Id, new CommandMessage("give_ammo"));
            f.Tick(3);

            Assert.Equal(15, f.World.Get<WeaponState>(local).Ammo);
        }

        [Test]
        public static void Console_UnknownCommand_Reports()
        {
            var f = Fixture.Create(withNpc: false);
            f.Tick(10);
            f.Host.Manager.Get(new ModId("com.fps.console"))!.Context.Network
                .SendToServer(new CommandProtocol().Id, new CommandMessage("nope"));
            f.Tick(3);
            Assert.True(ConsoleMod.LastOutput.Contains("未知命令"));
        }

        // ---- npc ----

        [Test]
        public static void Npc_Spawns_Replicated()
        {
            var f = Fixture.Create();
            f.Tick(10);
            // 该夹具无 inventory：3 玩家后即 2 个 NPC（netId 4,5）
            var runtime = NetworkMod.Current!;
            Assert.True(runtime.TryGetEntity(4u, out _));
            Assert.True(runtime.TryGetEntity(5u, out _));
        }

        [Test]
        public static void Npc_ChasesAndAttacks_LocalPlayer()
        {
            var f = Fixture.Create();
            f.Tick(5);

            // 本地玩家满血；NPC 出生 (-6,0.5,6)/(6,0.5,6)，玩家 (0,0.5,0) 在仇恨范围 8 内（距离≈8.5 略超？传近点）
            var local = new Entity(f.LocalPlayer);
            var pos = f.World.Get<Com.Fps.Player.Position3>(local);
            pos.X = -5f; pos.Z = 5f; // 贴近 NPC A
            f.World.Add(local, pos);

            var hpBefore = f.World.Get<Com.Fps.Player.Health>(local).Current;
            f.Tick(120); // 6s：足够追击 + 数次攻击

            var hpAfter = f.World.Get<Com.Fps.Player.Health>(local).Current;
            Assert.True(hpAfter < hpBefore, $"NPC 未攻击玩家: {hpBefore} → {hpAfter}");
        }

        [Test]
        public static void Weapon_HitsNpc_NpcDies()
        {
            var f = Fixture.Create();
            f.Tick(10);

            // 取 x<0 的 NPC 当前位置（AI 可能已追击 dummy），对齐弹道
            var raw = (object[])f.Host.Manager.Get(NpcId)!.Context.Mods.Call(NpcId, NpcMod.GetAllNpcsCap, null)!;
            uint npcId = 0;
            float nx = 0f, nz = 0f;
            foreach (var row in raw)
            {
                var a = (object[])row;
                if ((float)a[1] < 0f) { npcId = (uint)a[0]; nx = (float)a[1]; nz = (float)a[3]; break; }
            }
            Assert.True(npcId != 0, $"NPC 未找到: [{string.Join(", ", EnumerateNpcRows(f))}]");

            // 本地玩家传送到 NPC 正后方 4m，朝 +Z 开火必中
            var local = new Entity(f.LocalPlayer);
            var pos = f.World.Get<Com.Fps.Player.Position3>(local);
            pos.X = nx; pos.Z = nz - 4f;
            f.World.Add(local, pos);

            for (var i = 0; i < 3; i++) // 34×3=102>100
            {
                f.Host.Manager.Get(new ModId("com.fps.weapon"))!.Context.Network
                    .SendToServer(new FireProtocol().Id, new FireRequest(0f));
                f.Tick(6);
            }

            var npcHp = f.World.Get<NpcHealth>(new Entity(npcId));
            Assert.Equal(0, npcHp.Current);
        }
    }
}
