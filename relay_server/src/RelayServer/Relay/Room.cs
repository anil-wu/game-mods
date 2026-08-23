using System;
using System.Collections.Generic;
using System.Net;

namespace RelayServer.Relay;

/// <summary>一个中转房间：房主 + 客户端集合。</summary>
public sealed class Room
{
    public ulong Id { get; }
    public IPEndPoint? Host { get; set; }
    public Dictionary<ushort, IPEndPoint> Clients { get; } = new();
    public ushort NextConnId { get; set; } = 1;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public Room(ulong id) => Id = id;

    public ushort AssignConnId() => NextConnId++;

    public void Touch() => LastActivity = DateTime.UtcNow;
}
