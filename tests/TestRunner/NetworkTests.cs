using Com.Game.Network;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>Network.Mod 测试：稳定协议 ID / 注册冲突 / 方向强制 / 回环管线 / 版本窗口 / 配额。</summary>
    public static class NetworkTests
    {
        private static readonly ModId ModA = new("com.game.alpha");
        private static readonly ModId ModB = new("com.game.beta");

        private sealed class TestProtocol : INetworkProtocol
        {
            private readonly string _path;
            private readonly ModId _owner;
            public NetworkDirection Direction { get; }
            public ushort Version { get; init; }
            public int MaxSize => 64;

            public TestProtocol(ModId owner, string path, NetworkDirection direction)
            {
                _owner = owner;
                _path = path;
                Direction = direction;
            }

            public ProtocolId Id => ProtocolId.Of(_owner, _path);
            ushort INetworkProtocol.Version => Version;

            public void Encode(in object message, INetworkWriter writer)
                => writer.WriteInt32(1, (int)message);

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
            public NetworkContext LastContext;
            public void Handle(in NetworkContext context, in object message)
            {
                Count++;
                Last = message;
                LastContext = context;
            }
        }

        private static (NetworkRuntimeImpl runtime, LoopbackTransport transport) StartedHost()
        {
            var runtime = new NetworkRuntimeImpl();
            var transport = new LoopbackTransport();
            runtime.AttachTransport(transport);
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });
            return (runtime, transport);
        }

        [Test]
        public static void ProtocolId_Stable_And_OrderIndependent()
        {
            // Rule 16：稳定哈希，两端各自计算天然一致，不依赖加载顺序
            var p1 = new TestProtocol(ModA, "fire", NetworkDirection.ClientToServer);
            var p2 = new TestProtocol(ModA, "fire", NetworkDirection.ClientToServer);
            Assert.Equal(p1.Id, p2.Id);
            Assert.Equal(ProtocolId.Of("com.game.alpha:fire"), p1.Id);
            Assert.True(p1.Id != new TestProtocol(ModA, "reload", NetworkDirection.ClientToServer).Id);
        }

        [Test]
        public static void Register_Conflict_Rejected()
        {
            var (runtime, _) = StartedHost();
            var proto = new TestProtocol(ModA, "fire", NetworkDirection.ClientToServer);
            runtime.RegisterProtocol(ModA, proto, null);
            // 同一协议 ID 被另一个 Mod 注册 → 拒绝并指出冲突双方（§11.5）
            Assert.Throws<NetworkException>(() =>
                runtime.RegisterProtocol(ModB, proto, null));
            // 同 owner 重复注册幂等
            runtime.RegisterProtocol(ModA, proto, null);
        }

        [Test]
        public static void Loopback_ClientToServer_RoundTrip()
        {
            var (runtime, _) = StartedHost();
            var proto = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, proto, handler);

            runtime.SendToServer(ModA, proto.Id, 123);

            Assert.Equal(1, handler.Count);
            Assert.Equal(123, (int)handler.Last!);
            Assert.True(handler.LastContext.IsServer);
            Assert.Equal(LoopbackTransport.LocalConnectionId, handler.LastContext.ConnectionId);
            Assert.Equal(1L, runtime.GetStats().MessagesSent);
            Assert.Equal(1L, runtime.GetStats().MessagesReceived);
            Assert.True(runtime.GetStats().BytesByMod[ModA] > 0); // §11.13 per-Mod 统计
        }

        [Test]
        public static void Loopback_ServerBroadcast_ToClient()
        {
            var (runtime, _) = StartedHost();
            var proto = new TestProtocol(ModA, "state", NetworkDirection.ServerToClient);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, proto, handler);

            runtime.Broadcast(ModA, proto.Id, 7);
            Assert.Equal(1, handler.Count);
            Assert.Equal(7, (int)handler.Last!);
            Assert.True(!handler.LastContext.IsServer);
        }

        [Test]
        public static void Direction_Enforced_ByApi()
        {
            var (runtime, _) = StartedHost();
            var c2s = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var s2c = new TestProtocol(ModA, "state", NetworkDirection.ServerToClient);
            runtime.RegisterProtocol(ModA, c2s, null);
            runtime.RegisterProtocol(ModA, s2c, null);

            // §11.7：方向由 API 形态强制
            Assert.Throws<NetworkException>(() => runtime.Broadcast(ModA, c2s.Id, 1));
            Assert.Throws<NetworkException>(() => runtime.SendToServer(ModA, s2c.Id, 1));
        }

        [Test]
        public static void Send_OtherModsProtocol_Rejected()
        {
            var (runtime, _) = StartedHost();
            var proto = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            runtime.RegisterProtocol(ModA, proto, null);
            // ModChannel 隔离：一个 Mod 的数据只能走自己的协议（§11.7）
            Assert.Throws<NetworkException>(() => runtime.SendToServer(ModB, proto.Id, 1));
        }

        [Test]
        public static void VersionWindow_OldCodecDropped()
        {
            // 协议废弃窗口：只保留当前版本（§11.6）——注册 v2，收到 v1 帧直接丢弃
            var (runtime, transport) = StartedHost();
            var protoV2 = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer) { Version = 2 };
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, protoV2, handler);

            // 手工构造 v1 帧
            var w = new PayloadWriter();
            w.WriteInt32(1, 5);
            var payload = w.ToBuffer();
            var frame = new byte[NetworkRuntimeImpl.FrameHeaderSize + payload.Length];
            var idBytes = protoV2.Id.Value;
            frame[0] = (byte)idBytes; frame[1] = (byte)(idBytes >> 8);
            frame[2] = (byte)(idBytes >> 16); frame[3] = (byte)(idBytes >> 24);
            frame[4] = 1; frame[5] = 0; // version = 1
            frame[8] = (byte)payload.Length;
            System.Array.Copy(payload.Data, 0, frame, NetworkRuntimeImpl.FrameHeaderSize, payload.Length);

            transport.SendFromClient(frame); // 模拟旧版本客户端
            Assert.Equal(0, handler.Count);
            Assert.Equal(1L, runtime.GetStats().DroppedInvalid);
        }

        [Test]
        public static void MaxSize_Enforced()
        {
            var (runtime, _) = StartedHost();
            var proto = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            runtime.RegisterProtocol(ModA, proto, null);
            runtime.SendToServer(ModA, proto.Id, 1); // 几字节载荷正常通过
            // 载荷超 MaxSize → 丢弃并记违规（§11.12 UGC 防护）
            var big = new BigProtocol(ModA);
            runtime.RegisterProtocol(ModA, big, null);
            Assert.Throws<NetworkException>(() => runtime.SendToServer(ModA, big.Id, new string('x', 1000)));
            Assert.True(runtime.GetStats().DroppedQuota > 0);
        }

        private sealed class BigProtocol : INetworkProtocol
        {
            private readonly ModId _owner;
            public BigProtocol(ModId owner) => _owner = owner;
            public ProtocolId Id => ProtocolId.Of(_owner, "big");
            public ushort Version => 1;
            public NetworkDirection Direction => NetworkDirection.ClientToServer;
            public int MaxSize => 64;
            public void Encode(in object message, INetworkWriter writer) => writer.WriteString(1, (string)message);
            public object Decode(INetworkReader reader) => "";
        }

        [Test]
        public static void UnregisterAll_ByOwner()
        {
            var (runtime, _) = StartedHost();
            runtime.RegisterProtocol(ModA, new TestProtocol(ModA, "a", NetworkDirection.ClientToServer), null);
            runtime.RegisterProtocol(ModA, new TestProtocol(ModA, "b", NetworkDirection.ServerToClient), null);
            runtime.RegisterProtocol(ModB, new TestProtocol(ModB, "c", NetworkDirection.ClientToServer), null);

            runtime.UnregisterAll(ModA); // §11.6：协议生命周期与 Mod 严格一致
            Assert.True(!runtime.IsRegistered(ProtocolId.Of(ModA, "a")));
            Assert.True(!runtime.IsRegistered(ProtocolId.Of(ModA, "b")));
            Assert.True(runtime.IsRegistered(ProtocolId.Of(ModB, "c")));
        }

        [Test]
        public static void RolePath_Orthogonal()
        {
            // Rule 15：Role（运行角色）× Path（连接路径）两个维度正交
            var runtime = new NetworkRuntimeImpl();
            runtime.Start(new NetworkConfig { Role = NetworkRole.Service, Path = NetworkPath.Relay });
            Assert.Equal(NetworkRole.Service, runtime.Role);
            runtime.Stop();
        }
    }
}
