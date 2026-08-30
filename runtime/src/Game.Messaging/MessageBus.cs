using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;

namespace Game.Messaging
{
    /// <summary>消息通信异常（归属伪造 / 配额违反等协议级错误）。</summary>
    public sealed class MessageException : Exception
    {
        public MessageException(string message) : base(message) { }
    }

    /// <summary>一条 Trace 记录（§14.9）。</summary>
    public readonly struct MessageTraceEntry
    {
        public readonly MessageId Id;
        public readonly ModId Sender;
        public readonly int SubscriberCount;
        public readonly long ElapsedTicks;

        public MessageTraceEntry(MessageId id, ModId sender, int subscriberCount, long elapsedTicks)
        {
            Id = id;
            Sender = sender;
            SubscriberCount = subscriberCount;
            ElapsedTicks = elapsedTicks;
        }
    }

    /// <summary>某条消息的累计统计。</summary>
    public sealed class MessageStats
    {
        public long TotalPublished;
        public long TotalTicks;
        public double AverageMilliseconds =>
            TotalPublished == 0 ? 0 : (double)TotalTicks / TotalPublished / Stopwatch.Frequency * 1000.0;
    }

    /// <summary>
    /// 消息总线 v2（§13/§14）：Mod 间唯一的事件通知通道（Pub/Sub、无返回）。
    /// - 载荷一律二进制（Rule 14）：Publish 一次编码，N 个订阅者共享同一份只读视图（§14.12.1）。
    /// - 谁定义谁发布（Rule 11）：Publish 校验 Sender 与 MessageId 归属，不匹配直接拒绝。
    /// - 订阅归属 ModObject：Unload 时 Core 强制 UnsubscribeAll(modId)（§13.5）。
    /// - 默认同步直派（§14.12.6）；Dispatch 中修改订阅表延迟到派发结束生效（§13.6 规则 3）。
    /// - 需要返回值的"请求"不是消息——走 ModCall（§12.11 / §14.10）。
    /// </summary>
    public sealed class MessageBus
    {
        private sealed class Subscription
        {
            public ModId Subscriber = default;
            public MessageHandler Handler = null!;
            public int ConsecutiveErrors;
        }

        private readonly Dictionary<MessageId, List<Subscription>> _subscribers = new();
        private readonly Dictionary<ModId, long> _sequences = new();
        private readonly Dictionary<ModId, int> _publishedThisFrame = new();
        private readonly Dictionary<MessageId, MessageStats> _stats = new();
        private readonly List<MessageTraceEntry> _trace;
        private int _traceHead;
        private int _traceCount;
        private int _dispatching;
        private readonly List<Action> _pendingMutations = new();

        public MessageQuotas Quotas { get; }

        public Action<string>? Log { get; set; }
        public Action<Exception>? OnError { get; set; }

        public MessageBus(MessageQuotas? quotas = null)
        {
            Quotas = quotas ?? new MessageQuotas();
            _trace = new List<MessageTraceEntry>(Quotas.TraceCapacity);
            for (var i = 0; i < Quotas.TraceCapacity; i++) _trace.Add(default);
        }

        /// <summary>订阅消息（在 IMod.Register 中调用，归属 subscriber 的 ModObject）。</summary>
        public void Subscribe(ModId subscriber, MessageId id, MessageHandler handler)
        {
            if (handler is null) throw new ArgumentNullException(nameof(handler));
            if (_dispatching > 0)
            {
                _pendingMutations.Add(() => Subscribe(subscriber, id, handler));
                return;
            }
            if (!_subscribers.TryGetValue(id, out var list))
            {
                list = new List<Subscription>();
                _subscribers[id] = list;
            }
            if (list.Count >= Quotas.MaxSubscribersPerMessage)
            {
                Log?.Invoke($"[MessageBus] 消息 '{id}' 订阅者数超配额 {Quotas.MaxSubscribersPerMessage}，已拒绝 '{subscriber}'");
                return;
            }
            list.Add(new Subscription { Subscriber = subscriber, Handler = handler });
        }

        /// <summary>强制退订某 Mod 的全部订阅（卸载流程由 Core 执行，§13.5）。</summary>
        public void UnsubscribeAll(ModId subscriber)
        {
            if (_dispatching > 0)
            {
                _pendingMutations.Add(() => UnsubscribeAll(subscriber));
                return;
            }
            var emptyIds = new List<MessageId>();
            foreach (var (id, list) in _subscribers)
            {
                list.RemoveAll(s => s.Subscriber == subscriber);
                if (list.Count == 0) emptyIds.Add(id);
            }
            foreach (var id in emptyIds) _subscribers.Remove(id);
            _publishedThisFrame.Remove(subscriber);
            _sequences.Remove(subscriber);
        }

