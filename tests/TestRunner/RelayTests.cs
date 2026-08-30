using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Com.Game.Network;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;
using RelayServer.Allocation;
using RelayServer.Protocol;
using RelayServer.Relay;

namespace TestRunner
{
    /// <summary>
    /// Relay Provider（§11.11）测试：线格式双实现互通（Rule 19）/ Bind 与转发 / 广播 / connId 映射 /
    /// Leave 退房 / BindFail / Session 模型（CreateSession/JoinSession，假控制面 HTTP）/
    /// Auto 路径回退（§11.3）/ 五层全链路 @ Relay（协商+加密+兴趣+流量）。
    /// 拓扑：UdpRelayNode（临时端口，进程内）+ RelayClientTransport（PumpOnce 同步驱动，无后台线程）——
    /// 确定性无 flake，不依赖真实公网。
    /// </summary>
    public static class RelayTests
    {
        private static readonly byte[] Secret =
        {
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
            0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
        };

        private static readonly byte[] Psk =
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        };

        private static readonly ModId ModA = new("com.game.alpha");

        // ---- 测试协议与处理器（与 NetworkTests 同型） ----

        private sealed class TestProtocol : INetworkProtocol
        {
            private readonly string _path;
            private readonly ModId _owner;
            public NetworkDirection Direction { get; }
            public ushort Version => 1;
            public int MaxSize => 4096;

            public TestProtocol(ModId owner, string path, NetworkDirection direction)
            {
                _owner = owner;
                _path = path;
                Direction = direction;
            }

            public ProtocolId Id => ProtocolId.Of(_owner, _path);
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
            public NetworkContext LastContext;
            public void Handle(in NetworkContext context, in object message)
            {
                Count++;
                Last = message;
                LastContext = context;
            }
        }

        // ---- Relay 拓扑辅助：节点 + Host 传输 + N 客户端传输，PumpOnce 同步驱动 ----

        private sealed class RelayHarness : IDisposable
        {
            public readonly UdpRelayNode Node;
            public readonly AllocationService Allocation;
            public RelayClientTransport? Host;
            public readonly List<RelayClientTransport> Clients = new();

            public RelayHarness(byte[] secret)
            {
                Allocation = new AllocationService(secret, TimeSpan.FromMinutes(30));
                Node = new UdpRelayNode(0, secret, new RelayServer.Relay.RoomTable(), TimeSpan.FromMinutes(5));
            }

            public string RelayEndpoint => "127.0.0.1:" + Node.Port;

            /// <summary>分配房间（控制面产物，Host token）。</summary>
            public AllocationResult AllocateRoom() => Allocation.Allocate();

            public RelayClientTransport AddHost(ulong roomId, byte[] token, long expiryMs)
            {
                Host = new RelayClientTransport(new RelayClientTransport.Config
                {
                    RelayHost = "127.0.0.1",
                    RelayPort = Node.Port,
                    RoomId = roomId,
                    Token = token,
                    ExpiryMs = expiryMs,
                    IsHost = true,
                    BackgroundPump = false,
                });
                return Host;
            }

            public RelayClientTransport AddClient(ulong roomId, byte[] token, long expiryMs)
            {
                var t = new RelayClientTransport(new RelayClientTransport.Config
                {
                    RelayHost = "127.0.0.1",
                    RelayPort = Node.Port,
                    RoomId = roomId,
                    Token = token,
                    ExpiryMs = expiryMs,
                    IsHost = false,
                    BackgroundPump = false,
                });
                Clients.Add(t);
                return t;
            }

            /// <summary>泵到静止：节点 + 全部传输轮询直至无新报文（确定性无 flake）。</summary>
            public void PumpUntilQuiescent()
            {
                for (var guard = 0; guard < 200; guard++)
                {
                    var total = Node.PumpOnce();
                    if (Host is not null) total += Host.PumpOnce();
                    foreach (var c in Clients) total += c.PumpOnce();
                    if (total == 0) break;
                }
            }

            public void Dispose()
            {
                foreach (var c in Clients) { try { c.Stop(); } catch (Exception) { } }
                if (Host is not null) { try { Host.Stop(); } catch (Exception) { } }
                Node.Close();
            }
        }

        // ---- §9.5-6 RelayWire 与 relay_server 双实现互通（Rule 19：可重复定义未漂移） ----

