using System;
using System.Collections.Generic;

namespace RelayServer.Relay;

/// <summary>房间表（内存），线程安全。</summary>
public sealed class RoomTable
{
    private readonly Dictionary<ulong, Room> _rooms = new();
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _rooms.Count; }
    }

    public Room GetOrCreate(ulong id)
    {
        lock (_lock)
        {
            if (_rooms.TryGetValue(id, out var room)) return room;
            room = new Room(id);
            _rooms[id] = room;
            return room;
        }
    }

    public bool TryGet(ulong id, out Room? room)
    {
        lock (_lock) return _rooms.TryGetValue(id, out room);
    }

    /// <summary>清理闲置超时的房间。</summary>
    public void Sweep(TimeSpan idleTimeout)
    {
        var now = DateTime.UtcNow;
        List<ulong> toRemove;
        lock (_lock)
        {
            toRemove = new List<ulong>();
            foreach (var (id, room) in _rooms)
                if (now - room.LastActivity > idleTimeout)
                    toRemove.Add(id);
            foreach (var id in toRemove) _rooms.Remove(id);
        }
    }
}
