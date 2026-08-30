using System;
using Com.Game.Network;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// 全链路集成（实现设计 §9.6）：五层组合 @ Loopback——
    /// 版本协商（§11.6）+ 兴趣管理（§11.8）+ 加密（§11.12）+ 流量配额（§11.13）+ 三级统计。
    /// 与 RelayTests.Relay_FullPipeline_OverRelay（@ Relay）互为"两端各一遍"对照，
    /// 证明五层组合不破坏彼此。真实 com.game.network 程序集经 ModManager 加载，
    /// Host 回环（§11.3 硬规则）走完整 Encode → 协议 → Decode 管线。
    /// </summary>
    public static class LayeredPipelineTests
    {
        private static readonly ModId ModA = new("com.game.alpha");

        private static readonly byte[] Psk =
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        };

        private sealed class TestProtocol : INetworkProtocol
        {
            private readonly string _path;
            private readonly NetworkDirection _direction;
            public ushort Version => 1;
            public int MaxSize => 64;

            public TestProtocol(string path, NetworkDirection direction)
            {
                _path = path;
                _direction = direction;
            }

            public ProtocolId Id => ProtocolId.Of(ModA, _path);
            public NetworkDirection Direction => _direction;
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

        /// <summary>拒绝一切广播的兴趣管理器（§11.8 过滤语义在 Loopback 上的体现）。</summary>
        private sealed class NoInterest : IInterestManager
        {
            public bool IsRelevant(int connectionId, ProtocolId protocolId, in object message) => false;
        }

        /// <summary>
        /// 全链路 @ Loopback：协商（Start 内握手 + 后注册默认接受）→ 加密（psk，握手帧 + 业务帧全量密文）→
        /// 兴趣（默认全发 → 过滤 → 恢复全发）→ 流量（预算内往返 + 超限 Throttle）→ 三级统计与加密两档。
        /// </summary>
        [Test]
        public static void FullPipeline_Loopback_AllLayers_Coexist()
        {
            // 真实 Network.Mod 程序集经 ModManager 加载（与 PostStartRegisteredProtocol_Accepted 同型）
            var host = new ModRuntimeHost(RuntimeRole.Host);
            var manifest = ModManifest.Parse(RepoPaths.ModJson("com.game.network"));
            host.Manager.RegisterManifest(manifest, new[] { typeof(NetworkMod).Assembly });
            host.Manager.Load(manifest.ModId);

            var runtime = NetworkMod.Current!;
            var transport = new LoopbackTransport();
            runtime.AttachTransport(transport);
            runtime.Security.Enable(Psk); // 加密（§11.12）：在握手前启用，hello/result 也走密文
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });

            // ① 协商（§11.6）：Host 本地回环在 Start 内完成握手（服务器侧 connId=1 / 客户端侧 connId=0）
            Assert.True(runtime.Negotiation.IsNegotiated(LoopbackTransport.LocalConnectionId),
                "服务器侧连接应在 Start 内协商完成（§11.3 回环硬规则）");
            Assert.True(runtime.Negotiation.IsNegotiated(NegotiationLayer.ClientConnId),
                "客户端侧应在 Start 内协商完成");
            Assert.Equal(0L, runtime.GetStats().DroppedNotNegotiated);

            // 业务协议（C2S + S2C），Start 后注册 → 默认接受（§2.4）
            var input = new TestProtocol("input", NetworkDirection.ClientToServer);
            var state = new TestProtocol("state", NetworkDirection.ServerToClient);
            var serverHandler = new CollectHandler();
            var clientHandler = new CollectHandler();
            runtime.RegisterProtocol(ModA, input, serverHandler);
            runtime.RegisterProtocol(ModA, state, clientHandler);

            // ② 兴趣（§11.8）：默认全发 → 注册过滤 → 恢复全发
            runtime.Broadcast(ModA, state.Id, 7);
            Assert.Equal(1, clientHandler.Count, "默认无 manager = 全房间广播（现状语义回归）");

            runtime.RegisterInterest(ModA, state.Id, new NoInterest());
            runtime.Broadcast(ModA, state.Id, 8);
            Assert.Equal(1, clientHandler.Count, "被兴趣过滤的连接零消息");
            Assert.Equal(0L, runtime.GetStats().DroppedThrottle, "兴趣过滤不是流量限流");

            runtime.RegisterInterest(ModA, state.Id, null); // null = 清除回默认全发（§3.1）
            runtime.Broadcast(ModA, state.Id, 9);
            Assert.Equal(2, clientHandler.Count, "清除兴趣后恢复全发");

            // ③ 加密（§11.12）：密文下业务帧往返，零认证失败 / 零重放
            runtime.SendToServer(ModA, input.Id, 123);
            Assert.Equal(1, serverHandler.Count);
            Assert.Equal(123, (int)serverHandler.Last!);
            Assert.Equal(0L, runtime.GetStats().DroppedAuth, "加密往返不应有认证失败");
            Assert.Equal(0L, runtime.GetStats().DroppedReplay, "加密往返不应有重放丢弃");

            // ④ 流量（§11.13）：当前窗口内已 3 条业务（广播 7、9 + 123；被兴趣过滤的 8 不占窗口）。
            // 预算 MaxPacketRate=5：再发 2 条填满（5/5），第 6 条被 Throttle。
            runtime.ConfigureBudget(ModA, new NetworkBudget { MaxPacketRate = 5 });
            runtime.SendToServer(ModA, input.Id, 1); // 窗口第 4 条 ≤ 5 → 放行
            runtime.SendToServer(ModA, input.Id, 2); // 窗口第 5 条 ≤ 5 → 放行
            Assert.Equal(3, serverHandler.Count, "预算内发送应全部送达");
            Assert.Equal(0L, runtime.GetStats().DroppedThrottle);

            runtime.SendToServer(ModA, input.Id, 3); // 窗口第 6 条 > 5 → 限流 drop
            Assert.Equal(3, serverHandler.Count, "超限消息应被 Throttle 丢弃");
            Assert.Equal(1L, runtime.GetStats().DroppedThrottle);

            // ⑤ 三级统计（§11.13）+ 加密前后两档
            var stats = runtime.GetStats();
            Assert.True(stats.BytesByConnection.ContainsKey(LoopbackTransport.LocalConnectionId)
                        && stats.BytesByConnection[LoopbackTransport.LocalConnectionId] > 0, "Connection 级统计");
            Assert.True(stats.BytesByMod.ContainsKey(ModA) && stats.BytesByMod[ModA] > 0, "Mod 级统计");
            Assert.True(stats.BytesByProtocol.ContainsKey(input.Id) && stats.BytesByProtocol[input.Id] > 0,
                "Protocol 级统计");
            Assert.True(runtime.Traffic.ModTransportBytes(ModA) > runtime.Traffic.ModLogicalBytes(ModA),
                "加密后传输字节应大于逻辑字节（§11.13 两档）");

            // 业务口径：握手帧不计业务统计（§11.13）。发送统计含被限流 1 条（§5.4 统计无条件记录），
            // 接收统计只含送达（广播 7/9 + C2S 123/1/2 = 5；被 Throttle 的第 6 条不送达）。
            Assert.Equal(6L, stats.MessagesSent);
            Assert.Equal(5L, stats.MessagesReceived);

            runtime.Stop();
        }

        /// <summary>
        /// 全链路 @ Loopback + required 判定（§11.6 策略表）：服务器声明必需 Mod，
        /// 客户端 hello 缺该 Mod → 整体拒绝（reason=2 + 缺失清单）；被拒连接发送抛 NetworkException。
        /// </summary>
        [Test]
        public static void FullPipeline_Loopback_RequiredRejects()
        {
            var host = new ModRuntimeHost(RuntimeRole.Host);
            var manifest = ModManifest.Parse(RepoPaths.ModJson("com.game.network"));
            host.Manager.RegisterManifest(manifest, new[] { typeof(NetworkMod).Assembly });
            host.Manager.Load(manifest.ModId);

            var runtime = NetworkMod.Current!;
            runtime.Negotiation.RequiredMods = new[] { "com.game.missing" }; // 客户端 hello 不含该 Mod
            var transport = new LoopbackTransport();
            runtime.AttachTransport(transport);
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });

            // 客户端 hello 缺 required Mod → 整体拒绝（reason=2 + 缺失清单）
            Assert.True(runtime.Negotiation.IsRejected(NegotiationLayer.ClientConnId, out var reason, out var missing),
                "缺 required Mod 应整体拒连");
            Assert.Equal(2u, reason);
            Assert.True(missing.Contains("com.game.missing"), $"missing 清单应含 com.game.missing，实际 '{missing}'");

            // 被拒连接上发送业务协议 → 抛 NetworkException（§11.6 防御）
            var input = new TestProtocol("input", NetworkDirection.ClientToServer);
            runtime.RegisterProtocol(ModA, input, null);
            Assert.Throws<NetworkException>(() => runtime.SendToServer(ModA, input.Id, 1));

            runtime.Stop();
        }
    }
}
