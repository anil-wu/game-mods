using System;
using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;
using Com.Game.Protocol;
using Com.Sample.PushBox;

namespace TestRunner
{
    public static class PushBoxTests
    {
        private sealed class Env
        {
            public World World = new();
            public MessageBus Messages = new();
            public SystemGroup Systems;
            public ModId ModId;

            public Env()
            {
                Systems = new SystemGroup(Messages);
                ModId = new ModId("com.sample.pushbox");

                // 先加载协议核心 Mod（推箱子依赖它）
                var protoCtx = new TestModContext("com.game.protocol", "1.0.0", World, Systems, Messages);
                new ProtocolEntry().OnLoad(protoCtx);

                // 再加载推箱子 Mod
                var ctx = new TestModContext("com.sample.pushbox", "0.1.0", World, Systems, Messages);
                new ModEntry().OnLoad(ctx);
            }
        }

        private static Session GetSession(World world)
        {
            foreach (var (_, s) in world.Store<Session>().All())
                return s;
            throw new Exception("未找到 Session");
        }

        private static Session TickUntilWon(Env env, int maxTicks)
        {
            for (int i = 0; i < maxTicks; i++)
            {
                env.Systems.UpdateAll(env.World, 1f / 30f);
                var s = GetSession(env.World);
                if (s.Phase == GamePhase.Won) return s;
            }
            return GetSession(env.World);
        }

        [Test]
        public static void A_Wins()
        {
            var env = new Env();
            env.Messages.SendTo(env.ModId, new PushInputMsg { Side = PlayerSide.Left, Pushing = true });
            var s = TickUntilWon(env, 600);
            Assert.True(s.Phase == GamePhase.Won, "A 单独推应分出胜负");
            Assert.Equal(PlayerSide.Left, s.Winner, "A 胜");
        }

        [Test]
        public static void B_Wins()
        {
            var env = new Env();
            env.Messages.SendTo(env.ModId, new PushInputMsg { Side = PlayerSide.Right, Pushing = true });
            var s = TickUntilWon(env, 600);
            Assert.True(s.Phase == GamePhase.Won, "B 单独推应分出胜负");
            Assert.Equal(PlayerSide.Right, s.Winner, "B 胜");
        }

        [Test]
        public static void Stalemate()
        {
            var env = new Env();
            env.Messages.SendTo(env.ModId, new PushInputMsg { Side = PlayerSide.Left, Pushing = true });
            env.Messages.SendTo(env.ModId, new PushInputMsg { Side = PlayerSide.Right, Pushing = true });
            for (int i = 0; i < 300; i++)
                env.Systems.UpdateAll(env.World, 1f / 30f);
            var s = GetSession(env.World);
            Assert.True(s.Phase == GamePhase.Running, "双方同推应不分胜负");
        }
    }
}
