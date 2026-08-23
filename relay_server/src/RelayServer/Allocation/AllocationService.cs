using System;
using System.Collections.Concurrent;
using System.Threading;
using RelayServer.Protocol;

namespace RelayServer.Allocation;

public readonly struct AllocationResult
{
    public ulong RoomId { get; }
    public string JoinCode { get; }
    public byte[] Token { get; }
    public DateTimeOffset Expiry { get; }

    public AllocationResult(ulong roomId, string joinCode, byte[] token, DateTimeOffset expiry)
    {
        RoomId = roomId;
        JoinCode = joinCode;
        Token = token;
        Expiry = expiry;
    }
}

/// <summary>
/// 控制面：分配房间、签发 token、解析 JoinCode。
/// v1 单节点：与 Relay 节点共享 secret，映射存内存（多节点需接入共享存储）。
/// </summary>
public sealed class AllocationService
{
    private readonly byte[] _secret;
    private readonly TimeSpan _tokenLifetime;
    private readonly ConcurrentDictionary<string, ulong> _codeToRoom = new();
    private ulong _nextRoomId = 1;

    public AllocationService(byte[] secret, TimeSpan tokenLifetime)
    {
        _secret = secret;
        _tokenLifetime = tokenLifetime;
    }

    /// <summary>分配房间：生成 JoinCode + 签发房主 token。</summary>
    public AllocationResult Allocate()
    {
        var roomId = Interlocked.Increment(ref _nextRoomId);
        var code = JoinCode.Generate();
        while (!_codeToRoom.TryAdd(code, roomId))
            code = JoinCode.Generate();   // 撞码重试（概率极低）

        var expiry = DateTimeOffset.UtcNow + _tokenLifetime;
        var token = TokenCodec.Issue(_secret, roomId, TokenCodec.RoleHost, expiry);
        return new AllocationResult(roomId, code, token, expiry);
    }

    /// <summary>解析 JoinCode：签发客户端 token。</summary>
    public bool TryResolve(string joinCode, out AllocationResult result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(joinCode)) return false;
        if (!_codeToRoom.TryGetValue(joinCode.Trim().ToUpperInvariant(), out var roomId)) return false;

        var expiry = DateTimeOffset.UtcNow + _tokenLifetime;
        var token = TokenCodec.Issue(_secret, roomId, TokenCodec.RoleClient, expiry);
        result = new AllocationResult(roomId, joinCode.Trim().ToUpperInvariant(), token, expiry);
        return true;
    }
}