        [Test]
        public static void RelayWire_Packet_Interop()
        {
            // 客户端侧实现编码 → 服务器侧实现解析（DataRaw）
            var clientPacket = new RelayWire.Packet(RelayWire.Type.DataRaw, 7, new byte[] { 0x4E, 0x54, 0x01, 0x02 });
            var bytes = clientPacket.Encode();
            Assert.True(RelayServer.Protocol.Packet.TryDecode(bytes, out var serverPacket),
                "服务器侧实现应能解析客户端侧编码的报文");
            Assert.Equal(MessageType.DataRaw, serverPacket.Type);
            Assert.Equal((ushort)7, serverPacket.ConnId);
            Assert.Equal(4, serverPacket.Payload.Length);
            Assert.Equal(0x4E, serverPacket.Payload.Span[0]);

            // 服务器侧实现编码 → 客户端侧实现解析（PeerLeft）
            var serverPacket2 = new RelayServer.Protocol.Packet(MessageType.PeerLeft, 9, new byte[] { 9, 0 });
            var bytes2 = serverPacket2.Encode();
            Assert.True(RelayWire.Packet.TryDecode(bytes2, out var clientDecoded),
                "客户端侧实现应能解析服务器侧编码的报文");
            Assert.Equal(RelayWire.Type.PeerLeft, clientDecoded.Type);
            Assert.Equal((ushort)9, clientDecoded.ConnId);
            Assert.Equal(2, clientDecoded.Payload.Length);

            // 非法报文（魔数错 / 版本错 / 超长）双方一致拒绝
            var bad = new byte[8];
            Assert.True(!RelayWire.Packet.TryDecode(bad, out _));
            Assert.True(!RelayServer.Protocol.Packet.TryDecode(bad, out _));
            bad[0] = 0x4D; bad[1] = 0x52; bad[2] = 99; // 版本错
            Assert.True(!RelayWire.Packet.TryDecode(bad, out _));
            Assert.True(!RelayServer.Protocol.Packet.TryDecode(bad, out _));
        }

        [Test]
        public static void RelayWire_Bind_Interop()
        {
            // 客户端侧编码 Bind 载荷（33B）→ 服务器侧 BindRequest.TryParse 解析一致
            const ulong roomId = 424242;
            const long expiryMs = 1700000000000L;
            var token = new byte[RelayWire.TokenSize];
            for (var i = 0; i < token.Length; i++) token[i] = (byte)i;

            var payload = RelayWire.EncodeBindRequest(roomId, RelayWire.RoleClient, expiryMs, token);
            Assert.Equal(33, payload.Length);
            Assert.True(RelayServer.Protocol.BindRequest.TryParse(payload, out var req));
            Assert.Equal(roomId, req.RoomId);
            Assert.Equal(RelayWire.RoleClient, req.Role);
            Assert.Equal(expiryMs, req.Expiry.ToUnixTimeMilliseconds());
            Assert.True(req.Token.Span.SequenceEqual(token), "token 经线格式往返应逐字节一致");

            // 过短载荷双方一致拒绝
            Assert.True(!RelayWire.TryParseBindRequest(new byte[32], out _, out _, out _, out _));
            Assert.True(!RelayServer.Protocol.BindRequest.TryParse(new byte[32], out _));
        }

