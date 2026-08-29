namespace Game.Messaging
{
    /// <summary>
    /// 消息配额（§14.8 防消息风暴）。每 Mod 每帧 Publish 上限，超限丢弃并告警；
    /// 高频每帧数据禁止走消息——那是 ECS Replication 的职责（§13.6 规则 5）。
    /// </summary>
    public sealed class MessageQuotas
    {
        /// <summary>每 Mod 每帧 Publish 上限。</summary>
        public int MaxPublishPerFrame { get; set; } = 64;

        /// <summary>单条消息载荷上限（字节）。</summary>
        public int MaxPayloadSize { get; set; } = 1024;

        /// <summary>每条消息的最大订阅者数。</summary>
        public int MaxSubscribersPerMessage { get; set; } = 32;

        /// <summary>同一订阅者连续异常阈值，超过则熔断该订阅（§14.8）。</summary>
        public int MaxErrorsBeforeCircuitBreak { get; set; } = 8;

        /// <summary>Trace 环形缓冲容量（§14.9）。</summary>
        public int TraceCapacity { get; set; } = 128;
    }
}
