using Game.ModLoader;

namespace Com.Game.Protocol
{
    /// <summary>
    /// 协议核心 Mod（com.game.protocol）入口：纯传输层，无游戏逻辑。
    /// 分为客户端部分（ProtocolClient）与服务端部分（ProtocolServer），
    /// 各自维护独立的解析器注册表。
    /// </summary>
    public sealed class ProtocolEntry : IMod
    {
        public static ProtocolClient Client { get; private set; } = null!;
        public static ProtocolServer Server { get; private set; } = null!;

        public void OnLoad(IModContext context)
        {
            Client = new ProtocolClient(context.Messages);
            Server = new ProtocolServer(context.Messages);
            context.Log.Info($"协议核心 Mod '{context.ModId}' v{context.Version} 已加载");
        }
    }
}