        [Test]
        public static void RelayWire_Vectors_Decode()
        {
            // 测试向量（§14.11.3）：消费方（客户端侧 RelayWire + 服务器侧实现）解析同一字节应解出一致字段
            foreach (var vector in RelayWireVectors.All())
            {
                Assert.True(RelayWire.Packet.TryDecode(vector.Bytes, out var packet),
                    $"向量 {vector.ContractId} 应能被客户端侧实现解析");
                // 服务器侧实现对同一字节解析一致（双实现无漂移）
                Assert.True(RelayServer.Protocol.Packet.TryDecode(vector.Bytes, out var serverPacket),
                    $"向量 {vector.ContractId} 应能被服务器侧实现解析");
                Assert.Equal((MessageType)packet.Type, serverPacket.Type);
                Assert.Equal(packet.ConnId, serverPacket.ConnId);
            }

            // Bind 向量载荷字段级校验
            var bindHost = RelayWireVectors.BindHost();
            Assert.True(RelayWire.Packet.TryDecode(bindHost.Bytes, out var bindPkt));
            Assert.Equal(RelayWire.Type.BindHost, bindPkt.Type);
            Assert.True(RelayWire.TryParseBindRequest(bindPkt.Payload, out var roomId, out var role, out var expiryMs, out var token));
            Assert.Equal(42UL, roomId);
            Assert.Equal(RelayWire.RoleHost, role);
            Assert.Equal(1700000000000L, expiryMs);
            Assert.Equal(16, token.Length);
            // 服务器侧解析同一载荷一致
            Assert.True(RelayServer.Protocol.BindRequest.TryParse(bindPkt.Payload, out var serverReq));
            Assert.Equal(42UL, serverReq.RoomId);
            Assert.Equal(RelayWire.RoleHost, serverReq.Role);
            Assert.Equal(1700000000000L, serverReq.Expiry.ToUnixTimeMilliseconds());
        }

        // ---- §9.5-1/2/3/4 绑定 / 转发 / 广播 / connId 映射 / Leave ----

        [Test]
        public static void Relay_HostBind_ClientBind_DataRoundTrip()
        {
            using var harness = new RelayHarness(Secret);
            var room = harness.AllocateRoom(); // 控制面签发 Host token
            var host = harness.AddHost(room.RoomId, room.Token, room.Expiry.ToUnixTimeMilliseconds());
            var serverFrames = new List<(int Conn, byte[] Data)>();
            var clientFrames = new List<byte[]>();
            host.ReceivedOnServer += (c, d) => serverFrames.Add((c, d));

            // Host 绑定
            host.StartServer();
            harness.PumpUntilQuiescent();
            Assert.True(host.IsServerBound, "Host 应收到 BindAck（服务器侧就绪）");
            Assert.True(!host.IsBindFailed);

            // Client 解析房间码 → 绑定
            Assert.True(harness.Allocation.TryResolve(room.JoinCode, out var clientAlloc), "房间码应可解析");
            var client = harness.AddClient(room.RoomId, clientAlloc.Token, clientAlloc.Expiry.ToUnixTimeMilliseconds());
            client.ReceivedOnClient += d => clientFrames.Add(d);
            client.StartClient();
            harness.PumpUntilQuiescent();
            Assert.True(client.IsClientBound, "客户端应收到 BindAck");
            Assert.Equal(RelayWire.FirstRemoteConnId, client.LocalConnId); // 第一个远端客户端 connId=2（1 预留给 Host 本地客户端）
            Assert.True(host.ServerConnections.Contains(RelayWire.FirstRemoteConnId), "Host 应收到 PeerJoined");

            // C2S：Client → Relay → Host（connId 保持客户端自身 id）
            var c2s = new byte[] { 0x4E, 0x54, 0xAA, 0xBB, 0x01 };
            client.SendFromClient(c2s);
            harness.PumpUntilQuiescent();
            Assert.Equal(1, serverFrames.Count);
            Assert.Equal(RelayWire.FirstRemoteConnId, serverFrames[0].Conn);
            Assert.Equal(0x4E, serverFrames[0].Data[0]);
            Assert.Equal(5, serverFrames[0].Data.Length);

            // S2C：Host → Relay → Client
            var s2c = new byte[] { 0x4E, 0x54, 0xCC, 0xDD };
            host.SendFromServerTo(RelayWire.FirstRemoteConnId, s2c);
            harness.PumpUntilQuiescent();
            Assert.Equal(1, clientFrames.Count);
            Assert.Equal(0xCC, clientFrames[0][2]);
        }

