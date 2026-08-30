using System;
using System.Collections.Generic;
using Com.Game.Network;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// 协议版本协商（§11.6）测试：交集计算 / required 判定 / 表现层降级禁用 / 废弃窗口（legacy codec）/
    /// 测试向量 / 真实 Mod 加载回归。
    /// 拓扑：BridgeFixture（Service 服务端 + N 个 Client 运行时，跨运行时握手，客户端可在 Start 前注册协议）
    /// + Loopback（Host 回环）+ 手工 RawTransport（注入未协商连接的业务帧）。
    /// </summary>
    public static class NegotiationTests
    {
        private static readonly ModId ModA = new("com.game.alpha");
        private static readonly ModId ModB = new("com.game.beta");

        // ---- 测试协议与处理器 ----

        private sealed class TestProtocol : INetworkProtocol
        {
            private readonly string _path;
            private readonly ModId _owner;
            public NetworkDirection Direction { get; }
            public ushort Version { get; init; }
            public int MaxSize => 64;

            public TestProtocol(ModId owner, string path, NetworkDirection direction, ushort version = 1)
            {
                _owner = owner;
                _path = path;
                Direction = direction;
                Version = version;
            }

            public ProtocolId Id => ProtocolId.Of(_owner, _path);
            public void Encode(in object message, INetworkWriter writer) => writer.WriteInt32(1, (int)message);
            public object Decode(INetworkReader reader)
            {
                reader.TryReadInt32(1, out var v);
                return v;
            }
        }

        /// <summary>legacy codec：线格式与当前版可区分（写 v*10，读 raw/10）——用于证明服务器真的用了旧 codec。</summary>
        private sealed class LegacyProtocol : INetworkProtocol
        {
            private readonly string _path;
            private readonly ModId _owner;
            public NetworkDirection Direction { get; }
            public ushort Version { get; }
            public int MaxSize => 64;

            public LegacyProtocol(ModId owner, string path, NetworkDirection direction, ushort version)
            {
                _owner = owner;
                _path = path;
                Direction = direction;
                Version = version;
            }

            public ProtocolId Id => ProtocolId.Of(_owner, _path);
            public void Encode(in object message, INetworkWriter writer) => writer.WriteInt32(1, (int)message * 10);
            public object Decode(INetworkReader reader)
            {
                reader.TryReadInt32(1, out var v);
                return v / 10;
            }
        }

        private sealed class CollectHandler : INetworkHandler
        {
            public int Count;
            public object? Last;
            public NetworkContext LastContext;
            public void Handle(in NetworkContext context, in object message)
            {
                Count++;
                Last = message;
                LastContext = context;
            }
        }

        /// <summary>手工传输：只启动服务器侧，连接从不发 hello——用于注入"未协商连接"的业务帧（§11.6 防御）。</summary>
        private sealed class RawTransport : INetworkTransport
        {
            private static readonly List<int> Conns = new() { 7 };
            public event Action<byte[]>? ReceivedOnClient;
            public event Action<int, byte[]>? ReceivedOnServer;
            public event Action? ClientReady;
            public IReadOnlyCollection<int> ServerConnections => Conns;
            public void StartClient() { }
            public void StartServer() { }
            public void Stop() { }
            public void SendFromClient(byte[] frame) => ReceivedOnServer?.Invoke(7, frame);
            public void SendFromServerTo(int connectionId, byte[] frame) => ReceivedOnClient?.Invoke(frame);
            public void SendFromServerAll(byte[] frame) => ReceivedOnClient?.Invoke(frame);
        }

        private static byte[] EncodeRawFrame(ProtocolId id, ushort version, int value)
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, value);
            var payload = w.ToBuffer();
            var frame = new byte[NetworkRuntimeImpl.FrameHeaderSize + payload.Length];
            var idVal = id.Value;
            frame[0] = (byte)idVal;
            frame[1] = (byte)(idVal >> 8);
            frame[2] = (byte)(idVal >> 16);
            frame[3] = (byte)(idVal >> 24);
            frame[4] = (byte)version;
            frame[5] = (byte)(version >> 8);
            frame[8] = (byte)payload.Length;
            Array.Copy(payload.Data, 0, frame, NetworkRuntimeImpl.FrameHeaderSize, payload.Length);
            return frame;
        }

        // ---- §9.1-1 同版本全接受，业务帧正常往返 ----

        [Test]
        public static void Handshake_AcceptsAll_WhenVersionsMatch()
        {
            using var f = BridgeFixture.CreateServer();
            var input = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var state = new TestProtocol(ModA, "state", NetworkDirection.ServerToClient);
            var serverHandler = new CollectHandler();
            f.Server.RegisterProtocol(ModA, input, serverHandler);
            f.Server.RegisterProtocol(ModA, state, null); // 服务器也注册 s2c 协议（SendToClient 校验需要）

            var client = f.AddClient();
            var clientHandler = new CollectHandler();
            client.RegisterProtocol(ModA, input, null);
            client.RegisterProtocol(ModA, state, clientHandler);
            f.StartClients();

            // 客户端 → 服务器（hello 后 SendToServer 放行）
            client.SendToServer(ModA, input.Id, 123);
            Assert.Equal(1, serverHandler.Count);
            Assert.Equal(123, (int)serverHandler.Last!);
            Assert.True(serverHandler.LastContext.IsServer);

            // 服务器 → 客户端单播（connId 由服务器 Handler 上下文捕获）
            var connId = serverHandler.LastContext.ConnectionId;
            f.Server.SendToClient(ModA, connId, state.Id, 7);
            Assert.Equal(1, clientHandler.Count);
            Assert.Equal(7, (int)clientHandler.Last!);
            Assert.True(!clientHandler.LastContext.IsServer);

            // 双侧协商状态：accepted 版本 = 注册版本 1
            Assert.True(f.Server.Negotiation.IsNegotiated(connId));
            Assert.True(client.Negotiation.IsNegotiated(NegotiationLayer.ClientConnId));
            Assert.Equal((ushort)1, f.Server.Negotiation.SendVersion(connId, input.Id));
        }

        // ---- §9.1-2 核心玩法协议无交集 → 整体拒连（reason=1） ----

        [Test]
        public static void Handshake_Rejects_CoreProtocolNoIntersection()
        {
            using var f = BridgeFixture.CreateServer();
            f.Server.Negotiation.RequiredMods = new[] { ModA.Value }; // 核心玩法协议所在 Mod

            var inputV3 = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer, version: 3);
            f.Server.RegisterProtocol(ModA, inputV3, null);

            var client = f.AddClient();
            client.RegisterProtocol(ModA,
                new TestProtocol(ModA, "input", NetworkDirection.ClientToServer, version: 1), null);
            f.StartClients();

            // 服务器侧：连接被拒，reason=1（core_no_intersection）
            Assert.True(f.Server.Negotiation.IsRejected(1, out var reason, out var missing), "连接应被整体拒绝");
            Assert.Equal(1u, reason);
            Assert.Equal("", missing);

            // 客户端侧：后续发送抛 NetworkException（带拒绝原因）
            Assert.Throws<NetworkException>(() => client.SendToServer(ModA, inputV3.Id, 1));
            Assert.True(client.Negotiation.IsRejected(NegotiationLayer.ClientConnId, out var cReason, out _));
            Assert.Equal(1u, cReason);
        }

        // ---- §9.1-3 表现层协议无交集 → 降级禁用，连接继续 ----

        [Test]
        public static void Handshake_DisablesOptionalProtocol()
        {
            using var f = BridgeFixture.CreateServer();
            // 服务器：可选（非 required）c2s 协议 v2 + s2c 协议 v2
            var optC2S = new TestProtocol(ModA, "opt", NetworkDirection.ClientToServer, version: 2);
            var optS2C = new TestProtocol(ModA, "skin", NetworkDirection.ServerToClient, version: 2);
            f.Server.RegisterProtocol(ModA, optC2S, null);
            f.Server.RegisterProtocol(ModA, optS2C, null);

            var client = f.AddClient();
            var clientHandler = new CollectHandler();
            client.RegisterProtocol(ModA,
                new TestProtocol(ModA, "opt", NetworkDirection.ClientToServer, version: 1), null);
            client.RegisterProtocol(ModA,
                new TestProtocol(ModA, "skin", NetworkDirection.ServerToClient, version: 1), clientHandler);
            f.StartClients();
            var baseFrames = f.ServerTransport.SentFrames.Count; // 握手 result 帧基线

            // 连接被接受（表现层降级不拒连），但两个协议进入 disabled
            Assert.True(f.Server.Negotiation.IsNegotiated(1));
            Assert.True(f.Server.Negotiation.IsDisabled(1, optC2S.Id));
            Assert.True(f.Server.Negotiation.IsDisabled(1, optS2C.Id));
            Assert.True(client.Negotiation.IsDisabled(NegotiationLayer.ClientConnId, optC2S.Id));

            // 客户端发送被禁协议 → 抛 NetworkException
            Assert.Throws<NetworkException>(() => client.SendToServer(ModA, optC2S.Id, 1));

            // 服务器广播被禁协议 → 静默跳过（不发帧，客户端 Handler 不触发）
            f.Server.Broadcast(ModA, optS2C.Id, 5);
            Assert.Equal(0, clientHandler.Count);
            Assert.Equal(baseFrames, f.ServerTransport.SentFrames.Count);

            // 直接注入被禁协议帧到服务器 → DroppedNotNegotiated++，Handler 不触发（§11.6 防御）
            f.ClientTransports[0].SendFromClient(EncodeRawFrame(optC2S.Id, 1, 1));
            Assert.Equal(1L, f.Server.GetStats().DroppedNotNegotiated);
            Assert.Equal(0, f.Server.GetStats().MessagesReceived);
        }

        // ---- §9.1-4 客户端缺少必需 Mod → 整体拒连（reason=2 + 缺失清单） ----

        [Test]
        public static void Handshake_MissingRequiredMod_Rejects()
        {
            using var f = BridgeFixture.CreateServer();
            f.Server.Negotiation.RequiredMods = new[] { ModA.Value, ModB.Value };
            f.Server.RegisterProtocol(ModA,
                new TestProtocol(ModA, "input", NetworkDirection.ClientToServer), null);

            var client = f.AddClient();
            client.RegisterProtocol(ModA,
                new TestProtocol(ModA, "input", NetworkDirection.ClientToServer), null);
            f.StartClients();

            Assert.True(f.Server.Negotiation.IsRejected(1, out var reason, out var missing));
            Assert.Equal(2u, reason);
            Assert.True(missing.Contains(ModB.Value), $"missing 应含 {ModB.Value}，实际 '{missing}'");
            Assert.Throws<NetworkException>(() => client.SendToServer(ModA, ProtocolId.Of(ModA, "input"), 1));
        }

        // ---- §9.1-5 废弃窗口：服务器保留"当前 + 上一版"，协商到 legacy 版本时用 legacy codec ----

        [Test]
        public static void Negotiation_UsesLegacyCodec()
        {
            using var f = BridgeFixture.CreateServer();
            // 服务器：当前版 v3 + legacy v2 codec（线格式不同：legacy 写 v*10）
            var moveV3 = new TestProtocol(ModA, "move", NetworkDirection.ClientToServer, version: 3);
            var moveLegacy = new LegacyProtocol(ModA, "move", NetworkDirection.ClientToServer, version: 2);
            var serverHandler = new CollectHandler();
            f.Server.RegisterProtocol(ModA, moveV3, serverHandler);
            f.Server.RegisterLegacyCodec(ModA, moveV3.Id, 2, moveLegacy);

            // 服务器 s2c 同构：state v3 + legacy v2
            var stateV3 = new TestProtocol(ModA, "state", NetworkDirection.ServerToClient, version: 3);
            var stateLegacy = new LegacyProtocol(ModA, "state", NetworkDirection.ServerToClient, version: 2);
            f.Server.RegisterProtocol(ModA, stateV3, null);
            f.Server.RegisterLegacyCodec(ModA, stateV3.Id, 2, stateLegacy);

            // 客户端：老版本客户端，声明 v2（同一逻辑协议 ID）
            var client = f.AddClient();
            var clientHandler = new CollectHandler();
            client.RegisterProtocol(ModA,
                new LegacyProtocol(ModA, "move", NetworkDirection.ClientToServer, version: 2), null);
            client.RegisterProtocol(ModA,
                new LegacyProtocol(ModA, "state", NetworkDirection.ServerToClient, version: 2), clientHandler);
            f.StartClients();

            // 协商取共同版本 = 客户端声明版本 v2（∈ {注册 3} ∪ {legacy 2}）
            Assert.True(f.Server.Negotiation.IsNegotiated(1));
            Assert.Equal((ushort)2, f.Server.Negotiation.SendVersion(1, moveV3.Id));

            // C2S：客户端（v2 codec）发 5 → 线格式 50 → 服务器用 legacy codec 解回 5
            // （若服务器误用 v3 codec 会解出 50；且帧版本必须是协商版本 2，否则被 DroppedInvalid）
            client.SendToServer(ModA, moveV3.Id, 5);
            Assert.Equal(1, serverHandler.Count);
            Assert.Equal(5, (int)serverHandler.Last!);
            Assert.Equal(0L, f.Server.GetStats().DroppedInvalid);
            Assert.Equal(1L, f.Server.GetStats().MessagesReceived);

            // S2C：服务器用 legacy codec 出帧（帧头版本 2 + legacy 线格式 50）→ 客户端解回 5
            f.Server.SendToClient(ModA, 1, stateV3.Id, 5);
            Assert.Equal(1, clientHandler.Count);
            Assert.Equal(5, (int)clientHandler.Last!);
            var frame = f.ServerTransport.SentFrames[^1];
            Assert.Equal((ushort)2, (ushort)(frame[4] | (frame[5] << 8))); // 帧头版本 = 协商版本
        }

        [Test]
        public static void RegisterLegacyCodec_WrongOwner_Rejected()
        {
            using var f = BridgeFixture.CreateServer();
            var proto = new TestProtocol(ModA, "move", NetworkDirection.ClientToServer, version: 3);
            f.Server.RegisterProtocol(ModA, proto, null);

            // 非 owner 注册 legacy → 拒绝（§11.6 仅协议 owner 可注册旧版本 codec）
            Assert.Throws<NetworkException>(() =>
                f.Server.RegisterLegacyCodec(ModB, proto.Id, 2,
                    new LegacyProtocol(ModA, "move", NetworkDirection.ClientToServer, version: 2)));
            // 未注册协议的 legacy → 拒绝
            Assert.Throws<NetworkException>(() =>
                f.Server.RegisterLegacyCodec(ModA, ProtocolId.Of(ModA, "nope"), 2,
                    new LegacyProtocol(ModA, "nope", NetworkDirection.ClientToServer, version: 2)));
        }

        // ---- §9.1-6 未协商连接上的业务帧 → drop（防御：握手完成前业务流量不可注入） ----

        [Test]
        public static void BusinessFrame_BeforeNegotiation_Dropped()
        {
            var runtime = new NetworkRuntimeImpl();
            var transport = new RawTransport();
            runtime.AttachTransport(transport);
            runtime.Start(new NetworkConfig { Role = NetworkRole.Service, Path = NetworkPath.Direct });
            var proto = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, proto, handler);

            // 发送路径：未协商 → 抛 NetworkException
            Assert.Throws<NetworkException>(() => runtime.SendToServer(ModA, proto.Id, 1));

            // 接收路径：注入业务帧 → DroppedNotNegotiated++，Handler 不触发
            transport.SendFromClient(EncodeRawFrame(proto.Id, 1, 1));
            Assert.Equal(1L, runtime.GetStats().DroppedNotNegotiated);
            Assert.Equal(0, handler.Count);
        }

        // ---- §9.1-7 握手协议测试向量：线格式回归锁定（§14.11.3 可重复定义） ----

        [Test]
        public static void Handshake_TestVectors_Decode()
        {
            var helloVec = HandshakeVectors.Hello();
            var entries = HandshakeVectors.ParseHelloEntries(helloVec.Bytes);
            Assert.Equal(2, entries.Length);
            Assert.Equal(ProtocolId.Of("com.game.alpha:move"), entries[0].Id);
            Assert.Equal((ushort)3, entries[0].Version);
            Assert.Equal("com.game.alpha", entries[0].ModId);
            Assert.Equal(ProtocolId.Of("com.game.beta:state"), entries[1].Id);
            Assert.Equal((ushort)1, entries[1].Version);

            var resultVec = HandshakeVectors.Result();
            var result = HandshakeVectors.ParseResult(resultVec.Bytes);
            Assert.True(result.Accept);
            Assert.Equal(0u, result.Reason);
            Assert.Equal(2, result.Accepted.Length);
            Assert.Equal((ushort)3, result.Accepted[0].Version);
            Assert.Equal(1, result.Disabled.Length);
            Assert.Equal(ProtocolId.Of("com.game.opt:skin"), result.Disabled[0]);
        }

        [Test]
        public static void Handshake_Protocols_RoundTrip_ThroughWire()
        {
            // 协议 Encode → Decode 往返（线格式锁定，含 accept=0 分支）
            var helloProto = new HandshakeHelloProtocol();
            var hello = new HandshakeHello
            {
                Entries = new[]
                {
                    new ProtocolVersionEntry
                    {
                        Id = ProtocolId.Of("com.game.alpha:move"),
                        Version = 3,
                        ModId = "com.game.alpha",
                        Path = "move",
                    },
                },
            };
            var w = new PayloadWriter();
            helloProto.Encode(hello, w);
            var decoded = (HandshakeHello)helloProto.Decode(new PayloadReader(w.ToBuffer().ToArray()));
            Assert.Equal(1, decoded.Entries.Length);
            Assert.Equal(hello.Entries[0].Id, decoded.Entries[0].Id);
            Assert.Equal((ushort)3, decoded.Entries[0].Version);
            Assert.Equal("com.game.alpha", decoded.Entries[0].ModId);

            var resultProto = new HandshakeResultProtocol();
            var result = new HandshakeResult { Accept = false, Reason = 2, MissingMods = "com.game.beta" };
            var w2 = new PayloadWriter();
            resultProto.Encode(result, w2);
            var r2 = (HandshakeResult)resultProto.Decode(new PayloadReader(w2.ToBuffer().ToArray()));
            Assert.True(!r2.Accept);
            Assert.Equal(2u, r2.Reason);
            Assert.Equal("com.game.beta", r2.MissingMods);
        }

        // ---- §9.1-8 真实 Mod 加载（RegisterManifest + Load）+ 后注册协议默认接受 ----

        [Test]
        public static void PostStartRegisteredProtocol_Accepted()
        {
            // 真实 com.game.network 程序集经 ModManager 加载（RegisterManifest + Load），Loopback Host 回环：
            // 握手在 Start 内完成（hello 只含复制协议），之后注册的业务协议默认接受（§2.4）——
            // 既有 fixture（先 Start 再注册协议）行为的回归保证。
            var host = new ModRuntimeHost(RuntimeRole.Host);
            var manifest = ModManifest.Parse(RepoPaths.ModJson("com.game.network"));
            host.Manager.RegisterManifest(manifest, new[] { typeof(NetworkMod).Assembly });
            host.Manager.Load(manifest.ModId);

            var runtime = NetworkMod.Current!;
            var transport = new LoopbackTransport();
            runtime.AttachTransport(transport);
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });

            // 后注册 c2s 协议：无需重握手即可收发
            var input = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, input, handler);
            runtime.SendToServer(ModA, input.Id, 42);
            Assert.Equal(1, handler.Count);
            Assert.Equal(42, (int)handler.Last!);

            // 后注册 s2c 协议：Broadcast 正常
            var state = new TestProtocol(ModA, "state", NetworkDirection.ServerToClient);
            var clientHandler = new CollectHandler();
            runtime.RegisterProtocol(ModA, state, clientHandler);
            runtime.Broadcast(ModA, state.Id, 9);
            Assert.Equal(1, clientHandler.Count);
            Assert.Equal(9, (int)clientHandler.Last!);

            // 握手帧不计业务统计（§11.13 业务口径）
            Assert.Equal(2L, runtime.GetStats().MessagesSent);
            Assert.Equal(2L, runtime.GetStats().MessagesReceived);
            runtime.Stop();
        }
    }
}
