using Game.ModLoader;
using Mirror;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>Mirror 自定义消息：承载协议二进制。</summary>
    public struct ModProtocolMsg : NetworkMessage
    {
        public byte[] Payload;
    }

    /// <summary>
    /// 框架网络层：Mirror 传输，桥接协议 Mod 的二进制桥（IProtocolHost）。
    /// 仅负责二进制字节的收发，不理解任何游戏协议。
    /// </summary>
    public sealed class MirrorNetModule : MonoBehaviour
    {
        private IProtocolHost? _host;

        /// <summary>绑定协议宿主：把协议 Mod 的发送钩子接到 Mirror。</summary>
        public void Bind(IProtocolHost host)
        {
            _host = host;

            // 客户端侧：发送给服务端
            host.BindClient(data =>
            {
                NetworkClient.Send(new ModProtocolMsg { Payload = data });
                return data.Length;
            });

            // 服务端侧：广播 / 单发
            host.BindServer(
                data =>
                {
                    NetworkServer.SendToAll(new ModProtocolMsg { Payload = data });
                    return data.Length;
                },
                (connId, data) =>
                {
                    if (NetworkServer.connections.TryGetValue(connId, out var conn))
                    {
                        conn.Send(new ModProtocolMsg { Payload = data });
                        return data.Length;
                    }
                    return 0;
                });
        }

        private void OnEnable()
        {
            NetworkServer.RegisterHandler<ModProtocolMsg>(OnServerMsg, false);
            NetworkClient.RegisterHandler<ModProtocolMsg>(OnClientMsg, false);
        }

        private void OnServerMsg(NetworkConnectionToClient conn, ModProtocolMsg msg)
            => _host?.OnServerBinary(msg.Payload);

        private void OnClientMsg(ModProtocolMsg msg)
            => _host?.OnClientBinary(msg.Payload);
    }
}
