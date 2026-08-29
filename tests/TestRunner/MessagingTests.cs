using Game.Messaging;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;

namespace TestRunner
{
    /// <summary>消息总线 v2 测试（§13/§14：二进制、归属、退订、熔断、配额、Trace）。</summary>
    public static class MessagingTests
    {
        private static readonly ModId Owner = new("com.game.weapon");
        private static readonly ModId Other = new("com.game.quest");
        private static readonly MessageId Fired = new(Owner, "fired");

        private static PayloadBuffer EncodeInt(int v)
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, v);
            return w.ToBuffer();
        }

        [Test]
        public static void PublishSubscribe_Binary_RoundTrip()
        {
            var bus = new MessageBus();
            int received = 0;
            ModId sender = default;
            bus.Subscribe(Other, Fired, (in MessageEnvelope env, PayloadReader reader) =>
            {
                sender = env.Sender;
                reader.TryReadInt32(1, out received);
            });

            var buf = EncodeInt(42);
            bus.Publish(Owner, Fired, 1, in buf);
            Assert.Equal(42, received);
            Assert.Equal(Owner, sender); // Sender 由 Bus 填充（§14.1）
        }

        [Test]
        public static void Publish_Ownership_Enforced()
        {
            // 谁定义谁发布（Rule 11）：非 owner 发布直接拒绝，防止伪造事件流
            var bus = new MessageBus();
            var buf = EncodeInt(1);
            Assert.Throws<MessageException>(() => bus.Publish(Other, Fired, 1, in buf));
        }

        [Test]
        public static void Publish_Sequence_PerSender()
        {
            var bus = new MessageBus();
            long seq1 = 0, seq2 = 0;
            bus.Subscribe(Other, Fired, (in MessageEnvelope env, PayloadReader _) =>
            {
                if (seq1 == 0) seq1 = env.Sequence;
                else seq2 = env.Sequence;
            });
            var buf = EncodeInt(1);
            bus.Publish(Owner, Fired, 1, in buf);
            bus.Publish(Owner, Fired, 1, in buf);
            Assert.Equal(1L, seq1);
            Assert.Equal(2L, seq2); // 同一 Sender 严格保序（§14.8）
        }

        [Test]
        public static void UnsubscribeAll_RemovesEverything()
        {
            var bus = new MessageBus();
            bus.Subscribe(Other, Fired, (in MessageEnvelope _, PayloadReader _) => { });
            bus.Subscribe(Owner, Fired, (in MessageEnvelope _, PayloadReader _) => { });
            Assert.Equal(2, bus.SubscriberCount(Fired));

            bus.UnsubscribeAll(Other);
            Assert.Equal(1, bus.SubscriberCount(Fired));
            bus.UnsubscribeAll(Owner);
            Assert.Equal(0, bus.SubscriberCount(Fired));
        }

        [Test]
        public static void ExceptionIsolation_And_CircuitBreak()
        {
            var quotas = new MessageQuotas { MaxErrorsBeforeCircuitBreak = 2 };
            var bus = new MessageBus(quotas);
            int errors = 0;
            bus.OnError += _ => errors++;

            int goodCalls = 0;
            bus.Subscribe(Other, Fired, (in MessageEnvelope _, PayloadReader _) => throw new System.Exception("boom"));
            bus.Subscribe(Owner, Fired, (in MessageEnvelope _, PayloadReader _) => goodCalls++);

            var buf = EncodeInt(1);
            for (var i = 0; i < 5; i++) bus.Publish(Owner, Fired, 1, in buf);

            Assert.Equal(5, goodCalls);       // 坏订阅者不中断其他订阅者（§14.8）
            Assert.Equal(3, errors);          // 熔断阈值 2 → 第 3 次后熔断，共报 3 次错
        }

        [Test]
        public static void Quota_PublishPerFrame()
        {
            var bus = new MessageBus(new MessageQuotas { MaxPublishPerFrame = 2 });
            int calls = 0;
            bus.Subscribe(Other, Fired, (in MessageEnvelope _, PayloadReader _) => calls++);

            var buf = EncodeInt(1);
            for (var i = 0; i < 5; i++) bus.Publish(Owner, Fired, 1, in buf);
            Assert.Equal(2, calls); // 超限丢弃（§14.8 防消息风暴）

            bus.BeginFrame(); // 下一帧重置
            bus.Publish(Owner, Fired, 1, in buf);
            Assert.Equal(3, calls);
        }

        [Test]
        public static void Quota_MaxPayloadSize()
        {
            var bus = new MessageBus(new MessageQuotas { MaxPayloadSize = 8 });
            int calls = 0;
            bus.Subscribe(Other, Fired, (in MessageEnvelope _, PayloadReader _) => calls++);
            var w = new PayloadWriter();
            w.WriteString(1, new string('x', 100));
            var buf = w.ToBuffer();
            bus.Publish(Owner, Fired, 1, in buf);
            Assert.Equal(0, calls); // 超大载荷丢弃
        }

        [Test]
        public static void MutationDuringDispatch_Deferred()
        {
            // Dispatch 中 Subscribe/UnsubscribeAll 进入待处理队列，派发结束统一生效（§13.6 规则 3）
            var bus = new MessageBus();
            int late = 0;
            bus.Subscribe(Other, Fired, (in MessageEnvelope _, PayloadReader _) =>
            {
                bus.Subscribe(Owner, Fired, (in MessageEnvelope _2, PayloadReader _3) => late++);
                bus.UnsubscribeAll(Other);
            });
            var buf = EncodeInt(1);
            bus.Publish(Owner, Fired, 1, in buf); // 派发中修改订阅表
            Assert.Equal(1, bus.SubscriberCount(Fired)); // Other 已退订，新订阅生效
            bus.Publish(Owner, Fired, 1, in buf);
            Assert.Equal(1, late);
        }

        [Test]
        public static void Trace_And_Stats()
        {
            var bus = new MessageBus();
            bus.Subscribe(Other, Fired, (in MessageEnvelope _, PayloadReader _) => { });
            var buf = EncodeInt(1);
            bus.Publish(Owner, Fired, 1, in buf);

            var trace = bus.Trace();
            Assert.Equal(1, trace.Count);
            Assert.Equal(Fired, trace[0].Id);
            Assert.Equal(1, trace[0].SubscriberCount);
            Assert.Equal(1L, bus.Stats[Fired].TotalPublished); // §14.9 可观测性
        }

        [Test]
        public static void ZeroSubscribers_NotAnError()
        {
            // Event 是广播事实，0 个订阅者不是错误（§14.10 规则 1）
            var bus = new MessageBus();
            var buf = EncodeInt(1);
            bus.Publish(Owner, Fired, 1, in buf);
            Assert.Equal(1, bus.Trace().Count);
        }
    }
}
