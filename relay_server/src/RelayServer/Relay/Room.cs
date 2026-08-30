using System;
using System.Collections.Generic;
using System.Net;
using RelayServer.Protocol;

namespace RelayServer.Relay;

/// <summary>一个中转房间：房主 + 客户端集合。</summary>
public sealed class Room
{
    public ulong Id { get; }
    public IPEndPoint? Host { get; set; }
    public Dictionary<ushort, IPEndPoint> Clients { get; } = new();
    public ushort NextConnId { get; set; } = RelayProtocol.FirstRemoteConnId;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public Room(ulong id) => Id = id;

    public ushort AssignConnId() => NextConnId++;

    /// <summary>移除指定 connId 的客户端（Leave / 闲置清扫时调用）。返回是否真的存在并移除。</summary>
    public bool RemoveClient(ushort connId) => Clients.Remove(connId);

    /// <summary>客户端是否已满（connId 分配溢出防御，原型期 65534 上限）。</summary>
    public bool IsFull => NextConnId >= ushort.MaxValue;

    public void Touch() => LastActivity = DateTime.UtcNow;
}
