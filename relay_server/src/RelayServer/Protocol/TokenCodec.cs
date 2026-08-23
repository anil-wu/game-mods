using System;
using System.Security.Cryptography;

namespace RelayServer.Protocol;

/// <summary>
/// 无状态 HMAC token：HMAC-SHA256(secret, roomId+role+expiry) 截断 16 字节。
/// Relay 节点无需查库即可校验。
/// </summary>
public static class TokenCodec
{
    public const byte RoleHost = 0;
    public const byte RoleClient = 1;
    public const int TokenSize = 16;

    public static byte[] Issue(byte[] secret, ulong roomId, byte role, DateTimeOffset expiry)
    {
        using var hmac = new HMACSHA256(secret);
        var sig = hmac.ComputeHash(BuildPayload(roomId, role, expiry));
        var token = new byte[TokenSize];
        Array.Copy(sig, token, TokenSize);
        return token;
    }

    public static bool Verify(byte[] secret, ulong roomId, byte role, DateTimeOffset expiry, ReadOnlySpan<byte> token)
    {
        if (token.Length != TokenSize) return false;
        using var hmac = new HMACSHA256(secret);
        var sig = hmac.ComputeHash(BuildPayload(roomId, role, expiry));
        return sig.AsSpan(0, TokenSize).SequenceEqual(token);
    }

    private static byte[] BuildPayload(ulong roomId, byte role, DateTimeOffset expiry)
    {
        var payload = new byte[17];
        for (int i = 0; i < 8; i++) payload[i] = (byte)(roomId >> (8 * i));
        payload[8] = role;
        var ms = (ulong)expiry.ToUnixTimeMilliseconds();
        for (int i = 0; i < 8; i++) payload[9 + i] = (byte)(ms >> (8 * i));
        return payload;
    }
}
