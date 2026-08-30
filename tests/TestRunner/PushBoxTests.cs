using Com.Game.Network;
using Com.Sample.PushBox;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// 推箱子端到端（V2 全链路）：
    /// 输入 → Network.Mod 协议（Host 回环管线，§11.3）→ Server 权威 ECS →
    /// 判胜 → 广播 → Client 落地本地状态 + MessageBus 二进制事件（§12.9 模式 3）。
    /// </summary>
    public static class PushBoxTests
    {
        private static readonly ModId PushBoxId = new("com.sample.pushbox");
        private static readonly ModId TestSubscriber = new("com.test.runner");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public IModContext PushBoxCtx = null!;
            public PushInputProtocol InputProtocol = new();
            public int GameWonEvents;
            public PlayerSide? EventWinner;

            public static Fixture Create()
            {
                var f = new Fixture
                {
                    Host = new ModRuntimeHost(RuntimeRole.Host),
                };

                // 1. 框架 Mod 在前：Network.Mod（§10.4 加载顺序最前）
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.network")),
                    typeof(NetworkMod).Assembly);
                var runtime = (INetworkRuntime)f.Host.Services.Get(WellKnownServices.NetworkRuntime);
                runtime.AttachTransport(new LoopbackTransport());
                runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });

                // 2. 业务 Mod：推箱子（从真实 mod.json 加载清单）
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.sample.pushbox")),
                    typeof(PushBoxMod).Assembly, RepoPaths.ModDir("com.sample.pushbox"));
                f.PushBoxCtx = f.Host.Manager.Get(PushBoxId)!.Context;

                // 3. 订阅本地 game_won 事件（二进制载荷）
                f.Host.Messages.Subscribe(TestSubscriber, PushBoxMod.GameWonEvent,
                    (in Game.Messaging.MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.GameWonEvents++;
                        if (reader.TryReadByte(1, out var w))
                            f.EventWinner = (PlayerSide)w;
                    });
                return f;
            }

            public void Push(PlayerSide side, bool pushing)
                => PushBoxCtx.Network.SendToServer(InputProtocol.Id, new PushInput(side, pushing));

            public void Tick(int frames, float dt = 0.05f)
            {
                for (var i = 0; i < frames; i++) Host.Tick(dt);
            }
        }

        [Test]
        public static void A_Wins()
        {
            var f = Fixture.Create();
            f.Push(PlayerSide.Left, true);   // A 推（→）
            f.Tick(400);                     // 20s 游戏时间

            var session = PushBoxMod.World.Get<Session>(new Entity(PushBoxMod.SessionEntityId));
            Assert.Equal(GamePhase.Won, session.Phase);
            Assert.Equal(PlayerSide.Left, session.Winner);

            // Host 回环：Server 广播 → Client Handler 落地本地状态（§12.9 模式 3）
            Assert.Equal(PlayerSide.Left, PushBoxMod.ClientWinner);
            // MessageBus 二进制事件已发布（谁定义谁发布，Rule 11）
            Assert.Equal(1, f.GameWonEvents);
            Assert.Equal(PlayerSide.Left, f.EventWinner);
        }

        [Test]
        public static void B_Wins()
        {
            var f = Fixture.Create();
            f.Push(PlayerSide.Right, true);  // B 推（←）
            f.Tick(400);

            var session = PushBoxMod.World.Get<Session>(new Entity(PushBoxMod.SessionEntityId));
            Assert.Equal(GamePhase.Won, session.Phase);
            Assert.Equal(PlayerSide.Right, session.Winner);
            Assert.Equal(PlayerSide.Right, PushBoxMod.ClientWinner);
        }

        [Test]
        public static void Stalemate()
        {
            var f = Fixture.Create();
            f.Push(PlayerSide.Left, true);
            f.Push(PlayerSide.Right, true);  // 双方对推 → 合力为 0
            f.Tick(200);

            var session = PushBoxMod.World.Get<Session>(new Entity(PushBoxMod.SessionEntityId));
            Assert.Equal(GamePhase.Running, session.Phase);
            Assert.Equal(0, f.GameWonEvents);
        }

        [Test]
        public static void Protocols_Registered_WithStableIds()
        {
            var f = Fixture.Create();
            var runtime = NetworkMod.Current!;
            // Rule 16：稳定哈希 ID，与声明的协议清单一一对应（mod.json network.protocols）
            Assert.True(runtime.IsRegistered(ProtocolId.Of("com.sample.pushbox:input")));
            Assert.True(runtime.IsRegistered(ProtocolId.Of("com.sample.pushbox:game_won")));
        }

        [Test]
        public static void ModResources_LoadDataFile()
        {
            var f = Fixture.Create();
            var mod = f.Host.Manager.Get(PushBoxId)!;
            // Mod 自治资源域（§8）：包内数据文件经 AssetId 命名空间加载
            var json = (string)mod.Resources.Load(AssetId.Parse("com.sample.pushbox:data/arena.json"));
            Assert.True(json.Contains("arena") || json.Contains("{"));
        }

        [Test]
        public static void SelfUnload_QuitGameToLobby()
        {
            // 退出推箱子（非整个应用）：Mod 经 context.Mods 合法通道热卸载自身，返回大厅
            var f = Fixture.Create();
            f.Push(PlayerSide.Left, true);
            f.Tick(50);

            f.PushBoxCtx.Mods.Unload(PushBoxId);

            // 游戏 Mod 完全卸载：协议 / 系统 / 事件全部清理
            var runtime = NetworkMod.Current!;
            Assert.True(!f.Host.Manager.IsLoaded(PushBoxId));
            Assert.True(!runtime.IsRegistered(ProtocolId.Of("com.sample.pushbox:input")));
            Assert.Equal(0, f.Host.Systems.CountOf(PushBoxId));
            // 大厅存活：框架 Mod（pinned）与其他 Mod 不受影响
            Assert.True(f.Host.Manager.IsLoaded(new ModId("com.game.network")));
            Assert.True(runtime.IsActive);
        }

        [Test]
        public static void Unload_CleansNetworkAndMessages()
        {
            var f = Fixture.Create();
            var runtime = NetworkMod.Current!;

            f.Host.Manager.Unload(PushBoxId);

            // 协议按 OwnerMod 一次性清理（§11.6）
            Assert.True(!runtime.IsRegistered(ProtocolId.Of("com.sample.pushbox:input")));
            Assert.True(!runtime.IsRegistered(ProtocolId.Of("com.sample.pushbox:game_won")));
            // 系统移除（§9.2）
            Assert.Equal(0, f.Host.Systems.CountOf(PushBoxId));
        }
    }
}
