using System;
using Game.Messaging;
using Game.Mod.Contract;

namespace Com.Game.Protocol
{
    /// <summary>
    /// 协议 Mod 的客户端部分（仅负责传输，无游戏逻辑）：
    /// 1. 上行：接受游戏 Mod 客户端消息 → 序列化为二进制 → 发送给服务端
    /// 2. 下行：接受服务端二进制 → 找客户端 Mod 注册的解析器 → 解析 → 分发给对应客户端 Mod
    /// 维护独立的客户端解析器注册表。
    /// </summary>
    public sealed class ProtocolClient
    {
        private readonly ProtocolCore _core;

        /// <summary>上行网络钩子：发送二进制给服务端（由框架网络层注入）。</summary>
        public Func<byte[], int>? SendToServer { get; set; }

        public ProtocolClient(MessageBus bus)
        {
            _core = new ProtocolCore(bus);
        }

        /// <summary>客户端 Mod 注册协议解析器（编解码器）。</summary>
        public uint Register<T>(ModId owner, IMessageCodec<T> codec) where T : struct
            => _core.Register(owner, codec);

        /// <summary>上行：客户端 Mod 消息 → 序列化 → 二进制 → 服务端。</summary>
        public void Send<T>(T message) where T : struct
            => SendToServer?.Invoke(_core.Encode(message));

        /// <summary>下行：服务端二进制 → 解析 → 分发给对应客户端 Mod（由网络层调用）。</summary>
        public void Receive(byte[] data)
            => _core.Receive(data);

        /// <summary>客户端 Mod 注册消息处理器（下行分发目标）。</summary>
        public void Handle<T>(ModId owner, Func<T, object?> handler) where T : notnull
            => _core.Handle(owner, handler);

        /// <summary>查询消息类型对应的 ID（未注册返回 0）。</summary>
        public uint IdOf<T>() where T : notnull
            => _core.IdOf<T>();
    }
}
