using System;
using System.Collections.Generic;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// 双端桥传输（测试辅助）：把两套 (runtime, transport) 互连成真实的 Client↔Server 拓扑——
    /// 服务器侧（Role=Service）只 StartServer，客户端侧（Role=Client）只 StartClient。
    /// 用于协议版本协商（§11.6）等需要"客户端声明 ≠ 服务器注册"的跨运行时测试；
    /// 与 Loopback（单运行时 Host 回环）互补。
    /// 支持一服务端挂多个客户端（兴趣管理多连接过滤测试）。
    /// </summary>
    public sealed class BridgeTransport : INetworkTransport
    {
        private sealed class ClientLink
        {
            public BridgeTransport Transport = null!;
            public int ConnId;
        }

        private readonly List<ClientLink> _links = new(); // 服务器侧：已接入的客户端
        private int _nextConnId = 1;
        private bool _serverUp;
        private bool _clientUp;
        private BridgeTransport? _server; // 客户端侧：我的服务端
        private int _myConnId;

        /// <summary>最近发出的帧（测试断言帧头版本等）。</summary>
        public readonly List<byte[]> SentFrames = new();

        public event Action<byte[]>? ReceivedOnClient;
        public event Action<int, byte[]>? ReceivedOnServer;
        public event Action? ClientReady;

        public IReadOnlyCollection<int> ServerConnections
        {
            get
            {
                var list = new List<int>(_links.Count);
                foreach (var link in _links) list.Add(link.ConnId);
                return list;
            }
        }

        /// <summary>服务器侧接入一个客户端侧（分配连接 id）。</summary>
        public void AttachClient(BridgeTransport client)
        {
            if (client._server is not null) throw new InvalidOperationException("客户端已接入服务端");
            var connId = _nextConnId++;
            _links.Add(new ClientLink { Transport = client, ConnId = connId });
            client._server = this;
            client._myConnId = connId;
        }

        public void StartServer()
        {
            _serverUp = true;
        }

        public void StartClient()
        {
            if (_server is null) throw new NetworkException("BridgeTransport: 未 AttachClient 到服务端");
            _clientUp = true;
            ClientReady?.Invoke(); // 客户端传输就绪 → 运行时发起握手（§11.6）
        }

        public void Stop()
        {
            _serverUp = false;
            _clientUp = false;
            _links.Clear();
            SentFrames.Clear();
        }

        public void SendFromClient(byte[] frame)
        {
            if (!_clientUp) throw new NetworkException("BridgeTransport: Client 未启动");
            _server!.ReceivedOnServer?.Invoke(_myConnId, frame);
        }

        public void SendFromServerTo(int connectionId, byte[] frame)
        {
            if (!_serverUp) throw new NetworkException("BridgeTransport: Server 未启动");
            foreach (var link in _links)
            {
                if (link.ConnId != connectionId) continue;
                SentFrames.Add(frame);
                link.Transport.ReceivedOnClient?.Invoke(frame);
                return;
            }
        }

        public void SendFromServerAll(byte[] frame)
        {
            if (!_serverUp) throw new NetworkException("BridgeTransport: Server 未启动");
            foreach (var link in _links)
            {
                SentFrames.Add(frame);
                link.Transport.ReceivedOnClient?.Invoke(frame);
            }
        }
    }

    /// <summary>双端拓扑辅助：服务器 runtime（Service）+ N 个客户端 runtime（Client）。
    /// 客户端 AddClient() 后保持未启动——测试可先注册协议再 StartClients()（协商需要协议在 hello 之前注册）。</summary>
    public sealed class BridgeFixture : IDisposable
    {
        public Com.Game.Network.NetworkRuntimeImpl Server = null!;
        public BridgeTransport ServerTransport = null!;
        public readonly List<Com.Game.Network.NetworkRuntimeImpl> Clients = new();
        public readonly List<BridgeTransport> ClientTransports = new();

        /// <summary>建服务器并启动（Role=Service）。</summary>
        public static BridgeFixture CreateServer()
        {
            var f = new BridgeFixture();
            f.Server = new Com.Game.Network.NetworkRuntimeImpl();
            f.ServerTransport = new BridgeTransport();
            f.Server.AttachTransport(f.ServerTransport);
            f.Server.Start(new NetworkConfig { Role = NetworkRole.Service, Path = NetworkPath.Direct });
            return f;
        }

        /// <summary>追加一个客户端（connId 递增），返回未启动的 runtime——注册协议后调 StartClients() 完成握手。</summary>
        public Com.Game.Network.NetworkRuntimeImpl AddClient()
        {
            var client = new Com.Game.Network.NetworkRuntimeImpl();
            var transport = new BridgeTransport();
            ServerTransport.AttachClient(transport);
            client.AttachTransport(transport);
            Clients.Add(client);
            ClientTransports.Add(transport);
            return client;
        }

        /// <summary>启动全部客户端（触发 ClientReady → 握手）。</summary>
        public void StartClients()
        {
            foreach (var c in Clients)
                c.Start(new NetworkConfig { Role = NetworkRole.Client, Path = NetworkPath.Direct });
        }

        public void Dispose()
        {
            foreach (var c in Clients) c.Stop();
            Server.Stop();
        }
    }
}
