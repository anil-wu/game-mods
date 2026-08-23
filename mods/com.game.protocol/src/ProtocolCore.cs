using System;
using System.Collections.Generic;
using Game.Messaging;
using Game.Mod.Contract;

namespace Com.Game.Protocol
{
    /// <summary>协议解析器（编解码）：消息类型 ↔ 二进制。</summary>
    public interface IMessageCodec<T> where T : struct
    {
        byte[] Encode(T message);
        T Decode(byte[] data);
    }

    /// <summary>
    /// 协议核心（可复用，客户端/服务端各持一个独立实例）：编解码注册 + 帧封装/解析 + 分发给对应 Mod。
    /// 二进制帧格式：[msgId u16][payload bytes]
    /// </summary>
    public sealed class ProtocolCore
    {
        private readonly MessageBus _bus;
        private readonly Dictionary<Type, ushort> _ids = new();
        private readonly Dictionary<ushort, CodecEntry> _entries = new();
        private ushort _nextId = 1;

        private sealed class CodecEntry
        {
            public ModId Owner;
            public Func<object, byte[]> Encode = null!;
            public Func<byte[], object> Decode = null!;
        }

        public ProtocolCore(MessageBus bus)
        {
            _bus = bus;
        }

        /// <summary>注册协议解析器：为消息类型分配 ID 并登记编解码器；owner = 接收该消息的 Mod。</summary>
        public ushort Register<T>(ModId owner, IMessageCodec<T> codec) where T : struct
        {
            var type = typeof(T);
            if (_ids.TryGetValue(type, out var id)) return id;

            id = _nextId++;
            _ids[type] = id;
            _entries[id] = new CodecEntry
            {
                Owner = owner,
                Encode = obj => codec.Encode((T)obj),
                Decode = data => codec.Decode(data),
            };
            return id;
        }

        /// <summary>序列化：Mod 消息 → [msgId][payload] 二进制。</summary>
        public byte[] Encode<T>(T message) where T : struct
        {
            if (!_ids.TryGetValue(typeof(T), out var id))
                throw new InvalidOperationException($"消息类型 {typeof(T).Name} 未注册协议解析器");

            var payload = _entries[id].Encode(message);
            var data = new byte[2 + payload.Length];
            data[0] = (byte)(id & 0xFF);
            data[1] = (byte)(id >> 8);
            Array.Copy(payload, 0, data, 2, payload.Length);
            return data;
        }

        /// <summary>解析并分发：二进制 → 用对应解析器反序列化 → 分发给对应 Mod。</summary>
        public void Receive(byte[] data)
        {
            if (data is null || data.Length < 2) return;
            var id = (ushort)(data[0] | (data[1] << 8));
            if (!_entries.TryGetValue(id, out var entry)) return;

            var payload = new byte[data.Length - 2];
            Array.Copy(data, 2, payload, 0, payload.Length);

            var message = entry.Decode(payload);
            _bus.SendTo(entry.Owner, message); // 分发给对应的 Mod
        }

        /// <summary>接收端：Mod 注册消息处理器（分发的目标）。</summary>
        public void Handle<T>(ModId owner, Func<T, object?> handler) where T : notnull
            => _bus.Handle(owner, handler);

        /// <summary>查询消息类型对应的 ID（未注册返回 0）。</summary>
        public ushort IdOf<T>() where T : notnull
            => _ids.TryGetValue(typeof(T), out var id) ? id : (ushort)0;
    }
}