        /// <summary>
        /// 发布事件（一对多、无返回）。只有 owner Mod 能发布自己命名空间的消息（§14.2）。
        /// 同一 Sender、同一 MessageId 严格保序（Envelope.Sequence）。
        /// </summary>
        public void Publish(ModId sender, MessageId id, int version, in PayloadBuffer payload)
        {
            if (id.Mod != sender)
                throw new MessageException($"Mod '{sender}' 无权发布 '{id}'（谁定义谁发布，Rule 11）");

            if (payload.Length > Quotas.MaxPayloadSize)
            {
                Log?.Invoke($"[MessageBus] '{sender}' 发布 '{id}' 载荷 {payload.Length}B 超配额 {Quotas.MaxPayloadSize}B，已丢弃");
                return;
            }

            _publishedThisFrame.TryGetValue(sender, out var frameCount);
            if (frameCount >= Quotas.MaxPublishPerFrame)
            {
                Log?.Invoke($"[MessageBus] '{sender}' 本帧发布超配额 {Quotas.MaxPublishPerFrame}，'{id}' 已丢弃");
                return;
            }
            _publishedThisFrame[sender] = frameCount + 1;

            _sequences.TryGetValue(sender, out var seq);
            seq++;
            _sequences[sender] = seq;
            var envelope = new MessageEnvelope(id, sender, version, seq, DateTime.UtcNow.Ticks);

            if (!_subscribers.TryGetValue(id, out var list) || list.Count == 0)
            {
                RecordTrace(envelope, 0, 0);
                return;
            }

            var start = Stopwatch.GetTimestamp();
            _dispatching++;
            try
            {
                // 快照派发：订阅者共享同一份只读视图，零拷贝（§14.12.1）
                var snapshot = list.ToArray();
                var reader = new PayloadReader(payload);
                foreach (var sub in snapshot)
                {
                    if (sub.ConsecutiveErrors > Quotas.MaxErrorsBeforeCircuitBreak)
                        continue; // 已熔断
                    try
                    {
                        sub.Handler(in envelope, reader);
                        sub.ConsecutiveErrors = 0;
                    }
                    catch (Exception e)
                    {
                        sub.ConsecutiveErrors++;
                        OnError?.Invoke(new MessageException(
                            $"订阅者 '{sub.Subscriber}' 处理 '{id}' 异常（连续 {sub.ConsecutiveErrors} 次）: {e.Message}"));
                        if (sub.ConsecutiveErrors > Quotas.MaxErrorsBeforeCircuitBreak)
                            Log?.Invoke($"[MessageBus] 订阅者 '{sub.Subscriber}' 对 '{id}' 的订阅已熔断");
                    }
                }
            }
            finally
            {
                _dispatching--;
                if (_dispatching == 0 && _pendingMutations.Count > 0)
                {
                    var ops = _pendingMutations.ToArray();
                    _pendingMutations.Clear();
                    foreach (var op in ops) op();
                }
            }

            var elapsed = Stopwatch.GetTimestamp() - start;
            RecordTrace(envelope, list.Count, elapsed);
            if (!_stats.TryGetValue(id, out var stats))
            {
                stats = new MessageStats();
                _stats[id] = stats;
            }
            stats.TotalPublished++;
            stats.TotalTicks += elapsed;
        }

        /// <summary>帧开始：重置每帧配额计数（由主循环固定相位调用）。</summary>
        public void BeginFrame() => _publishedThisFrame.Clear();

        /// <summary>最近 N 条消息 Trace（§14.9）。</summary>
        public IReadOnlyList<MessageTraceEntry> Trace()
        {
            var result = new List<MessageTraceEntry>(_traceCount);
            for (var i = 0; i < _traceCount; i++)
            {
                var idx = (_traceHead - 1 - i + _trace.Count) % _trace.Count;
                result.Add(_trace[idx]);
            }
            return result;
        }

        public IReadOnlyDictionary<MessageId, MessageStats> Stats => _stats;

        public int SubscriberCount(MessageId id) =>
            _subscribers.TryGetValue(id, out var list) ? list.Count : 0;

        /// <summary>某 Mod 的订阅总数（卸载泄漏报告用）。</summary>
        public int SubscriptionCount(ModId subscriber)
        {
            var n = 0;
            foreach (var list in _subscribers.Values)
                foreach (var sub in list)
                    if (sub.Subscriber == subscriber) n++;
            return n;
        }

        private void RecordTrace(in MessageEnvelope envelope, int subscriberCount, long elapsedTicks)
        {
            _trace[_traceHead] = new MessageTraceEntry(envelope.Id, envelope.Sender, subscriberCount, elapsedTicks);
            _traceHead = (_traceHead + 1) % _trace.Count;
            if (_traceCount < _trace.Count) _traceCount++;
        }
    }
}
