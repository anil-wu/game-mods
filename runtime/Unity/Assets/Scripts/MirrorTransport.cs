using System;
using System.Collections.Generic;
using Game.Mod.Runtime;
using kcp2k;
using Mirror;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>Mirror 自定义消息：承载网络帧二进制。</summary>
    public struct ModProtocolMsg : NetworkMessage
    {
        public byte[] Payload;
    }

    /// <summary>
    /// Mirror 传输提供者（§11.2 Provider 层）：INetworkTransport 的 Mirror 实现。
    /// 只看到二进制帧，不理解任何协议（与 Relay 盲转发同一抽象层级）；
    /// Direct Provider → Mirror → Transport（KCP / TCP / WebSocket），可替换（§11.2）。
    /// </summary>
    public sealed class MirrorTransport : MonoBehaviour, INetworkTransport
    {
        private bool _serverUp;
        private bool _clientUp;
        private readonly List<int> _connections = new();

        public event Action<byte[]>? ReceivedOnClient;
        public event Action<int, byte[]>? ReceivedOnServer;

        /// <summary>客户端连接建立后触发（§11.6 握手触发点：KCP 连接就绪才能发出 hello）。</summary>
        public event Action? ClientReady;

        public IReadOnlyCollection<int> ServerConnections => _connections;

        private void Awake()
        {
            // 无 NetworkManager 场景：自行装配 KCP 传输层（Provider 层实现细节，§11.2）
            if (Transport.active == null)
                Transport.active = gameObject.AddComponent<KcpTransport>();

            NetworkServer.RegisterHandler<ModProtocolMsg>(OnServerMsg, false);
            NetworkClient.RegisterHandler<ModProtocolMsg>(OnClientMsg, false);
        }

        private void OnDestroy()
        {
            // Play 模式退出时 GameObject 销毁——同时关停网络，避免下次 Play 端口占用
            Stop();
            NetworkServer.UnregisterHandler<ModProtocolMsg>();
            NetworkClient.UnregisterHandler<ModProtocolMsg>();
        }

        public void StartServer()
        {
            if (_serverUp) return;
            NetworkServer.Listen(16);
            _serverUp = true;
        }

        public void StartClient()
        {
            if (_clientUp) return;
            NetworkClient.OnConnectedEvent += OnClientConnected;
            NetworkClient.Connect("localhost"); // Host 本地客户端走真实网络栈回环（§11.3 不短路）
            _clientUp = true;
        }

        private void OnClientConnected()
        {
            // KCP 连接就绪 → 传输层可用（SendFromClient 不再静默丢弃）→ 通知运行时发起握手
            ClientReady?.Invoke();
        }

        public void Stop()
        {
            if (_clientUp) { NetworkClient.Shutdown(); _clientUp = false; }
            NetworkClient.OnConnectedEvent -= OnClientConnected;
            if (_serverUp) { NetworkServer.Shutdown(); _serverUp = false; }
            _connections.Clear();
        }

        public void SendFromClient(byte[] frame)
        {
            // KCP 握手完成前（Connecting）静默丢弃——输入状态每帧重发，连接建立即恢复；
            // NetworkClient.active 在 Connecting 期间即为 true，必须等 Connected
            if (!NetworkClient.isConnected) return;
            NetworkClient.Send(new ModProtocolMsg { Payload = frame });
        }

        public void SendFromServerTo(int connectionId, byte[] frame)
        {
            if (NetworkServer.connections.TryGetValue(connectionId, out var conn))
                conn.Send(new ModProtocolMsg { Payload = frame });
        }

        public void SendFromServerAll(byte[] frame)
        {
            if (_serverUp)
                NetworkServer.SendToAll(new ModProtocolMsg { Payload = frame });
        }

        private void OnServerMsg(NetworkConnectionToClient conn, ModProtocolMsg msg)
        {
            if (!_connections.Contains(conn.connectionId))
                _connections.Add(conn.connectionId);
            ReceivedOnServer?.Invoke(conn.connectionId, msg.Payload);
        }

        private void OnClientMsg(ModProtocolMsg msg)
            => ReceivedOnClient?.Invoke(msg.Payload);
    }
}
