using Game.Mod.Contract.Wire;

namespace Game.Mod.Contract
{
    /// <summary>协议方向（§11.4）。</summary>
    public enum NetworkDirection
    {
        ClientToServer,
        ServerToClient,
    }

    /// <summary>投递语义。</summary>
    public enum Delivery
    {
        Reliable,
        Unreliable,
        Sequenced,
    }

    /// <summary>
    /// 频率档（§11.4 硬约束）：High 频协议禁止装箱路径（应由生成 Codec 直写 Buffer）；
    /// 每秒超过约 5 次的状态变化必须走 ECS Replication，不允许塞进普通协议消息。
    /// </summary>
    public enum Frequency
    {
        Low,
        High,
    }

    /// <summary>
    /// Manifest 中的协议声明（§15.4）：供握手协商与构建期查重；
    /// 真正的 Encode/Decode/Handler 由 Mod 在 Register 时注册（§11.6）。
    /// </summary>
    public sealed class ProtocolDecl
    {
        /// <summary>本地协议路径（Mod 命名空间内唯一），全局 ID = xxHash32("{ModId}:{Path}")。</summary>
        public string Path { get; set; } = "";

        public ushort Version { get; set; } = 1;

        public NetworkDirection Direction { get; set; }

        public Delivery Delivery { get; set; } = Delivery.Reliable;

        public Frequency Frequency { get; set; } = Frequency.Low;

        /// <summary>计算全局稳定协议 ID（Rule 16）。</summary>
        public ProtocolId GlobalId(ModId owner) => ProtocolId.Of(owner, Path);

        public override string ToString() => $"{Path} v{Version} {Direction}";
    }
}
