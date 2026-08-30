using System;
using System.Collections.Generic;
using Com.Game.Network;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// 流量统计/配额/限流（§11.13）测试：Mod 级发送速率与带宽预算 / 挂起窗口 Throttle /
    /// 服务器连接级收包预算 / Connection-Mod-Protocol 三级统计 / 恢复默认 / UnregisterAll 清理（Rule 20）。
    /// 拓扑：Loopback Host（发送侧执行）+ RawTransport Service（注入帧，连接级收包防御）。
    /// </summary>
    public static class TrafficTests
    {
        private static readonly ModId ModA = new("com.game.alpha");

        private sealed class TestProtocol : INetworkProtocol
        {
            private readonly string _path;
            public NetworkDirection Direction { get; }
            public ushort Version => 1;
            public int MaxSize => 4096;

            public TestProtocol(string path, NetworkDirection direction)
            {
                _path = path;
                Direction = direction;
            }

            public ProtocolId Id => ProtocolId.Of(ModA, _path);
            public void Encode(in object message, INetworkWriter writer) => writer.WriteInt32(1, (int)message);
            public object Decode(INetworkReader reader)
            {
                reader.TryReadInt32(1, out var v);
                return v;
            }
        }

        /// <summary>固定载荷协议（带宽/收包预算测试；payload 60B → 帧 ~79B）。</summary>
        private sealed class BlobProtocol : INetworkProtocol
        {
            public NetworkDirection Direction { get; }
            public ushort Version => 1;
            public int MaxSize => 4096;

            public BlobProtocol(NetworkDirection direction) => Direction = direction;

            public ProtocolId Id => ProtocolId.Of(ModA, "blob");
            public void Encode(in object message, INetworkWriter writer) => writer.WriteBytes(1, (byte[])message);
            public object Decode(INetworkReader reader)
            {
                reader.TryReadBytes(1, out _);
                return Array.Empty<byte>();
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

        /// <summary>伪时钟 Host：TimeMs 在 Start 前注入（窗口从 0 起算，确定性）。</summary>
        private sealed class ClockedHost : IDisposable
        {
            public NetworkRuntimeImpl Runtime;
            public long Now;

            public ClockedHost()
            {
                Runtime = new NetworkRuntimeImpl();
                Runtime.Traffic.TimeMs = () => Now;
                var transport = new LoopbackTransport();
                Runtime.AttachTransport(transport);
                Runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });
            }

            public void Dispose() => Runtime.Stop();
        }

        /// <summary>手工传输：只启动服务器侧，连接从不发 hello——直接注入业务帧（连接级收包预算测试）。</summary>
        private sealed class RawTransport : INetworkTransport
        {
            private static readonly List<int> Conns = new() { 1 };
            public event Action<byte[]>? ReceivedOnClient;
            public event Action<int, byte[]>? ReceivedOnServer;
            public event Action? ClientReady;
            public IReadOnlyCollection<int> ServerConnections => Conns;
            public void StartClient() { }
            public void StartServer() { }
            public void Stop() { }
            public void SendFromClient(byte[] frame) => ReceivedOnServer?.Invoke(1, frame);
            public void SendFromServerTo(int connectionId, byte[] frame) => ReceivedOnClient?.Invoke(frame);
            public void SendFromServerAll(byte[] frame) => ReceivedOnClient?.Invoke(frame);
        }

        private static byte[] EncodeBlobFrame(ProtocolId id, ushort version, int blobSize)
        {
            var w = new PayloadWriter();
            w.WriteBytes(1, new byte[blobSize]);
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

        // ---- §9.4-1 Mod 发送速率预算 + 挂起窗口 Throttle（§5.4） ----

        [Test]
        public static void ModSendRate_Throttled()
        {
            using var host = new ClockedHost();
            var runtime = host.Runtime;
            runtime.ConfigureBudget(ModA, new NetworkBudget { MaxPacketRate = 2, ThrottleSuspendMs = 100 });
            var proto = new TestProtocol("input", NetworkDirection.ClientToServer);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, proto, handler);

            // 前 2 条在预算内
            runtime.SendToServer(ModA, proto.Id, 0);
            runtime.SendToServer(ModA, proto.Id, 1);
            Assert.Equal(2, handler.Count);
            Assert.Equal(0L, runtime.GetStats().DroppedThrottle);

            // 第 3 条起超 MaxPacketRate=2 → drop；第 5 条（窗口内第 3 次违规）触发挂起
            runtime.SendToServer(ModA, proto.Id, 2);
            runtime.SendToServer(ModA, proto.Id, 3);
            runtime.SendToServer(ModA, proto.Id, 4);
            Assert.Equal(2, handler.Count);
            Assert.Equal(3L, runtime.GetStats().DroppedThrottle);
            Assert.True(runtime.Traffic.IsSuspended(ModA), "窗口内违规 ≥3 应挂起 ThrottleSuspendMs");

            // 挂起窗口内：一切发送直接 drop 计数
            runtime.SendToServer(ModA, proto.Id, 5);
            Assert.Equal(4L, runtime.GetStats().DroppedThrottle);
            Assert.Equal(2, handler.Count);

            // 越过挂起窗口但仍在 1s 统计窗口内 → 仍超限（违规累计续挂）
            host.Now = 150;
            runtime.SendToServer(ModA, proto.Id, 6);
            Assert.Equal(5L, runtime.GetStats().DroppedThrottle);
            Assert.Equal(2, handler.Count);

            // 越过 1s 窗口 → 预算窗口重置，恢复
            host.Now = 1100;
            runtime.SendToServer(ModA, proto.Id, 7);
            Assert.Equal(3, handler.Count);
            Assert.Equal(5L, runtime.GetStats().DroppedThrottle); // 不再新增
            Assert.True(!runtime.Traffic.IsSuspended(ModA));
        }

        // ---- §9.4-2 Mod 带宽预算（逻辑字节窗口） ----

        [Test]
        public static void ModBandwidth_Throttled()
        {
            var runtime = new NetworkRuntimeImpl();
            var transport = new LoopbackTransport();
            runtime.AttachTransport(transport);
            runtime.ConfigureBudget(ModA, new NetworkBudget { MaxBandwidth = 100 });
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });

            var blob = new BlobProtocol(NetworkDirection.ServerToClient);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, blob, handler);

            runtime.Broadcast(ModA, blob.Id, new byte[60]); // 帧 ~79B ≤ 100 → 放行
            Assert.Equal(1, handler.Count);
            Assert.Equal(0L, runtime.GetStats().DroppedThrottle);

            runtime.Broadcast(ModA, blob.Id, new byte[60]); // 累计 158 > 100 → 限流 drop
            Assert.Equal(1, handler.Count);
            Assert.Equal(1L, runtime.GetStats().DroppedThrottle);
        }

        // ---- §9.4-3 服务器连接级收包预算（恶意/失控客户端防线，§5.4） ----

        [Test]
        public static void ConnectionReceiveBudget_Enforced()
        {
            var runtime = new NetworkRuntimeImpl();
            var transport = new RawTransport();
            runtime.AttachTransport(transport);
            runtime.ConfigureBudget(ModA, new NetworkBudget { MaxConnectionBandwidth = 100 });
            runtime.Start(new NetworkConfig { Role = NetworkRole.Service, Path = NetworkPath.Direct });

            var blob = new BlobProtocol(NetworkDirection.ClientToServer);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, blob, handler);

            // 1 帧（~79B）在连接预算内 → 过连接级 → 未协商 → DroppedNotNegotiated
            transport.SendFromClient(EncodeBlobFrame(blob.Id, 1, 60));
            Assert.Equal(1L, runtime.GetStats().DroppedNotNegotiated);
            Assert.Equal(0, handler.Count);

            // 第 2、3 帧累计 158/237 > 100 → 连接级收包预算 drop（在协商校验之前，§1.3 步骤⑤'）
            transport.SendFromClient(EncodeBlobFrame(blob.Id, 1, 60));
            transport.SendFromClient(EncodeBlobFrame(blob.Id, 1, 60));
            Assert.Equal(2L, runtime.GetStats().DroppedThrottle);
            Assert.Equal(1L, runtime.GetStats().DroppedNotNegotiated); // 无新增
            Assert.Equal(0, handler.Count);
        }

        // ---- §9.4-4 Connection / Mod / Protocol 三级统计（§11.13） ----

        [Test]
        public static void ThreeLevelStats_Recorded()
        {
            var runtime = new NetworkRuntimeImpl();
            var transport = new LoopbackTransport();
            runtime.AttachTransport(transport);
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });

            var proto = new TestProtocol("input", NetworkDirection.ClientToServer);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, proto, handler);
            for (var i = 0; i < 3; i++) runtime.SendToServer(ModA, proto.Id, i);

            var stats = runtime.GetStats();
            // Connection 级（§11.13 三级之一）
            Assert.True(stats.BytesByConnection.ContainsKey(1), "服务器侧应记录来源连接字节");
            Assert.True(stats.BytesByConnection[1] > 0);
            Assert.True(runtime.Traffic.ConnRecvBytes(1) > 0);
            Assert.True(runtime.Traffic.ConnSentBytes(0) > 0); // 客户端侧发送连接
            // Mod 级
            Assert.True(stats.BytesByMod[ModA] > 0);
            Assert.Equal(3, runtime.Traffic.ModSendMessages(ModA));
            Assert.True(runtime.Traffic.ModLogicalBytes(ModA) > 0);
            // Protocol 级
            Assert.Equal(3L, stats.MessagesByProtocol[proto.Id]);
            Assert.True(stats.BytesByProtocol[proto.Id] > 0);
            Assert.True(runtime.Traffic.ProtocolBytes(proto.Id) > 0);
            // 既有业务口径不变
            Assert.Equal(3L, stats.MessagesSent);
            Assert.Equal(3L, stats.MessagesReceived);
        }

        // ---- §9.4-5 null = 恢复默认预算 ----

        [Test]
        public static void ConfigureBudget_ResetToDefault()
        {
            var runtime = new NetworkRuntimeImpl();
            var transport = new LoopbackTransport();
            runtime.AttachTransport(transport);
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });

            var proto = new TestProtocol("input", NetworkDirection.ClientToServer);
            runtime.RegisterProtocol(ModA, proto, null);

            // MaxPacketRate=0：任何发送都超限
            runtime.ConfigureBudget(ModA, new NetworkBudget { MaxPacketRate = 0 });
            runtime.SendToServer(ModA, proto.Id, 1);
            Assert.Equal(1L, runtime.GetStats().DroppedThrottle);
            Assert.Equal(0L, runtime.GetStats().MessagesReceived);

            // null = 恢复默认（100 msg/s / 64KB/s / 64KB 单消息）
            runtime.ConfigureBudget(ModA, null);
            for (var i = 0; i < 50; i++) runtime.SendToServer(ModA, proto.Id, i);
            Assert.Equal(1L, runtime.GetStats().DroppedThrottle); // 不再新增
            Assert.Equal(50L, runtime.GetStats().MessagesReceived);
        }

        // ---- §9.4-6 UnregisterAll 按 owner 清理预算（Rule 20） ----

        [Test]
        public static void UnregisterAll_ClearsBudget()
        {
            var runtime = new NetworkRuntimeImpl();
            var transport = new LoopbackTransport();
            runtime.AttachTransport(transport);
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });

            var proto = new TestProtocol("input", NetworkDirection.ClientToServer);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, proto, handler);
            runtime.ConfigureBudget(ModA, new NetworkBudget { MaxPacketRate = 0 });

            runtime.SendToServer(ModA, proto.Id, 1); // 超限 → drop
            Assert.Equal(1L, runtime.GetStats().DroppedThrottle);
            Assert.Equal(0, handler.Count);
            Assert.True(runtime.Traffic.HasBudget(ModA));

            runtime.UnregisterAll(ModA); // Rule 20：预算一并清理
            Assert.True(!runtime.Traffic.HasBudget(ModA));

            // 重新注册同协议 → 默认预算 → 正常发送
            runtime.RegisterProtocol(ModA, new TestProtocol("input", NetworkDirection.ClientToServer), handler);
            runtime.SendToServer(ModA, proto.Id, 2);
            Assert.Equal(1, handler.Count);
            Assert.Equal(1L, runtime.GetStats().DroppedThrottle); // 不再新增
        }
    }
}
