using System;
using System.Collections.Generic;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// 回环传输提供者（§11.3）：Host 的本地客户端也走完整的 Encode → 协议 → Decode 回环管线，
    /// 内存队列替代真实 Transport，保证 Host 玩家与普通 Client 行为完全一致——
    /// 禁止 Local Player ─X─► Server ECS 的短路写法。也用于无头测试。
    /// </summary>
    public sealed class LoopbackTransport : INetworkTransport
    {
        /// <summary>Host 本地连接的固定 ConnectionId。</summary>
        public const int LocalConnectionId = 1;

        private bool _serverUp;
        private bool _clientUp;
        private readonly List<int> _connections = new();

        public event Action<byte[]>? ReceivedOnClient;
        public event Action<int, byte[]>? ReceivedOnServer;

        public IReadOnlyCollection<int> ServerConnections => _connections;

        public void StartServer()
        {
            _serverUp = true;
        }

        public void StartClient()
        {
            if (!_serverUp)
                throw new NetworkException("Loopback: Server 未启动，Client 无法连接");
            _clientUp = true;
            if (!_connections.Contains(LocalConnectionId))
                _connections.Add(LocalConnectionId);
        }

        public void Stop()
        {
            _serverUp = false;
            _clientUp = false;
            _connections.Clear();
        }

        public void SendFromClient(byte[] frame)
        {
            if (!_clientUp) throw new NetworkException("Loopback: Client 未启动");
            ReceivedOnServer?.Invoke(LocalConnectionId, frame);
        }

        public void SendFromServerTo(int connectionId, byte[] frame)
        {
            if (!_serverUp) throw new NetworkException("Loopback: Server 未启动");
            if (!_connections.Contains(connectionId)) return;
            ReceivedOnClient?.Invoke(frame);
        }

        public void SendFromServerAll(byte[] frame)
        {
            if (!_serverUp) throw new NetworkException("Loopback: Server 未启动");
            if (_connections.Count == 0) return;
            ReceivedOnClient?.Invoke(frame);
        }
    }
}