        [Test]
        public static void Relay_Broadcast_ToAllClients()
        {
            using var harness = new RelayHarness(Secret);
            var room = harness.AllocateRoom();
            var host = harness.AddHost(room.RoomId, room.Token, room.Expiry.ToUnixTimeMilliseconds());
            host.StartServer();
            harness.PumpUntilQuiescent();

            var clients = new List<(RelayClientTransport T, List<byte[]> Frames)>();
            for (var i = 0; i < 2; i++)
            {
                Assert.True(harness.Allocation.TryResolve(room.JoinCode, out var a));
                var c = harness.AddClient(room.RoomId, a.Token, a.Expiry.ToUnixTimeMilliseconds());
                var frames = new List<byte[]>();
                c.ReceivedOnClient += d => frames.Add(d);
                clients.Add((c, frames));
                c.StartClient();
                harness.PumpUntilQuiescent();
            }

            // Host SendFromServerAll → 全部客户端（含本地回环 conn 1 与远端 2/3）
            var payload = new byte[] { 0x4E, 0x54, 0x77 };
            host.SendFromServerAll(payload);
            harness.PumpUntilQuiescent();

            Assert.Equal(2, clients.Count);
            foreach (var (_, frames) in clients)
            {
                Assert.Equal(1, frames.Count, "每个客户端都应收到广播");
                Assert.Equal(0x77, frames[0][2]);
            }
            Assert.Equal(3, host.ServerConnections.Count); // 本地 1 + 远端 2/3
        }

        [Test]
        public static void Relay_ConnId_Mapping()
        {
            using var harness = new RelayHarness(Secret);
            var room = harness.AllocateRoom();
            var host = harness.AddHost(room.RoomId, room.Token, room.Expiry.ToUnixTimeMilliseconds());
            var serverFrames = new List<(int Conn, byte[] Data)>();
            host.ReceivedOnServer += (c, d) => serverFrames.Add((c, d));
            host.StartServer();
            harness.PumpUntilQuiescent();

            var expected = new List<int>();
            var transports = new List<RelayClientTransport>();
            for (var i = 0; i < 3; i++)
            {
                Assert.True(harness.Allocation.TryResolve(room.JoinCode, out var a));
                var c = harness.AddClient(room.RoomId, a.Token, a.Expiry.ToUnixTimeMilliseconds());
                transports.Add(c);
                c.StartClient();
                harness.PumpUntilQuiescent();
                expected.Add(c.LocalConnId);
            }
            Assert.Equal(3, expected.Count); // 远端从 2 起顺序分配（1 预留给 Host 本地客户端）
            Assert.Equal(2, expected[0]);
            Assert.Equal(3, expected[1]);
            Assert.Equal(4, expected[2]);

            for (var i = 0; i < transports.Count; i++)
            {
                transports[i].SendFromClient(new byte[] { (byte)i });
            }
            harness.PumpUntilQuiescent();
            Assert.Equal(3, serverFrames.Count);
            for (var i = 0; i < expected.Count; i++)
                Assert.Equal(expected[i], serverFrames[i].Conn); // Host 侧 connId = 各客户端分配 id（§6.1）
        }

        [Test]
        public static void Relay_Leave_NotifiesHost()
        {
            using var harness = new RelayHarness(Secret);
            var room = harness.AllocateRoom();
            var host = harness.AddHost(room.RoomId, room.Token, room.Expiry.ToUnixTimeMilliseconds());
            host.StartServer();
            harness.PumpUntilQuiescent();

            Assert.True(harness.Allocation.TryResolve(room.JoinCode, out var a));
            var client = harness.AddClient(room.RoomId, a.Token, a.Expiry.ToUnixTimeMilliseconds());
            client.StartClient();
            harness.PumpUntilQuiescent();
            Assert.True(host.ServerConnections.Contains(client.LocalConnId));

            // 客户端 Stop → Leave → 节点移除 → Host 收 PeerLeft → 连接表收缩
            client.Stop();
            harness.PumpUntilQuiescent();
            Assert.True(!host.ServerConnections.Contains(client.LocalConnId), "Host 连接表应移除离开的客户端");
            Assert.Equal(1, host.ServerConnections.Count); // 只剩本地回环 conn 1
        }

        [Test]
        public static void Relay_BindFail_NoHost_Or_WrongToken()
        {
            using var harness = new RelayHarness(Secret);
            var room = harness.AllocateRoom();

            // 场景 1：房间无主（Host 未绑定）→ BindFail
            Assert.True(harness.Allocation.TryResolve(room.JoinCode, out var a));
            var early = harness.AddClient(room.RoomId, a.Token, a.Expiry.ToUnixTimeMilliseconds());
            early.StartClient();
            harness.PumpUntilQuiescent();
            Assert.True(early.IsBindFailed, "房间无主时 BindClient 应被拒绝");
            Assert.True(!early.IsClientBound);

            // 场景 2：token 错误（HMAC 校验不过）→ BindFail
            var host = harness.AddHost(room.RoomId, room.Token, room.Expiry.ToUnixTimeMilliseconds());
            host.StartServer();
            harness.PumpUntilQuiescent();
            var badToken = new byte[RelayWire.TokenSize];
            var bad = harness.AddClient(room.RoomId, badToken, room.Expiry.ToUnixTimeMilliseconds());
            bad.StartClient();
            harness.PumpUntilQuiescent();
            Assert.True(bad.IsBindFailed, "错误 token 应被节点拒绝");
            Assert.True(host.ServerConnections.Count == 1, "被拒客户端不应出现在 Host 连接表");
        }

