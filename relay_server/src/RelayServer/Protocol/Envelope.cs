using System;

namespace RelayServer.Protocol;

public static class RelayProtocol
{
    public const ushort Magic = 0x4D52;   // "MR"
    public const byte Version = 1;
    /// <summary>报头: magic u16 + ver u8 + type u8 + connId u16。</summary>
    public const int HeaderSize = 6;
    public const int TokenSize = 16;
    public const int MaxPayload = 1400;
    public const int MaxDatagram = HeaderSize + MaxPayload;
    /// <summary>房主固定 connId。</summary>
    public const ushort HostConnId = 0;
}

/// <summary>Relay 隧道报文。</summary>
public readonly struct Packet
{
    public MessageType Type { get; }
    public ushort ConnId { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public Packet(MessageType type, ushort connId, ReadOnlyMemory<byte> payload)
    {
        Type = type;
        ConnId = connId;
        Payload = payload;
    }

    public byte[] Encode()
    {
        var buf = new byte[RelayProtocol.HeaderSize + Payload.Length];
        buf[0] = (byte)(RelayProtocol.Magic & 0xFF);
        buf[1] = (byte)(RelayProtocol.Magic >> 8);
        buf[2] = RelayProtocol.Version;
        buf[3] = (byte)Type;
        buf[4] = (byte)(ConnId & 0xFF);
        buf[5] = (byte)(ConnId >> 8);
        Payload.Span.CopyTo(buf.AsSpan(RelayProtocol.HeaderSize));
        return buf;
    }

    public static bool TryDecode(byte[] data, out Packet packet)
    {
        packet = default;
        if (data is null || data.Length < RelayProtocol.HeaderSize) return false;
        if (data.Length > RelayProtocol.MaxDatagram) return false;
        var magic = (ushort)(data[0] | (data[1] << 8));
        if (magic != RelayProtocol.Magic) return false;
        if (data[2] != RelayProtocol.Version) return false;

        var type = (MessageType)data[3];
        var connId = (ushort)(data[4] | (data[5] << 8));
        var payload = new byte[data.Length - RelayProtocol.HeaderSize];
        Array.Copy(data, RelayProtocol.HeaderSize, payload, 0, payload.Length);
        packet = new Packet(type, connId, payload);
        return true;
    }
}
