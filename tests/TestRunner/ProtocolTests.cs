using System;
using Game.Messaging;
using Game.Mod.Contract;
using Com.Game.Protocol;

namespace TestRunner
{
    public static class ProtocolTests
    {
        private struct TestMsg { public int X; public int Y; }
        private struct OtherMsg { public int Z; }

        private sealed class TestMsgCodec : IMessageCodec<TestMsg>
        {
            public byte[] Encode(TestMsg m)
            {
                var b = new byte[8];
                BitConverter.GetBytes(m.X).CopyTo(b, 0);
                BitConverter.GetBytes(m.Y).CopyTo(b, 4);
                return b;
            }

            public TestMsg Decode(byte[] d) =>
                new TestMsg { X = BitConverter.ToInt32(d, 0), Y = BitConverter.ToInt32(d, 4) };
        }

        private sealed class OtherMsgCodec : IMessageCodec<OtherMsg>
        {
            public byte[] Encode(OtherMsg m) => BitConverter.GetBytes(m.Z);
            public OtherMsg Decode(byte[] d) => new OtherMsg { Z = BitConverter.ToInt32(d, 0) };
        }

        private static (ProtocolClient client, ProtocolServer server) Setup()
        {
            var bus = new MessageBus();
            return (new ProtocolClient(bus), new ProtocolServer(bus));
        }

        [Test]
        public static void DeterministicId()
        {
            var (client, server) = Setup();
            var owner = new ModId("com.test.mod");
            var cid = client.Register<TestMsg>(owner, new TestMsgCodec());
            var sid = server.Register<TestMsg>(owner, new TestMsgCodec());
            Assert.Equal(cid, sid, "两端同一类型同一 msgId");
            Assert.True(cid != 0, "msgId 非 0");
        }

        [Test]
        public static void OrderIndependence()
        {
            var (client, server) = Setup();
            var owner = new ModId("com.test.mod");
            var aId = client.Register<TestMsg>(owner, new TestMsgCodec());
            server.Register<OtherMsg>(owner, new OtherMsgCodec());  // 服务端先注册别的类型
            var bId = server.Register<TestMsg>(owner, new TestMsgCodec());
            Assert.Equal(aId, bId, "注册顺序无关，msgId 仍一致");
        }

        [Test]
        public static void Upstream_RoundTrip()
        {
            var (client, server) = Setup();
            var cOwner = new ModId("com.test.client");
            var sOwner = new ModId("com.test.server");
            client.Register<TestMsg>(cOwner, new TestMsgCodec());
            server.Register<TestMsg>(sOwner, new TestMsgCodec());

            TestMsg? received = null;
            server.Handle<TestMsg>(sOwner, m => { received = m; return null; });

            byte[]? sent = null;
            client.SendToServer = d => { sent = d; return d.Length; };
            client.Send(new TestMsg { X = 42, Y = -7 });

            Assert.True(sent != null && sent.Length == 12, "帧 [msgId u32][payload 8B] = 12 字节");
            server.Receive(sent!);
            Assert.True(received.HasValue && received.Value.X == 42 && received.Value.Y == -7, "上行解析分发");
        }

        [Test]
        public static void Downstream_Broadcast_And_Single()
        {
            var (client, server) = Setup();
            var cOwner = new ModId("com.test.client");
            var sOwner = new ModId("com.test.server");
            client.Register<TestMsg>(cOwner, new TestMsgCodec());
            server.Register<TestMsg>(sOwner, new TestMsgCodec());

            TestMsg? received = null;
            client.Handle<TestMsg>(cOwner, m => { received = m; return null; });

            byte[]? sent = null;
            server.SendToAll = d => { sent = d; return d.Length; };
            server.Send(new TestMsg { X = 1, Y = 2 });
            client.Receive(sent!);
            Assert.True(received.HasValue && received.Value.X == 1 && received.Value.Y == 2, "下行广播");

            int target = 0;
            server.SendToOne = (id, d) => { target = id; sent = d; return d.Length; };
            server.Send(7, new TestMsg { X = 5, Y = 6 });
            Assert.Equal(7, target, "单发目标客户端 ID");
            client.Receive(sent!);
            Assert.True(received.HasValue && received.Value.X == 5 && received.Value.Y == 6, "下行单发");
        }
    }
}
