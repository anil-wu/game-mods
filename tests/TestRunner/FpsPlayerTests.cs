using Com.Fps.Player;
using Com.Game.Network;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// com.fps.player 端到端：输入 C2S → 服务端权威移动 → Replication 回客户端；
    /// 生命/伤害（ModCall）/死亡事件/重生；本地玩家指派。
    /// </summary>
    public static class FpsPlayerTests
    {
        private static readonly ModId PlayerId = new("com.fps.player");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public World World => Host.World;
            public IModContext PlayerCtx = null!;
            public int DiedEvents;
            public uint LastDead;

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.network")),
                    typeof(NetworkMod).Assembly);
                var runtime = NetworkMod.Current!;
                runtime.AttachTransport(new LoopbackTransport());
                runtime.Start(new NetworkConfig { Role = NetworkRole.Host });

                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("MirrorUnityFPS", "com.fps.player")),
                    typeof(PlayerMod).Assembly, RepoPaths.SampleModDir("MirrorUnityFPS", "com.fps.player"));
                f.PlayerCtx = f.Host.Manager.Get(PlayerId)!.Context;

                f.Host.Messages.Subscribe(new ModId("com.test.runner"), PlayerMod.DiedEvent,
                    (in Game.Messaging.MessageEnvelope _, Game.Mod.Contract.Wire.PayloadReader reader) =>
                    {
                        f.DiedEvents++;
                        if (reader.TryReadUInt32(1, out var dead)) f.LastDead = dead;
                    });
                return f;
            }

            public void Tick(int frames, float dt = 0.05f)
            {
                for (var i = 0; i < frames; i++) Host.Tick(dt);
            }

            public void SendInput(float moveX, float moveZ, float yaw = 0f, bool jump = false)
                => PlayerCtx.Network.SendToServer(new InputProtocol().Id,
                    new InputState(moveX, moveZ, yaw, jump));

            public Entity LocalPlayer => new(PlayerMod.LocalPlayerEntityId);
        }

        [Test]
        public static void Spawn_Players_Replicated()
        {
            var f = Fixture.Create();
            f.Tick(10);

            // 本地玩家 + 2 靶标 = 3 个复制实体（netId 1..3）
            var runtime = NetworkMod.Current!;
            Assert.True(runtime.TryGetEntity(1u, out _));
            Assert.True(runtime.TryGetEntity(2u, out _));
            Assert.True(runtime.TryGetEntity(3u, out _));
            // player:local 指派已广播
            Assert.Equal(1u, PlayerMod.LocalPlayerNetId);
        }

        [Test]
        public static void Movement_ServerAuthoritative_ReplicatesToClient()
        {
            var f = Fixture.Create();
            f.Tick(5);

            // 前进（yaw=0 → +Z）：客户端上报输入 → 服务端积分 → 复制回客户端
            f.SendInput(0f, 1f);
            f.Tick(20); // 1s

            // 服务端位置（同一 World，Host 下本地实体即权威实体）
            var serverPos = f.World.Get<Position3>(f.LocalPlayer);
            Assert.True(serverPos.Z > 2f, $"服务端未前进: Z={serverPos.Z}");

            // 复制副本（netId 1）位置跟上（快照@15Hz 有一帧级延迟，容差半个身位）
            var runtime = NetworkMod.Current!;
            Assert.True(runtime.TryGetEntity(1u, out var replica));
            var replicaPos = f.World.Get<Position3>(replica);
            Assert.True(System.Math.Abs(serverPos.Z - replicaPos.Z) < 0.5f,
                $"副本未跟上权威位置: server={serverPos.Z}, replica={replicaPos.Z}");

            // 停止输入 → 应停止（速度由输入驱动）
            f.SendInput(0f, 0f);
            f.Tick(10);
            var z1 = f.World.Get<Position3>(f.LocalPlayer).Z;
            f.Tick(10);
            var z2 = f.World.Get<Position3>(f.LocalPlayer).Z;
            Assert.Equal(z1, z2);
        }

        [Test]
        public static void Movement_YawRelative()
        {
            var f = Fixture.Create();
            f.Tick(5);
            // yaw=90°（朝 +X）前进 → 应沿 +X 移动
            f.SendInput(0f, 1f, yaw: 90f);
            f.Tick(20);
            var pos = f.World.Get<Position3>(f.LocalPlayer);
            Assert.True(pos.X > 2f, $"未沿偏航方向移动: X={pos.X}");
            Assert.True(System.Math.Abs(pos.Z) < 1f, $"不应沿 Z 移动: Z={pos.Z}");
        }

        [Test]
        public static void Jump_And_Gravity()
        {
            var f = Fixture.Create();
            f.Tick(5);
            f.SendInput(0f, 0f, jump: true);
            f.Tick(4);
            var yMid = f.World.Get<Position3>(f.LocalPlayer).Y;
            Assert.True(yMid > PlayerConfig.GroundY + 0.05f, $"未起跳: Y={yMid}");
            f.SendInput(0f, 0f);
            f.Tick(40); // 2s 落回地面
            var yEnd = f.World.Get<Position3>(f.LocalPlayer).Y;
            Assert.Equal(PlayerConfig.GroundY, yEnd);
        }

        [Test]
        public static void ApplyDamage_ViaModCall_And_DeathEvent()
        {
            var f = Fixture.Create();
            f.Tick(5);

            // 经能力调用对靶标施加伤害（BCL 形状：object[] 行集，契约见 CONTRACT.md）
            var all = (object[])f.PlayerCtx.Mods.Call(
                PlayerId, PlayerMod.GetAllPositionsCap, null)!;
            Assert.Equal(3, all.Length);
            uint dummyId = 0;
            foreach (var row in all)
            {
                var a = (object[])row;
                if ((uint)a[0] != PlayerMod.LocalPlayerEntityId) { dummyId = (uint)a[0]; break; }
            }
            Assert.True(dummyId != 0);

            var died = (bool)f.PlayerCtx.Mods.Call(
                PlayerId, PlayerMod.ApplyDamageCap,
                new object[] { dummyId, 150, PlayerMod.LocalPlayerEntityId })!;
            Assert.True(died);
            Assert.Equal(1, f.DiedEvents);
            Assert.Equal(dummyId, f.LastDead);

            // 死亡后 Alive=false（能力快照行：[5]=Alive）
            var dto = (object[])f.PlayerCtx.Mods.Call(
                PlayerId, PlayerMod.GetPositionCap, dummyId)!;
            Assert.True(!(bool)dto[5]);
        }

        [Test]
        public static void Respawn_AfterDelay()
        {
            var f = Fixture.Create();
            f.Tick(5);
            var died = (bool)f.PlayerCtx.Mods.Call(
                PlayerId, PlayerMod.ApplyDamageCap,
                new object[] { PlayerMod.LocalPlayerEntityId, 999, 0u })!;
            Assert.True(died);

            f.Tick(80); // 4s > RespawnDelay
            var hp = f.World.Get<Health>(f.LocalPlayer);
            Assert.Equal(hp.Max, hp.Current); // 满血重生
            var pos = f.World.Get<Position3>(f.LocalPlayer);
            Assert.Equal(0f, pos.X); // 回出生点
        }

        /// <summary>声明依赖 player 的探针 Mod（验证卸载后能力调用行为）。</summary>
        public sealed class PlayerProbeMod : IMod
        {
            public static readonly ModId Id = new("com.test.playerprobe");
            public static System.Exception? CallError;

            public void Register(IModContext context) { }
            public void Unregister(IModContext context) { }

            public static void TryCall(IModContext ctx)
            {
                try { ctx.Mods.Call(PlayerId, PlayerMod.GetAllPositionsCap, null); }
                catch (System.Exception e) { CallError = e; }
            }
        }

        [Test]
        public static void Unload_CleansEverything()
        {
            var f = Fixture.Create();
            f.Tick(10);

            f.Host.Manager.Unload(PlayerId);

            var runtime = NetworkMod.Current!;
            Assert.True(!runtime.IsRegistered(ProtocolId.Of("com.fps.player:input")));
            Assert.Equal(0, f.Host.Systems.CountOf(PlayerId));
            Assert.Equal(0, f.World.EntityCount); // 玩家/靶标/副本实体全部销毁

            // 卸载后加载探针（可选依赖声明）→ 能力调用得到 NoModException（§12.11 规则 2）
            f.Host.Load(new ModManifest
            {
                ModId = PlayerProbeMod.Id,
                Version = new ModVersion(1, 0, 0),
                Entry = typeof(PlayerProbeMod).FullName!,
                SharedModule = "test.dll",
                Dependencies = { new ModDependency { Id = PlayerId, Version = VersionRange.Any, Optional = true } },
            }, typeof(PlayerProbeMod).Assembly);
            var probeCtx = f.Host.Manager.Get(PlayerProbeMod.Id)!.Context;

            PlayerProbeMod.CallError = null;
            PlayerProbeMod.TryCall(probeCtx);
            Assert.True(PlayerProbeMod.CallError is NoModException,
                $"期望 NoModException，实际 {PlayerProbeMod.CallError}");
        }
    }
}