        // ---- §9.5-5 控制面 → 线格式 → 节点校验全链路 ----

        [Test]
        public static void Relay_Allocation_Code_Flow()
        {
            using var harness = new RelayHarness(Secret);
            var room = harness.AllocateRoom();
            Assert.True(room.JoinCode.Length >= 6, "房间码应为 6 位可读码");
            Assert.Equal(RelayWire.TokenSize, room.Token.Length);

            // 控制面产物 → RelayWire 编码 Bind → 节点 HMAC 校验通过（两端互通性实证）
            var host = harness.AddHost(room.RoomId, room.Token, room.Expiry.ToUnixTimeMilliseconds());
            host.StartServer();
            harness.PumpUntilQuiescent();
            Assert.True(host.IsServerBound, "AllocationService 签发的 Host token 应通过节点校验");

            // 客户端 resolve → 节点校验 RoleClient token
            Assert.True(harness.Allocation.TryResolve(room.JoinCode, out var clientAlloc));
            Assert.Equal(room.RoomId, clientAlloc.RoomId);
            var client = harness.AddClient(room.RoomId, clientAlloc.Token, clientAlloc.Expiry.ToUnixTimeMilliseconds());
            client.StartClient();
            harness.PumpUntilQuiescent();
            Assert.True(client.IsClientBound);

            // 换房主（roomId 对不上 token 内签名）→ BindFail
            var other = harness.AllocateRoom();
            var wrongRoom = harness.AddClient(other.RoomId, room.Token, room.Expiry.ToUnixTimeMilliseconds());
            wrongRoom.StartClient();
            harness.PumpUntilQuiescent();
            Assert.True(wrongRoom.IsBindFailed, "token 与 roomId 不匹配应被拒绝（无状态 HMAC）");
        }

        // ---- §9.5 Session 模型：CreateSession / JoinSession（假控制面 HTTP，不依赖真实公网） ----

        [Test]
        public static void Relay_CreateSession_JoinSession_RoundTrip()
        {
            using var harness = new RelayHarness(Secret);
            using var plane = new FakeControlPlane(Secret, harness.RelayEndpoint);

            // Host：CreateSession() → /allocate → 房间码
            var allocation = new RelayAllocationClient(plane.BaseUrl);
            var session = RelaySessions.CreateSession(allocation, "127.0.0.1", harness.Node.Port);
            Assert.True(session.JoinCode.Length >= 6, "CreateSession 应返回房间码");
            Assert.True(session.RoomId > 0);

            var host = harness.AddHost(session.RoomId, session.Token, session.ExpiryMs);
            var serverFrames = new List<(int Conn, byte[] Data)>();
            host.ReceivedOnServer += (c, d) => serverFrames.Add((c, d));
            host.StartServer();
            harness.PumpUntilQuiescent();
            Assert.True(host.IsServerBound);

            // Client：JoinSession(房间码) → /resolve → token → BindClient → 转发建立
            Assert.True(RelaySessions.TryJoinSession(allocation, session.JoinCode, "127.0.0.1", harness.Node.Port, out var clientSession));
            Assert.Equal(session.RoomId, clientSession.RoomId);
            var clientFrames = new List<byte[]>();
            var client = harness.AddClient(clientSession.RoomId, clientSession.Token, clientSession.ExpiryMs);
            client.ReceivedOnClient += d => clientFrames.Add(d);
            client.StartClient();
            harness.PumpUntilQuiescent();
            Assert.True(client.IsClientBound, "JoinSession 后客户端应绑定成功");

            // 转发：C2S 经 Relay 到 Host
            client.SendFromClient(new byte[] { 0x01, 0x02, 0x03 });
            harness.PumpUntilQuiescent();
            Assert.Equal(1, serverFrames.Count);
            Assert.Equal(3, serverFrames[0].Data.Length);

            // 无效房间码 → JoinSession 失败
            Assert.True(!RelaySessions.TryJoinSession(allocation, "ZZZZZZ", "127.0.0.1", harness.Node.Port, out _),
                "无效房间码 JoinSession 应失败");
        }

