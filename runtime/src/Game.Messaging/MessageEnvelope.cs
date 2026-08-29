using Game.Mod.Contract;

namespace Game.Messaging
{
    /// <summary>
    /// 消息信封（§14.1）：Core 定义，所有 Mod 通用；Sender 由 Bus 填充，Mod 不可伪造。
    /// Payload 属于 Mod，Envelope 对载荷透明——Bus 只按 MessageId 路由。
    /// </summary>
    public readonly struct MessageEnvelope
    {
        public readonly MessageId Id;
        public readonly ModId Sender;
        public readonly int Version;
        public readonly long Sequence;
        public readonly long Timestamp;
        public readonly long CorrelationId;

        public MessageEnvelope(MessageId id, ModId sender, int version, long sequence, long timestamp, long correlationId = 0)
        {
            Id = id;
            Sender = sender;
            Version = version;
            Sequence = sequence;
            Timestamp = timestamp;
            CorrelationId = correlationId;
        }

        public override string ToString() => $"{Id} #{Sequence} from {Sender}";
    }

    /// <summary>消息处理器：接收信封 + 载荷读取器（懒解码，§14.12.2）。</summary>
    public delegate void MessageHandler(in MessageEnvelope envelope, Game.Mod.Contract.Wire.PayloadReader reader);
}
