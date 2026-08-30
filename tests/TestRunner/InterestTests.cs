using System;
using System.Collections.Generic;
using Com.Game.Network;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// 兴趣管理（§11.8）测试：Broadcast 默认全发 / relevance 逐连接过滤 / 过滤在 Encode 前 /
    /// owner 与未注册协议校验 / 清除与 UnregisterAll 清理（Rule 20）/ 显式单播不过滤。
    /// 拓扑：BridgeFixture 一服务端 + 双客户端（多连接过滤），服务端 connId 按 AddClient 顺序 1/2。
    /// </summary>
    public static class InterestTests
    {
        private static readonly ModId ModA = new("com.game.alpha");
        private static readonly ModId ModB = new("com.game.beta");

        private sealed class StateProtocol : INetworkProtocol
        {
            public ProtocolId Id => ProtocolId.Of(ModA, "state");
            public ushort Version => 1;
            public NetworkDirection Direction => NetworkDirection.ServerToClient;
            public int MaxSize => 64;
            public void Encode(in object message, INetworkWriter writer) => writer.WriteInt32(1, (int)message);
            public object Decode(INetworkReader reader)
            {
                reader.TryReadInt32(1, out var v);
                return v;
            }
        }

        private sealed class CollectHandler : INetworkHandler
        {
            public int Count;
            public object? Last;
            public void Handle(in NetworkContext context, in object message)
            {
                Count++;
                Last = message;
            }
        }

        /// <summary>按 connId 白名单过滤的 manager；Evaluations 记录评估次数（断言"过滤在 Encode 前"）。</summary>
        private sealed class ConnFilter : IInterestManager
        {
            private readonly HashSet<int> _allowed;
            public int Evaluations;

            public ConnFilter(params int[] allowed) => _allowed = new HashSet<int>(allowed);

            public bool IsRelevant(int connectionId, ProtocolId protocolId, in object message)
            {
                Evaluations++;
                return _allowed.Contains(connectionId);
            }
        }

        /// <summary>一服务端 + 双客户端 fixture（connId 1/2），两端已握手且各注册 state 协议。</summary>
        private sealed class TwoClients : IDisposable
        {
            public BridgeFixture Bridge = null!;
            public CollectHandler H1 = null!, H2 = null!;

            public static TwoClients Create()
            {
                var t = new TwoClients { Bridge = BridgeFixture.CreateServer() };
                var state = new StateProtocol();
                t.Bridge.Server.RegisterProtocol(ModA, state, null);

                var c1 = t.Bridge.AddClient();
                var c2 = t.Bridge.AddClient();
                t.H1 = new CollectHandler();
                t.H2 = new CollectHandler();
                c1.RegisterProtocol(ModA, state, t.H1);
                c2.RegisterProtocol(ModA, state, t.H2);
                t.Bridge.StartClients();
                return t;
            }

            public void Dispose() => Bridge.Dispose();
        }

        // ---- §9.2-1 默认无 interest = 全房间广播（现状语义回归） ----

        [Test]
        public static void Broadcast_NoInterest_DeliversAll()
        {
            using var t = TwoClients.Create();
            var pid = new StateProtocol().Id;
            var baseFrames = t.Bridge.ServerTransport.SentFrames.Count; // 握手 result 帧基线

            t.Bridge.Server.Broadcast(ModA, pid, 7);

            Assert.Equal(1, t.H1.Count);
            Assert.Equal(1, t.H2.Count);
            Assert.Equal(7, (int)t.H1.Last!);
            Assert.Equal(baseFrames + 2, t.Bridge.ServerTransport.SentFrames.Count); // 逐连接一帧
        }

        // ---- §9.2-2 manager 按 connId 过滤：被过滤连接零消息 ----

        [Test]
        public static void Broadcast_InterestFiltersByConnection()
        {
            using var t = TwoClients.Create();
            var pid = new StateProtocol().Id;
            var baseFrames = t.Bridge.ServerTransport.SentFrames.Count; // 握手 result 帧基线
            t.Bridge.Server.RegisterInterest(ModA, pid, new ConnFilter(2)); // 只放行 connId=2（第二个客户端）

            t.Bridge.Server.Broadcast(ModA, pid, 7);

            Assert.Equal(0, t.H1.Count); // 被过滤客户端零消息
            Assert.Equal(1, t.H2.Count);
            Assert.Equal(baseFrames + 1, t.Bridge.ServerTransport.SentFrames.Count);
        }

        // ---- §9.2-3 过滤在 Encode 之前：每个候选连接评估一次，被过滤连接不产生帧 ----

        [Test]
        public static void Interest_EvaluatedBeforeEncode()
        {
            using var t = TwoClients.Create();
            var pid = new StateProtocol().Id;
            var filter = new ConnFilter(1); // 只放行 connId=1
            t.Bridge.Server.RegisterInterest(ModA, pid, filter);
            var baseFrames = t.Bridge.ServerTransport.SentFrames.Count; // 握手 result 帧基线

            t.Bridge.Server.Broadcast(ModA, pid, 3);

            Assert.Equal(2, filter.Evaluations); // 每个候选连接各评估一次（在 Encode 前过滤）
            Assert.Equal(1, t.H1.Count);
            Assert.Equal(0, t.H2.Count);
            Assert.Equal(baseFrames + 1, t.Bridge.ServerTransport.SentFrames.Count);
        }

        // ---- §9.2-4 owner 校验 / 未注册协议拒绝 ----

        [Test]
        public static void RegisterInterest_WrongOwner_Rejected()
        {
            using var t = TwoClients.Create();
            Assert.Throws<NetworkException>(() =>
                t.Bridge.Server.RegisterInterest(ModB, new StateProtocol().Id, new ConnFilter(1)));
        }

        [Test]
        public static void RegisterInterest_UnknownProtocol_Rejected()
        {
            using var f = BridgeFixture.CreateServer();
            Assert.Throws<NetworkException>(() =>
                f.Server.RegisterInterest(ModA, ProtocolId.Of("com.game.ghost:state"), new ConnFilter(1)));
        }

        // ---- §9.2-5 null = 清除回默认全发（§3.1） ----

        [Test]
        public static void RegisterInterest_Null_ClearsToDefault()
        {
            using var t = TwoClients.Create();
            var pid = new StateProtocol().Id;

            t.Bridge.Server.RegisterInterest(ModA, pid, new ConnFilter(2));
            t.Bridge.Server.Broadcast(ModA, pid, 1);
            Assert.Equal(0, t.H1.Count);
            Assert.Equal(1, t.H2.Count);

            t.Bridge.Server.RegisterInterest(ModA, pid, null); // 清除回默认全发
            t.Bridge.Server.Broadcast(ModA, pid, 2);
            Assert.Equal(1, t.H1.Count);
            Assert.Equal(2, t.H2.Count);
        }

        // ---- §9.2-5 UnregisterAll 按 owner 清理兴趣钩子（Rule 20） ----

        [Test]
        public static void UnregisterAll_ClearsInterest()
        {
            using var t = TwoClients.Create();
            var pid = new StateProtocol().Id;
            t.Bridge.Server.RegisterInterest(ModA, pid, new ConnFilter(2));
            t.Bridge.Server.Broadcast(ModA, pid, 1);
            Assert.Equal(0, t.H1.Count);
            Assert.Equal(1, t.H2.Count);

            t.Bridge.Server.UnregisterAll(ModA); // 协议 + 兴趣钩子一并清理

            // 重新注册同协议后广播 → 无残留兴趣，全发（若兴趣未清，connId=1 仍会被过滤）
            t.Bridge.Server.RegisterProtocol(ModA, new StateProtocol(), null);
            t.Bridge.Server.Broadcast(ModA, pid, 3);
            Assert.Equal(1, t.H1.Count);
            Assert.Equal(2, t.H2.Count);
        }

        // ---- §9.2-6 显式单播不过兴趣（调用方已选定目标，§3.3） ----

        [Test]
        public static void SendToClient_IgnoresInterest()
        {
            using var t = TwoClients.Create();
            var pid = new StateProtocol().Id;
            t.Bridge.Server.RegisterInterest(ModA, pid, new ConnFilter(2)); // 过滤掉 connId=1

            t.Bridge.Server.SendToClient(ModA, 1, pid, 9);

            Assert.Equal(1, t.H1.Count); // 单播不受兴趣影响
            Assert.Equal(9, (int)t.H1.Last!);
            Assert.Equal(0, t.H2.Count);
        }
    }
}