        // ---- §11.3 Auto：先 Direct（探测成功），失败回退 Relay ----

        [Test]
        public static void Relay_Auto_UsesDirect_WhenAvailable()
        {
            Assert.Equal(NetworkPath.Direct, AutoPathResolver.Resolve(() => true));
        }

        [Test]
        public static void Relay_Auto_FallsBackToRelay()
        {
            // 探测失败 → 回退 Relay：走 Session 模型 + Relay 转发，业务无感知（§11.3）
            Assert.Equal(NetworkPath.Relay, AutoPathResolver.Resolve(() => false));
            Assert.Equal(NetworkPath.Relay, AutoPathResolver.Resolve(null));

            using var harness = new RelayHarness(Secret);
            using var plane = new FakeControlPlane(Secret, harness.RelayEndpoint);
            var allocation = new RelayAllocationClient(plane.BaseUrl);

            // 回退路径 = CreateSession/JoinSession + Relay 传输
            var session = RelaySessions.CreateSession(allocation, "127.0.0.1", harness.Node.Port);
            var host = harness.AddHost(session.RoomId, session.Token, session.ExpiryMs);
            host.StartServer();
            harness.PumpUntilQuiescent();
            Assert.True(host.IsServerBound);

            Assert.True(RelaySessions.TryJoinSession(allocation, session.JoinCode, "127.0.0.1", harness.Node.Port, out var clientSession));
            var client = harness.AddClient(clientSession.RoomId, clientSession.Token, clientSession.ExpiryMs);
            var serverFrames = new List<(int Conn, byte[] Data)>();
            host.ReceivedOnServer += (c, d) => serverFrames.Add((c, d));
            client.StartClient();
            harness.PumpUntilQuiescent();

            client.SendFromClient(new byte[] { 0x42 });
            harness.PumpUntilQuiescent();
            Assert.Equal(1, serverFrames.Count, "Auto 回退后数据应经 Relay 正常到达 Host");
            Assert.Equal(client.LocalConnId, serverFrames[0].Conn);
        }

        // ---- §9.6 全链路 @ Relay：协商（§11.6）+ 加密（§11.12）+ 兴趣（§11.8）+ 流量（§11.13）组合 ----

