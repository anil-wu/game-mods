using System;

namespace RelayServer.Protocol;

/// <summary>
/// Bind 消息载荷: [roomId u64][role u8][expiry u64 ms][token 16B] = 33 字节。
/// </summary>
public readonly struct BindRequest
{
    public const int Size = 33;

    public ulong RoomId { get; }
    public byte Role { get; }
    public DateTimeOffset Expiry { get; }
    public ReadOnlyMemory<byte> Token { get; }

    private BindRequest(ulong roomId, byte role, DateTimeOffset expiry, ReadOnlyMemory<byte> token)
    {
        RoomId = roomId;
        Role = role;
        Expiry = expiry;
        Token = token;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out BindRequest req)
    {
        req = default;
        if (payload.Length < Size) return false;

        ulong roomId = 0;
        for (int i = 0; i < 8; i++) roomId |= (ulong)payload[i] << (8 * i);
        var role = payload[8];

        ulong ms = 0;
        for (int i = 0; i < 8; i++) ms |= (ulong)payload[9 + i] << (8 * i);
        var expiry = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);

        var token = new byte[TokenCodec.TokenSize];
        payload.Slice(17, TokenCodec.TokenSize).CopyTo(token);

        req = new BindRequest(roomId, role, expiry, token);
        return true;
    }

    public bool Verify(byte[] secret) => TokenCodec.Verify(secret, RoomId, Role, Expiry, Token.Span);
}
