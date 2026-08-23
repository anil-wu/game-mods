using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RelayServer.Protocol;

namespace RelayServer.Relay;

/// <summary>
/// UDP 哑转发节点：按房间路由数据报，完全不解析游戏协议。
/// 星型拓扑：客户端只与房主通信，房主按 connId 定向转发到客户端。
/// </summary>
public sealed class UdpRelayNode
{
    private readonly UdpClient _udp;
    private readonly byte[] _secret;
    private readonly RoomTable _rooms;
    private readonly TimeSpan _idleTimeout;
    private readonly ConcurrentDictionary<IPEndPoint, (ulong RoomId, ushort ConnId)> _peers = new();

    public Action<string>? Log { get; set; }

    public UdpRelayNode(int port, byte[] secret, RoomTable rooms, TimeSpan idleTimeout)
    {
        _udp = new UdpClient(port);
        _secret = secret;
        _rooms = rooms;
        _idleTimeout = idleTimeout;
    }

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public async Task RunAsync(CancellationToken ct)
    {
        Log?.Invoke($"[Relay] UDP 节点已启动, 端口 {Port}");

        // 周期清扫闲置房间
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                _rooms.Sweep(_idleTimeout);
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                HandlePacket(result.RemoteEndPoint, result.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
        }
    }

    private void HandlePacket(IPEndPoint remote, byte[] data)
    {
        if (!Packet.TryDecode(data, out var packet)) return;

        switch (packet.Type)
        {
            case MessageType.BindHost: HandleBind(remote, packet, expectHost: true); break;
            case MessageType.BindClient: HandleBind(remote, packet, expectHost: false); break;
            case MessageType.DataKcp:
            case MessageType.DataRaw: HandleData(remote, packet); break;
            case MessageType.Ping:
                Send(remote, new Packet(MessageType.Pong, RelayProtocol.HostConnId, ReadOnlyMemory<byte>.Empty));
                break;
            case MessageType.Pong: break;
        }
    }

    private void HandleBind(IPEndPoint remote, Packet packet, bool expectHost)
    {
        if (!BindRequest.TryParse(packet.Payload.Span, out var req))
        {
            Send(remote, BindFail());
            return;
        }

        var expectRole = expectHost ? TokenCodec.RoleHost : TokenCodec.RoleClient;
        if (req.Role != expectRole || !req.Verify(_secret) || req.Expiry <= DateTimeOffset.UtcNow)
        {
            Send(remote, BindFail());
            return;
        }

        var room = _rooms.GetOrCreate(req.RoomId);

        if (expectHost)
        {
            room.Host = remote;
            room.Touch();
            _peers[remote] = (req.RoomId, RelayProtocol.HostConnId);
            Send(remote, new Packet(MessageType.BindAck, RelayProtocol.HostConnId, ReadOnlyMemory<byte>.Empty));
            Log?.Invoke($"[Relay] 房主加入房间 {req.RoomId} ({remote})");
        }
        else
        {
            var hostEp = room.Host;
            if (hostEp is null)
            {
                Send(remote, BindFail());
                return;
            }

            var connId = room.AssignConnId();
            room.Clients[connId] = remote;
            room.Touch();
            _peers[remote] = (req.RoomId, connId);

            Send(remote, new Packet(MessageType.BindAck, connId, ReadOnlyMemory<byte>.Empty));

            var joined = new byte[2];
            joined[0] = (byte)(connId & 0xFF);
            joined[1] = (byte)(connId >> 8);
            Send(hostEp, new Packet(MessageType.PeerJoined, connId, joined));
            Log?.Invoke($"[Relay] 客户端 {connId} 加入房间 {req.RoomId} ({remote})");
        }
    }

    private void HandleData(IPEndPoint remote, Packet packet)
    {
        if (!_peers.TryGetValue(remote, out var peer)) return;   // 未绑定来源，丢弃
        if (!_rooms.TryGet(peer.RoomId, out var room)) return;
        room.Touch();

        if (peer.ConnId == RelayProtocol.HostConnId)
        {
            // 房主 → 指定客户端
            if (room.Clients.TryGetValue(packet.ConnId, out var clientEp))
                Send(clientEp, packet);
        }
        else
        {
            // 客户端 → 房主（connId 保持客户端自身 id，房主据此区分来源）
            var hostEp = room.Host;
            if (hostEp is not null)
                Send(hostEp, packet);
        }
    }

    private static Packet BindFail() =>
        new(MessageType.BindFail, RelayProtocol.HostConnId, ReadOnlyMemory<byte>.Empty);

    private void Send(IPEndPoint remote, Packet packet)
    {
        var data = packet.Encode();
        try { _udp.Send(data, data.Length, remote); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }
}