        [Test]
        public static void Relay_FullPipeline_OverRelay()
        {
            using var harness = new RelayHarness(Secret);
            var room = harness.AllocateRoom();

            // Host 运行时（Role=Host × Path=Relay）：StartServer 走 BindHost，本地客户端走回环（§11.3）
            var hostRuntime = new NetworkRuntimeImpl();
            var hostTransport = harness.AddHost(room.RoomId, room.Token, room.Expiry.ToUnixTimeMilliseconds());
            var input = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var state = new TestProtocol(ModA, "state", NetworkDirection.ServerToClient);
            var serverHandler = new CollectHandler();
            var hostLocalHandler = new CollectHandler(); // Host 本地玩家（回环）的 S2C 处理器
            hostRuntime.RegisterProtocol(ModA, input, serverHandler);
            hostRuntime.RegisterProtocol(ModA, state, hostLocalHandler);
            hostRuntime.AttachTransport(hostTransport);
            hostRuntime.Security.Enable(Psk);
            hostRuntime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Relay });
            harness.PumpUntilQuiescent(); // BindAck
            Assert.True(hostTransport.IsServerBound);
            Assert.True(hostRuntime.Negotiation.IsNegotiated(RelayClientTransport.LocalConnectionId),
                "Host 本地玩家应在 Start 内完成协商（回环硬规则，§11.3）");

            // 远端客户端运行时（Role=Client × Path=Relay）
            Assert.True(harness.Allocation.TryResolve(room.JoinCode, out var clientAlloc));
            var clientRuntime = new NetworkRuntimeImpl();
            var clientTransport = harness.AddClient(room.RoomId, clientAlloc.Token, clientAlloc.Expiry.ToUnixTimeMilliseconds());
            var clientHandler = new CollectHandler();
            clientRuntime.RegisterProtocol(ModA, input, null);
            clientRuntime.RegisterProtocol(ModA, state, clientHandler);
            clientRuntime.AttachTransport(clientTransport);
            clientRuntime.Security.Enable(Psk);
            clientRuntime.Start(new NetworkConfig { Role = NetworkRole.Client, Path = NetworkPath.Relay });
            harness.PumpUntilQuiescent(); // BindAck + PeerJoined + 握手 hello/result 经 Relay 完成
            Assert.True(clientRuntime.Negotiation.IsNegotiated(NegotiationLayer.ClientConnId),
                "远端客户端应经 Relay 完成版本协商");

            // 业务帧 C2S：全管线（协商校验→编码→流量→加密→Relay 信封→解密→解码）
            clientRuntime.SendToServer(ModA, input.Id, 123);
            harness.PumpUntilQuiescent();
            Assert.Equal(1, serverHandler.Count);
            Assert.Equal(123, (int)serverHandler.Last!);
            Assert.True(serverHandler.LastContext.IsServer);
            Assert.Equal(clientTransport.LocalConnId, serverHandler.LastContext.ConnectionId);
            Assert.Equal(0L, hostRuntime.GetStats().DroppedAuth);
            Assert.Equal(0L, hostRuntime.GetStats().DroppedReplay);

            // 广播：Host → 本地回环 + 远端（兴趣默认全发）
            hostRuntime.Broadcast(ModA, state.Id, 7);
            harness.PumpUntilQuiescent();
            Assert.Equal(1, clientHandler.Count, "远端客户端应收到广播");
            Assert.Equal(7, (int)clientHandler.Last!);
            Assert.Equal(1, hostLocalHandler.Count, "Host 本地玩家应经回环收到广播");

            // 兴趣过滤（§11.8）：只放行本地玩家 → 远端被过滤（过滤发生在 Encode 之前，不产生 Relay 流量）
            hostRuntime.RegisterInterest(ModA, state.Id, new LocalOnlyInterest());
            hostRuntime.Broadcast(ModA, state.Id, 8);
            harness.PumpUntilQuiescent();
            Assert.Equal(1, clientHandler.Count, "被兴趣过滤的远端不应收到");
            Assert.Equal(2, hostLocalHandler.Count, "本地玩家不受影响");

            // 流量配额（§11.13）：预算 MaxPacketRate=5，窗口内已有 1 条 → 再发 4 条填满，第 6 条被 Throttle
            clientRuntime.ConfigureBudget(ModA, new NetworkBudget { MaxPacketRate = 5 });
            for (var i = 0; i < 4; i++) clientRuntime.SendToServer(ModA, input.Id, 200 + i);
            harness.PumpUntilQuiescent();
            Assert.Equal(5, serverHandler.Count, "预算内 5 条应全部送达（1 条 + 4 条）");
            clientRuntime.SendToServer(ModA, input.Id, 299);
            harness.PumpUntilQuiescent();
            Assert.Equal(5, serverHandler.Count, "第 6 条应被限流（窗口内已 5 条）");
            Assert.Equal(1L, clientRuntime.GetStats().DroppedThrottle);

            // 三级统计（§11.13）与加密前后两档
            Assert.True(clientRuntime.GetStats().BytesByProtocol[input.Id] > 0, "Protocol 级统计");
            Assert.True(hostRuntime.GetStats().BytesByConnection[clientTransport.LocalConnId] > 0, "Connection 级统计（服务器收包）");
            Assert.True(clientRuntime.Traffic.ModTransportBytes(ModA) > clientRuntime.Traffic.ModLogicalBytes(ModA),
                "加密后传输字节应大于逻辑字节（§11.13 两档）");
            Assert.True(clientRuntime.GetStats().MessagesReceived > 0);
        }

        /// <summary>兴趣过滤（§11.8）：只放行 Host 本地玩家回环 connId=1，过滤全部远端。</summary>
        private sealed class LocalOnlyInterest : IInterestManager
        {
            public bool IsRelevant(int connectionId, ProtocolId protocolId, in object message)
                => connectionId == RelayClientTransport.LocalConnectionId;
        }

        // ---- 假控制面：TcpListener 伪造 HTTP（/allocate /resolve /health），规避 HttpListener URL ACL 依赖 ----

        private sealed class FakeControlPlane : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new();
            private readonly AllocationService _allocation;
            private readonly string _relayEndpoint;
            private readonly Task _task;

            public FakeControlPlane(byte[] secret, string relayEndpoint)
            {
                _allocation = new AllocationService(secret, TimeSpan.FromMinutes(30));
                _relayEndpoint = relayEndpoint;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _task = Task.Run(() => AcceptLoop());
            }

            public int Port { get; }
            public string BaseUrl => $"http://127.0.0.1:{Port}";

            private void AcceptLoop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch (Exception) { break; }
                    _ = Task.Run(() => Handle(client));
                }
            }

            private void Handle(TcpClient client)
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var header = ReadRequest(stream, out var body);
                        var lines = header.Split("\r\n");
                        var parts = lines.Length > 0 ? lines[0].Split(' ') : Array.Empty<string>();
                        if (parts.Length < 2) return;
                        var method = parts[0];
                        var path = parts[1];

                        string responseBody;
                        var status = 200;
                        if (path == "/allocate" && method == "POST")
                        {
                            var r = _allocation.Allocate();
                            responseBody = PipeText(r);
                        }
                        else if (path == "/resolve" && method == "POST")
                        {
                            var code = ExtractJoinCode(body);
                            if (code is null || !_allocation.TryResolve(code, out var r))
                            {
                                status = 404;
                                responseBody = "invalid join code";
                            }
                            else
                            {
                                responseBody = PipeText(r);
                            }
                        }
                        else if (path == "/health" && method == "GET")
                        {
                            responseBody = "{\"status\":\"ok\",\"rooms\":0}";
                        }
                        else
                        {
                            status = 404;
                            responseBody = "not found";
                        }

                        var statusText = status == 200 ? "OK" : "Not Found";
                        var resp = $"HTTP/1.1 {status} {statusText}\r\n" +
                                   $"Content-Length: {Encoding.UTF8.GetByteCount(responseBody)}\r\n" +
                                   "Content-Type: text/plain\r\nConnection: close\r\n\r\n" + responseBody;
                        var bytes = Encoding.UTF8.GetBytes(resp);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                catch (Exception) { /* 单请求失败不影响其他 */ }
            }

            private static string ReadRequest(NetworkStream stream, out string body)
            {
                var sb = new StringBuilder();
                var buf = new byte[1];
                body = "";
                var headerDone = false;
                while (sb.Length < 8192)
                {
                    var n = stream.Read(buf, 0, 1);
                    if (n == 0) break;
                    sb.Append((char)buf[0]);
                    if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
                    {
                        headerDone = true;
                        break;
                    }
                }
                if (!headerDone) return sb.ToString();
                var header = sb.ToString(0, sb.Length - 4);

                var contentLength = 0;
                foreach (var line in header.Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
                }
                if (contentLength > 0)
                {
                    var bodyBytes = new byte[contentLength];
                    var read = 0;
                    while (read < contentLength)
                    {
                        var n = stream.Read(bodyBytes, read, contentLength - read);
                        if (n == 0) break;
                        read += n;
                    }
                    body = Encoding.UTF8.GetString(bodyBytes, 0, read);
                }
                return header;
            }

            private static string? ExtractJoinCode(string body)
            {
                // {"joinCode":"ABCDEF"} 极简解析（假服务端，无需完整 JSON 库）
                var idx = body.IndexOf("\"joinCode\"", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                var quote1 = body.IndexOf('"', idx + 10);
                if (quote1 < 0) return null;
                var quote2 = body.IndexOf('"', quote1 + 1);
                if (quote2 < 0) return null;
                return body.Substring(quote1 + 1, quote2 - quote1 - 1);
            }

            private string PipeText(AllocationResult r) =>
                $"{r.JoinCode}|{r.RoomId}|{_relayEndpoint}|{Convert.ToBase64String(r.Token)}|{r.Expiry.ToUnixTimeMilliseconds()}";

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Stop(); } catch (Exception) { }
                try { _task.Wait(1000); } catch (Exception) { }
            }
        }
    }
}
