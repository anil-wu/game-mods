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
    /// <summary>每个 peer 最近活跃时间（收到来自该 peer 的任意报文即刷新），供闲置清扫（§6.4）。</summary>
    private readonly ConcurrentDictionary<IPEndPoint, DateTime> _activity = new();

    public Action<string>? Log { get; set; }

    public UdpRelayNode(int port, byte[] secret, RoomTable rooms, TimeSpan idleTimeout)
    {
        _udp = new UdpClient(port);
        _secret = secret;
        _rooms = rooms;
        _idleTimeout = idleTimeout;
    }

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    /// <summary>关闭节点（测试清理；生产由进程退出释放）。</summary>
    public void Close()
    {
        try { _udp.Close(); }
        catch (Exception) { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log?.Invoke($"[Relay] UDP 节点已启动, 端口 {Port}");

        // 周期清扫闲置房间与闲置 peer（客户端超时未心跳 → 移除并通知 Host PeerLeft，§6.4）
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                _rooms.Sweep(_idleTimeout);
                SweepIdle(_idleTimeout);
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

    /// <summary>
    /// 同步泵一次：处理当前全部可达 datagram（测试用，确定性无 flake——
    /// 测试不依赖后台线程时序，显式驱动 节点 PumpOnce + 传输 PumpOnce 即可）。
    /// 返回本次处理的报文数。
    /// </summary>
    public int PumpOnce()
    {
        var count = 0;
        while (_udp.Available > 0)
        {
            IPEndPoint remote;
            byte[] data;
            try
            {
                remote = new IPEndPoint(IPAddress.Any, 0);
                data = _udp.Receive(ref remote);
            }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            HandlePacket(remote, data);
            count++;
        }
        return count;
    }

    /// <summary>清扫闲置 peer（超过 timeout 未活动）→ 移除并通知 Host PeerLeft（§6.4）。</summary>
    public void SweepIdle(TimeSpan timeout)
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _activity)
        {
            if (now - kv.Value <= timeout) continue;
            if (_peers.TryGetValue(kv.Key, out var peer))
                RemovePeer(kv.Key, peer, notifyHost: true);
            _activity.TryRemove(kv.Key, out _);
        }
    }

    private void HandlePacket(IPEndPoint remote, byte[] data)
    {
        if (!Packet.TryDecode(data, out var packet)) return;
        if (_peers.ContainsKey(remote)) _activity[remote] = DateTime.UtcNow;

        switch (packet.Type)
        {
            case MessageType.BindHost: HandleBind(remote, packet, expectHost: true); break;
            case MessageType.BindClient: HandleBind(remote, packet, expectHost: false); break;
            case MessageType.DataKcp:
            case MessageType.DataRaw: HandleData(remote, packet); break;
            case MessageType.Leave: HandleLeave(remote, packet); break;
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
            if (hostEp is null || room.IsFull)
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

    private void HandleLeave(IPEndPoint remote, Packet packet)
    {
        if (!_peers.TryGetValue(remote, out var peer)) return; // 未绑定/已离开，忽略
        RemovePeer(remote, peer, notifyHost: true);
        Log?.Invoke($"[Relay] peer {peer.ConnId} 离开房间 {peer.RoomId} ({remote})");
    }

    /// <summary>从房间与 peer 表移除；客户端离开时通知 Host PeerLeft（§6.4）。</summary>
    private void RemovePeer(IPEndPoint remote, (ulong RoomId, ushort ConnId) peer, bool notifyHost)
    {
        _peers.TryRemove(remote, out _);
        _activity.TryRemove(remote, out _);
        if (!_rooms.TryGet(peer.RoomId, out var room)) return;

        if (peer.ConnId == RelayProtocol.HostConnId)
        {
            if (room.Host == remote) room.Host = null; // 房主离开 → 房间无主（新 Host 可再 BindHost 接管）
        }
        else
        {
            room.RemoveClient(peer.ConnId);
            if (notifyHost && room.Host is { } host)
            {
                var payload = new byte[2];
                payload[0] = (byte)(peer.ConnId & 0xFF);
                payload[1] = (byte)(peer.ConnId >> 8);
                Send(host, new Packet(MessageType.PeerLeft, peer.ConnId, payload));
            }
        }
        room.Touch();
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
