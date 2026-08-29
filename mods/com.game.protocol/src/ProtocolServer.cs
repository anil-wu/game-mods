using System;
using Game.Messaging;
using Game.Mod.Contract;

namespace Com.Game.Protocol
{
    /// <summary>
    /// 协议 Mod 的服务端部分（仅负责传输，无游戏逻辑）：
    /// 1. 上行：接受客户端二进制 → 找服务端 Mod 注册的解析器 → 解析 → 分发给对应服务端 Mod
    /// 2. 下行：接受服务端 Mod 消息 → 序列化为二进制 → 发送给客户端（单个 / 广播）
    /// 维护独立的服务端解析器注册表。
    /// </summary>
    public sealed class ProtocolServer
    {
        private readonly ProtocolCore _core;

        /// <summary>下行网络钩子：广播给所有客户端。</summary>
        public Func<byte[], int>? SendToAll { get; set; }

        /// <summary>下行网络钩子：发送给单个客户端。</summary>
        public Func<int, byte[], int>? SendToOne { get; set; }

        public ProtocolServer(MessageBus bus)
        {
            _core = new ProtocolCore(bus);
        }

        /// <summary>服务端 Mod 注册协议解析器（编解码器）。</summary>
        public uint Register<T>(ModId owner, IMessageCodec<T> codec) where T : struct
            => _core.Register(owner, codec);

        /// <summary>上行：客户端二进制 → 解析 → 分发给对应服务端 Mod（由网络层调用）。</summary>
        public void Receive(byte[] data)
            => _core.Receive(data);

        /// <summary>下行（广播）：服务端 Mod 消息 → 序列化 → 广播给所有客户端。</summary>
        public void Send<T>(T message) where T : struct
            => SendToAll?.Invoke(_core.Encode(message));

        /// <summary>下行（单发）：服务端 Mod 消息 → 序列化 → 发送给单个客户端。</summary>
        public void Send<T>(int clientId, T message) where T : struct
            => SendToOne?.Invoke(clientId, _core.Encode(message));

        /// <summary>服务端 Mod 注册消息处理器（上行分发目标）。</summary>
        public void Handle<T>(ModId owner, Func<T, object?> handler) where T : notnull
            => _core.Handle(owner, handler);

        /// <summary>查询消息类型对应的 ID（未注册返回 0）。</summary>
        public uint IdOf<T>() where T : notnull
            => _core.IdOf<T>();
    }
}
