namespace Game.Mod.Runtime
{
    /// <summary>
    /// Mod 网络预算（§11.13）：每 Mod 可配置；超预算则 Throttle——
    /// 防止恶意/写错的 Mod（如 Update()→Send() 造成的 60×N 网络洪水）拖垮服务器。
    /// mod.json 的 networkBudget 声明式读取列为后续（Core Loader → Network.Mod），
    /// 本期提供 API + 默认值即可实现 §11.13 语义（未配置的 Mod 按默认值执行）。
    /// </summary>
    public sealed class NetworkBudget
    {
        /// <summary>Mod 级单消息硬顶（协议 MaxSize 之外的 Mod 自设上限，字节）。</summary>
        public int MaxMessageSize = 65536;

        /// <summary>每秒消息数（该 Mod 全部连接合计）。</summary>
        public int MaxPacketRate = 100;

        /// <summary>每秒字节数（逻辑字节，该 Mod 合计）。</summary>
        public int MaxBandwidth = 65536;

        /// <summary>单连接每秒字节数（Server 收包防御）。</summary>
        public int MaxConnectionBandwidth = 16384;

        /// <summary>窗口内违规 ≥3 后挂起窗口（毫秒），期间该 Mod 一切发送直接 drop 计数（§5.4）。</summary>
        public int ThrottleSuspendMs = 100;
    }
}
