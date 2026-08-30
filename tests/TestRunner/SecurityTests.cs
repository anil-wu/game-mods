using System;
using System.Collections.Generic;
using System.Linq;
using Com.Game.Network;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// 加密（§11.12）测试：加密往返 / 密文隐藏 ProtocolId（Relay 不可见）/ 篡改丢弃 /
    /// 错误密钥拒收 / 序号防重放（含乱序容忍）/ 密钥不泄露给 Mod API / 加密+流量配额共存。
    /// 拓扑：BridgeFixture（跨运行时，两端各自 Enable 相同 psk）+ RecordingTransport（捕获密文报文，可重放/乱序）。
    /// </summary>
    public static class SecurityTests
    {
        private static readonly ModId ModA = new("com.game.alpha");

        private static readonly byte[] Psk =
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        };

        private static readonly byte[] OtherPsk =
        {
            0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22,
            0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0x00,
        };

        private sealed class TestProtocol : INetworkProtocol
        {
            private readonly ModId _owner;
            private readonly string _path;
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

        /// <summary>记录型传输：捕获 C2S/S2C 密文报文；Manual 模式下只记录不投递（用于乱序/重放测试）。</summary>
        private sealed class RecordingTransport : INetworkTransport
        {
            public readonly List<byte[]> C2S = new();
            public readonly List<byte[]> S2C = new();
            public bool Manual;

            public event Action<byte[]>? ReceivedOnClient;
            public event Action<int, byte[]>? ReceivedOnServer;
            public event Action? ClientReady;

            public IReadOnlyCollection<int> ServerConnections { get; } = new List<int> { 1 };

            public void StartClient() => ClientReady?.Invoke();
            public void StartServer() { }
            public void Stop() { }

            public void SendFromClient(byte[] frame)
            {
                C2S.Add(frame);
                if (!Manual) ReceivedOnServer?.Invoke(1, frame);
            }

            public void SendFromServerTo(int connectionId, byte[] frame)
            {
                S2C.Add(frame);
                ReceivedOnClient?.Invoke(frame);
            }

            public void SendFromServerAll(byte[] frame)
            {
                S2C.Add(frame);
                ReceivedOnClient?.Invoke(frame);
            }
        }

        /// <summary>记录密钥注入的假 provider：断言密钥只经 SetupSession 进入（§11.12 硬要求）。</summary>
        private sealed class RecordingProvider : INetworkSecurityProvider
        {
            public string Name => "recording-fake";
            public int Overhead => 0;
            public byte[]? SetupKey;
            public ushort SetupKeyId;
            public void SetupSession(byte[] sessionKey, ushort keyId)
            {
                SetupKey = sessionKey;
                SetupKeyId = keyId;
            }
            public byte[] Encrypt(byte[] frame) => frame;
            public byte[]? Decrypt(byte[] ciphertext) => ciphertext;
        }

        // ---- §9.3-1 加密往返（跨运行时 Bridge 拓扑，hello/result/业务帧全部走密文） ----

        [Test]
        public static void Encrypt_Decrypt_RoundTrip()
        {
            using var f = BridgeFixture.CreateServer();
            f.Server.Security.Enable(Psk);
            var input = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var state = new TestProtocol(ModA, "state", NetworkDirection.ServerToClient);
            var serverHandler = new CollectHandler();
            f.Server.RegisterProtocol(ModA, input, serverHandler);
            f.Server.RegisterProtocol(ModA, state, null);

            var client = f.AddClient();
            client.Security.Enable(Psk);
            var clientHandler = new CollectHandler();
            client.RegisterProtocol(ModA, input, null);
            client.RegisterProtocol(ModA, state, clientHandler);
            f.StartClients();

            // 握手在密文下完成（Hello/Result 均加密）→ 业务帧往返
            client.SendToServer(ModA, input.Id, 123);
            Assert.Equal(1, serverHandler.Count);
            Assert.Equal(123, (int)serverHandler.Last!);
            Assert.True(serverHandler.LastContext.IsServer);
            Assert.Equal(0L, f.Server.GetStats().DroppedAuth);
            Assert.Equal(0L, f.Server.GetStats().DroppedReplay);

            f.Server.SendToClient(ModA, 1, state.Id, 7);
            Assert.Equal(1, clientHandler.Count);
            Assert.Equal(7, (int)clientHandler.Last!);
            Assert.True(!clientHandler.LastContext.IsServer);
            Assert.Equal(0L, client.GetStats().DroppedAuth);
            Assert.Equal(0L, client.GetStats().DroppedReplay);
        }

        // ---- §9.3-2 密文隐藏 ProtocolId：Relay 视角只能看到信封（§11.12） ----

        [Test]
        public static void Ciphertext_HidesProtocolId()
        {
            var transport = new RecordingTransport();
            var runtime = new NetworkRuntimeImpl();
            runtime.AttachTransport(transport);
            runtime.Security.Enable(Psk);
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });
            var proto = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, proto, handler);

            runtime.SendToServer(ModA, proto.Id, 123);
            var wire = transport.C2S[^1];

            // 信封结构：Magic u16 + Flags + KeyId u16 + Seq u64 + CipherLen u32 + 密文 + Tag
            Assert.Equal(SecurityLayer.Magic, (ushort)(wire[0] | (wire[1] << 8)));
            var cipherLen = wire[13] | (wire[14] << 8) | (wire[15] << 16) | (wire[16] << 24);
            Assert.Equal(SecurityLayer.HeaderSize + cipherLen + SecurityLayer.TagSize, wire.Length);

            // 明文帧头（ProtocolId 4B LE）与业务载荷（123 的 varint F6 01）在密文中不可见
            var pidBytes = new[]
            {
                (byte)proto.Id.Value, (byte)(proto.Id.Value >> 8),
                (byte)(proto.Id.Value >> 16), (byte)(proto.Id.Value >> 24),
            };
            Assert.True(!ContainsBytes(wire, pidBytes), "密文不应包含明文 ProtocolId 字节序列（Relay 不可见）");
            Assert.True(!ContainsBytes(wire, new byte[] { 0xF6, 0x01 }), "密文不应包含明文业务载荷");
            Assert.Equal(1, handler.Count); // 解密后正常送达
        }

        // ---- §9.3-3 篡改密文 → DroppedAuth，Handler 不触发 ----

        [Test]
        public static void TamperedCiphertext_Dropped()
        {
            var transport = new RecordingTransport();
            var runtime = new NetworkRuntimeImpl();
            runtime.AttachTransport(transport);
            runtime.Security.Enable(Psk);
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });
            var proto = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, proto, handler);

            runtime.SendToServer(ModA, proto.Id, 123); // seq1 正常送达
            Assert.Equal(1, handler.Count);

            var wire = transport.C2S[^1];
            var tampered = (byte[])wire.Clone();
            tampered[SecurityLayer.HeaderSize] ^= 0xFF; // 翻转密文首字节

            transport.SendFromClient(tampered); // 注入篡改报文
            Assert.Equal(1L, runtime.GetStats().DroppedAuth); // HMAC 校验失败（encrypt-then-MAC）
            Assert.Equal(0L, runtime.GetStats().DroppedReplay); // 认证失败不算重放
            Assert.Equal(1, handler.Count); // Handler 不触发
        }

        // ---- §9.3-4 两端 psk 不一致 → 握手帧/业务帧全部认证失败 ----

        [Test]
        public static void WrongKey_Dropped()
        {
            using var f = BridgeFixture.CreateServer();
            f.Server.Security.Enable(Psk);
            var input = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var serverHandler = new CollectHandler();
            f.Server.RegisterProtocol(ModA, input, serverHandler);

            var client = f.AddClient();
            client.Security.Enable(OtherPsk); // 不同 psk
            client.RegisterProtocol(ModA, input, null);
            f.StartClients();

            // 客户端 hello 密文认证失败 → 服务器 DroppedAuth，永不协商
            Assert.True(f.Server.GetStats().DroppedAuth > 0, "密钥不匹配的 hello 应认证失败");
            Assert.Equal(0, serverHandler.Count);
            Assert.True(!f.Server.Negotiation.IsNegotiated(1));
            // 客户端未收到 result → 未协商 → 发送抛 NetworkException
            Assert.Throws<NetworkException>(() => client.SendToServer(ModA, input.Id, 1));
        }

        // ---- §9.3-5 序号防重放：重放拒绝 + 窗口内乱序容忍 ----

        [Test]
        public static void Replay_Seq_Dropped()
        {
            var transport = new RecordingTransport();
            var runtime = new NetworkRuntimeImpl();
            runtime.AttachTransport(transport);
            runtime.Security.Enable(Psk);
            runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });
            var proto = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var handler = new CollectHandler();
            runtime.RegisterProtocol(ModA, proto, handler);

            runtime.SendToServer(ModA, proto.Id, 1); // 合法帧送达
            Assert.Equal(1, handler.Count);
            var w1 = transport.C2S[^1];

            // 重放同一密文报文 → DroppedReplay++，Handler 不触发
            transport.SendFromClient(w1);
            Assert.Equal(1L, runtime.GetStats().DroppedReplay);
            Assert.Equal(1, handler.Count);

            // 乱序容忍：捕获 seq3/seq4 报文，先投递 seq4 再投递 seq3（窗口内乱序均可接受）
            transport.Manual = true;
            runtime.SendToServer(ModA, proto.Id, 2); // 生成 seq3 报文（hello 占用 seq1，业务1 为 seq2）
            var w3 = transport.C2S[^1];
            runtime.SendToServer(ModA, proto.Id, 3); // 生成 seq4 报文
            var w4 = transport.C2S[^1];
            transport.Manual = false;

            transport.SendFromClient(w4); // 先投递 seq4
            transport.SendFromClient(w3); // 再投递 seq3（乱序）
            Assert.Equal(3, handler.Count);
            Assert.Equal(1L, runtime.GetStats().DroppedReplay); // 乱序不产生重放丢包

            // 重放 seq4 → 拒绝
            transport.SendFromClient(w4);
            Assert.Equal(2L, runtime.GetStats().DroppedReplay);
            Assert.Equal(3, handler.Count);
        }

        // ---- §9.3-6 密钥不泄露给 Mod：INetworkContext 无密钥成员（编译期保证）+ 密钥只经 SetupSession 进入 provider ----

        [Test]
        public static void KeyNotExposed_ToModApi()
        {
            // 编译期/反射保证：INetworkContext（业务 Mod 唯一网络入口）无任何密钥相关成员（§11.12 硬要求）
            foreach (var member in typeof(INetworkContext).GetMembers())
            {
                var n = member.Name.ToLowerInvariant();
                Assert.True(!n.Contains("key") && !n.Contains("secret") &&
                            !n.Contains("crypto") && !n.Contains("security"),
                    $"INetworkContext 不应暴露密钥相关成员: {member.Name}");
            }

            // 密钥只经 SetupSession 进入 provider：派生后 32B、非原始 psk（psk 仅存在于 Network.Mod 内部）
            var providers = new List<RecordingProvider>();
            var runtime = new NetworkRuntimeImpl(() =>
            {
                var p = new RecordingProvider();
                providers.Add(p);
                return p;
            });
            runtime.Security.Enable(Psk);

            Assert.Equal(2, providers.Count); // C2S + S2C 两个方向 provider
            foreach (var p in providers)
            {
                Assert.True(p.SetupKey is not null, "密钥只经 SetupSession 进入 provider");
                Assert.Equal(32, p.SetupKey!.Length); // 派生后 32B
                Assert.True(!p.SetupKey.SequenceEqual(Psk), "provider 不应拿到原始 psk（必须经派生）");
                Assert.Equal((ushort)1, p.SetupKeyId);
            }
        }

        // ---- 全链路：加密 + 流量配额共存（预算内往返 + 预算超限 Throttle + 加密前后两档统计） ----

        [Test]
        public static void EncryptionAndTraffic_Coexist_RoundTrip()
        {
            using var f = BridgeFixture.CreateServer();
            f.Server.Security.Enable(Psk);
            var input = new TestProtocol(ModA, "input", NetworkDirection.ClientToServer);
            var serverHandler = new CollectHandler();
            f.Server.RegisterProtocol(ModA, input, serverHandler);

            var client = f.AddClient();
            client.Security.Enable(Psk);
            client.RegisterProtocol(ModA, input, null);
            f.StartClients();

            // 预算内 5 条往返（默认预算 100 msg/s 不触发）
            for (var i = 0; i < 5; i++) client.SendToServer(ModA, input.Id, i);
            Assert.Equal(5, serverHandler.Count);
            Assert.Equal(0L, client.GetStats().DroppedThrottle);
            Assert.Equal(0L, f.Server.GetStats().DroppedAuth);

            // 加密前后两档（§11.13）：Mod 级传输（加密后）> 逻辑字节
            Assert.True(client.Traffic.ModTransportBytes(ModA) > client.Traffic.ModLogicalBytes(ModA),
                "加密后传输字节应大于逻辑字节（§11.13 加密前后两档统计）");
            // 三级统计
            Assert.True(client.GetStats().BytesByProtocol[input.Id] > 0); // Protocol 级
            Assert.True(f.Server.GetStats().BytesByConnection[1] > 0);    // Connection 级（服务器收包）
            Assert.True(client.GetStats().BytesByConnection[0] > 0);      // Connection 级（客户端收发）

            // 预算超限 → Throttle（发送侧执行）
            client.ConfigureBudget(ModA, new NetworkBudget { MaxPacketRate = 5 });
            client.SendToServer(ModA, input.Id, 99);
            Assert.Equal(5, serverHandler.Count); // 未送达
            Assert.Equal(1L, client.GetStats().DroppedThrottle);
        }

        private static bool ContainsBytes(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || needle.Length > haystack.Length) return false;
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var found = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { found = false; break; }
                }
                if (found) return true;
            }
            return false;
        }
    }
}
