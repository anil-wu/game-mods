using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// Relay 传输提供者（§11.11，INetworkTransport 的 Relay 路径实现；与 Loopback/Mirror 同抽象层级，Rule 15）。
    ///
    /// 拓扑：星型——Host 经云端 Relay 暴露公网（无需公网 IP/端口映射）；远端客户端 BindClient 后经 Relay 与 Host 转发。
    /// 本传输只看 RelayWire 信封（SessionId/ConnectionId/加密 Payload），不理解任何 Mod 协议（§11.11 Blind Relay 客户端侧）。
    ///
    /// 模式：
    ///   - Host 角色（Config.IsHost=true）：StartServer() → BindHost（等 BindAck，Server 就绪）；
    ///     StartClient() → **内存回环**（§11.3 硬规则：Host 本地客户端走完整协议管线，但不过公网——
    ///     connId=1 预留给本地客户端，Relay 分配远端客户端从 2 起，见 RelayWire 契约）。
    ///   - Client 角色（IsHost=false）：StartClient() → BindClient → BindAck 报头携带自己的 connId → ClientReady。
    ///
    /// 生产 = 后台泵线程（UdpClient.ReceiveAsync 循环）；测试 = Config.BackgroundPump=false + PumpOnce() 同步驱动
    /// （处理当前全部可达 datagram，确定性无 flake）。跨线程 marshal 留给 Unity 引导接入期（§6.1）。
    /// </summary>
    public sealed class RelayClientTransport : INetworkTransport
    {
        /// <summary>Host 本地客户端固定 ConnId（§11.3 回环；与 RelayWire.LocalClientConnId 一致）。</summary>
        public const int LocalConnectionId = RelayWire.LocalClientConnId;

        public sealed class Config
        {
            /// <summary>relay_server 地址（IP 或主机名）。</summary>
            public string RelayHost = "127.0.0.1";

            /// <summary>relay_server UDP 数据面端口。</summary>
            public int RelayPort = 7777;

            /// <summary>会话号（/allocate 或 /resolve 返回的 roomId）。</summary>
            public ulong RoomId;

            /// <summary>绑定凭据（/allocate 或 /resolve 签发，TokenCodec 16B HMAC）。</summary>
            public byte[] Token = Array.Empty<byte>();

            /// <summary>token 签发时的 expiry（Bind 载荷必需：与 token 内签名一致才能过节点 HMAC 校验）。</summary>
            public long ExpiryMs;

            /// <summary>true → Host 角色（BindHost + 本地客户端回环）；false → Client 角色（BindClient）。</summary>
            public bool IsHost;

            /// <summary>心跳间隔（生产后台泵维护；测试 PumpOnce 模式不启用）。</summary>
            public TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

            /// <summary>生产 = 后台泵线程；测试 = false，用 PumpOnce() 同步驱动（§6.1 线程模型）。</summary>
            public bool BackgroundPump = true;
        }

        private readonly Config _config;
        private readonly List<int> _serverConnections = new();
        private UdpClient? _udp;
        private IPEndPoint? _relayEp;
        private Task? _pump;
        private CancellationTokenSource? _cts;
        private bool _serverPending;
        private bool _clientPending;
        private bool _serverBound;
        private bool _clientBound;
        private bool _bindFailed;
        private string? _lastError;
        private ushort _myConnId = RelayWire.HostConnId;
        private DateTime _lastHeartbeat = DateTime.UtcNow;

        public RelayClientTransport(Config config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(_config.RelayHost))
                throw new ArgumentException("RelayHost 不能为空", nameof(config));
            if (_config.Token is null || _config.Token.Length != RelayWire.TokenSize)
                throw new ArgumentException($"token 必须为 {RelayWire.TokenSize} 字节（relay_server TokenCodec 签发）", nameof(config));
            if (_config.IsHost)
                _serverConnections.Add(LocalConnectionId); // Host 本地客户端恒在（§11.3 回环）
        }

        public event Action<byte[]>? ReceivedOnClient;
        public event Action<int, byte[]>? ReceivedOnServer;

        /// <summary>客户端传输就绪（Relay：BindAck 后触发；Host：本地客户端回环就绪即触发）→ 运行时发起握手（§11.6）。</summary>
        public event Action? ClientReady;

        public IReadOnlyCollection<int> ServerConnections => _serverConnections;

        // ---- 诊断（测试/引导可观测性） ----

        public bool IsServerBound => _serverBound;
        public bool IsClientBound => _clientBound;
        public bool IsBindFailed => _bindFailed;
        public string? LastError => _lastError;

        /// <summary>本地（客户端角色）ConnId：BindAck 报头分配值；Host 本地客户端恒为 LocalConnectionId。</summary>
        public int LocalConnId => _myConnId;

        /// <summary>生产模式后台泵是否在跑。</summary>
        public bool IsPumping => _pump is not null;

        // ---- 生命周期 ----

        /// <summary>Host 侧：BindHost → 等 BindAck（异步：后台泵/测试 PumpOnce 处理回包）。</summary>
        public void StartServer()
        {
            if (!_config.IsHost)
                throw new NetworkException("RelayClientTransport: 非 Host 模式不能 StartServer（BindHost 只由房主发起）");
            if (_serverBound || _serverPending) return;
            EnsureSocket();
            var payload = RelayWire.EncodeBindRequest(
                _config.RoomId, RelayWire.RoleHost, _config.ExpiryMs, _config.Token);
            SendPacket(RelayWire.Type.BindHost, RelayWire.HostConnId, payload);
            _serverPending = true;
        }

        /// <summary>
        /// 客户端侧：BindClient → 等 BindAck（自己的 connId 在报头里）→ ClientReady。
        /// Host 模式：本地客户端走内存回环（§11.3 硬规则——完整协议管线，但不过公网）。
        /// </summary>
        public void StartClient()
        {
            if (_clientBound) return;
            if (_config.IsHost)
            {
                _clientBound = true;
                _myConnId = LocalConnectionId;
                ClientReady?.Invoke(); // 本地客户端就绪 → 运行时发起握手
                return;
            }
            if (_clientPending) return;
            EnsureSocket();
            var payload = RelayWire.EncodeBindRequest(
                _config.RoomId, RelayWire.RoleClient, _config.ExpiryMs, _config.Token);
            SendPacket(RelayWire.Type.BindClient, RelayWire.HostConnId, payload);
            _clientPending = true;
        }

        public void Stop()
        {
            try
            {
                // 退房通知（§6.1：Stop 发 Leave；Host 用 HostConnId，Client 用自己的 connId）
                if (_udp is not null && (_serverBound || _clientBound || _serverPending || _clientPending))
                    SendPacket(RelayWire.Type.Leave,
                        _config.IsHost ? RelayWire.HostConnId : _myConnId, Array.Empty<byte>());
            }
            catch (Exception) { /* 退房尽力而为 */ }

            _cts?.Cancel();
            _pump = null;
            try { _udp?.Dispose(); } catch (Exception) { }
            _udp = null;
            _relayEp = null;

            _serverBound = false;
            _clientBound = false;
            _serverPending = false;
            _clientPending = false;
            _bindFailed = false;
            _lastError = null;
            _serverConnections.Clear();
            if (_config.IsHost) _serverConnections.Add(LocalConnectionId); // 支持重启
        }

        // ---- 发送（全部走 RelayWire 信封；payload = SecurityLayer 加密后的信封字节，Relay 只见信封 §11.12） ----

        public void SendFromClient(byte[] frame)
        {
            if (_config.IsHost)
            {
                // Host 本地客户端 → 服务器上下文：内存回环（§11.3 硬规则）
                if (!_clientBound) throw new NetworkException("Relay: 本地客户端未就绪");
                ReceivedOnServer?.Invoke(LocalConnectionId, frame);
                return;
            }
            if (!_clientBound) throw new NetworkException("Relay: 客户端未绑定（BindAck 未到），禁止发送");
            SendPacket(RelayWire.Type.DataRaw, _myConnId, frame);
        }

        public void SendFromServerTo(int connectionId, byte[] frame)
        {
            if (_config.IsHost && connectionId == LocalConnectionId)
            {
                // 服务器上下文 → 本地客户端：内存回环
                ReceivedOnClient?.Invoke(frame);
                return;
            }
            if (!_serverBound) throw new NetworkException("Relay: Host 未绑定（BindAck 未到），禁止发送");
            if (connectionId < 0 || connectionId > ushort.MaxValue)
                throw new NetworkException($"Relay: 非法 connId {connectionId}");
            SendPacket(RelayWire.Type.DataRaw, (ushort)connectionId, frame);
        }

        public void SendFromServerAll(byte[] frame)
        {
            // 快照连接表逐连接发送（回环 + 远端统一走 SendFromServerTo 的定向分支）
            foreach (var connId in new List<int>(_serverConnections))
                SendFromServerTo(connId, frame);
        }

        // ---- 接收 ----

        /// <summary>
        /// 同步泵一次：处理当前全部可达 datagram（测试用，确定性无 flake——§6.1 线程模型）。
        /// 返回本次处理的报文数。
        /// </summary>
        public int PumpOnce()
        {
            var count = 0;
            while (_udp is { } udp && udp.Available > 0)
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = udp.Receive(ref remote); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                HandleDatagram(data);
                count++;
            }
            return count;
        }

        private void HandleDatagram(byte[] data)
        {
            if (!RelayWire.Packet.TryDecode(data, out var pkt)) return;

            switch (pkt.Type)
            {
                case RelayWire.Type.BindAck:
                    if (pkt.ConnId == RelayWire.HostConnId && _serverPending)
                    {
                        _serverPending = false;
                        _serverBound = true;
                    }
                    else if (pkt.ConnId >= RelayWire.FirstRemoteConnId && _clientPending)
                    {
                        _clientPending = false;
                        _clientBound = true;
                        _myConnId = pkt.ConnId;
                        ClientReady?.Invoke(); // → 运行时发起版本协商握手（§11.6）
                    }
                    break;

                case RelayWire.Type.BindFail:
                    _bindFailed = true;
                    _serverPending = false;
                    _clientPending = false;
                    _lastError = "relay BindFail（token 无效/过期，或房间无主）";
                    break;

                case RelayWire.Type.PeerJoined:
                    if (!_serverBound) break; // 只有 Host 关心远端客户端入房
                    AddConnection(pkt.ConnId);
                    break;

                case RelayWire.Type.PeerLeft:
                    RemoveConnection(pkt.ConnId);
                    break;

                case RelayWire.Type.DataRaw:
                case RelayWire.Type.DataKcp: // 原型期 KCP 未启用，按 raw 转发（信封不变）
                    if (_serverBound)
                        ReceivedOnServer?.Invoke(pkt.ConnId, pkt.Payload); // 远端客户端自带 connId（§6.1）
                    else
                        ReceivedOnClient?.Invoke(pkt.Payload);
                    break;

                case RelayWire.Type.Ping:
                    SendPacket(RelayWire.Type.Pong, RelayWire.HostConnId, Array.Empty<byte>());
                    break;

                case RelayWire.Type.Pong:
                case RelayWire.Type.Leave:
                    break; // 心跳回复 / 退房（节点消费，客户端不处理）
            }
        }

        // ---- 内部 ----

        private void EnsureSocket()
        {
            if (_udp is not null) return;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            _relayEp = ResolveRelayEndpoint();
            if (_config.BackgroundPump) StartPump();
        }

        private IPEndPoint ResolveRelayEndpoint()
        {
            if (IPAddress.TryParse(_config.RelayHost, out var ip))
                return new IPEndPoint(ip, _config.RelayPort);
            var addrs = Dns.GetHostAddresses(_config.RelayHost);
            if (addrs is null || addrs.Length == 0)
                throw new NetworkException($"Relay 主机解析失败: {_config.RelayHost}");
            return new IPEndPoint(addrs[0], _config.RelayPort);
        }

        private void StartPump()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _pump = Task.Run(() => PumpLoop(token));
        }

        private void PumpLoop(CancellationToken token)
        {
            var udp = _udp;
            while (!token.IsCancellationRequested && udp is not null)
            {
                try
                {
                    // 心跳保活（§6.1）：Ping/Pong 是节点闲置清扫的活性依据（§6.4）
                    if ((_serverBound || _clientBound) && DateTime.UtcNow - _lastHeartbeat >= _config.HeartbeatInterval)
                    {
                        SendPacket(RelayWire.Type.Ping, RelayWire.HostConnId, Array.Empty<byte>());
                        _lastHeartbeat = DateTime.UtcNow;
                    }
                    // netstandard2.1：无参数 ReceiveAsync 退出方式 = 释放 socket 抛 ObjectDisposedException
                    var result = udp.ReceiveAsync().GetAwaiter().GetResult();
                    HandleDatagram(result.Buffer);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (InvalidOperationException) { break; }
                catch (AggregateException) { break; }
            }
        }

        private void SendPacket(RelayWire.Type type, ushort connId, byte[] payload)
        {
            if (_udp is null || _relayEp is null)
                throw new NetworkException("Relay 未启动（先调用 StartServer/StartClient）");
            var data = new RelayWire.Packet(type, connId, payload).Encode();
            try { _udp.Send(data, data.Length, _relayEp); }
            catch (SocketException) { /* 尽力而为（生产由上层超时/重试兜底） */ }
        }

        private void AddConnection(int connId)
        {
            if (!_serverConnections.Contains(connId)) _serverConnections.Add(connId);
        }

        private void RemoveConnection(int connId)
        {
            _serverConnections.Remove(connId);
        }
    }
}
