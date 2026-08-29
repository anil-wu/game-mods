using System;
using Game.ModLoader;

namespace Com.Game.Protocol
{
    /// <summary>
    /// 协议核心 Mod（com.game.protocol）入口：纯传输层，无游戏逻辑。
    /// 分为客户端部分（ProtocolClient）与服务端部分（ProtocolServer），各自维护独立的解析器注册表。
    /// 实现 IProtocolHost，供框架网络层（Mirror）接线。
    /// </summary>
    public sealed class ProtocolEntry : IMod, IProtocolHost
    {
        public static ProtocolClient Client { get; private set; } = null!;
        public static ProtocolServer Server { get; private set; } = null!;

        public void OnLoad(IModContext context)
        {
            Client = new ProtocolClient(context.Messages);
            Server = new ProtocolServer(context.Messages);
            context.Log.Info($"协议核心 Mod '{context.ModId}' v{context.Version} 已加载");
        }

        public void BindClient(Func<byte[], int> sendToServer)
            => Client.SendToServer = sendToServer;

        public void BindServer(Func<byte[], int> sendToAll, Func<int, byte[], int> sendToOne)
        {
            Server.SendToAll = sendToAll;
            Server.SendToOne = sendToOne;
        }

        public void OnServerBinary(byte[] data)
            => Server.Receive(data);

        public void OnClientBinary(byte[] data)
            => Client.Receive(data);
    }
}
