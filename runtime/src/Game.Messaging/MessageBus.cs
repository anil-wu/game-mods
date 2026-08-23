using System;
using System.Collections.Generic;
using Game.Mod.Contract;

namespace Game.Messaging
{
    /// <summary>消息通信异常。</summary>
    public sealed class MessageException : Exception
    {
        public MessageException(string message) : base(message) { }
    }

    /// <summary>
    /// 消息总线：Mod 之间唯一的通信契约。
    /// 三类模式：广播（Subscribe/Broadcast）、定向（Handle/SendTo）、请求应答（Handle/Request）。
    /// 本地/网络透明由上层扩展，本实现为进程内投递。
    /// </summary>
    public sealed class MessageBus
    {
        // (目标 mod, 消息类型) → 处理器
        private readonly Dictionary<ModId, Dictionary<Type, Func<object, object?>>> _handlers = new();
        // 消息类型 → 订阅者列表
        private readonly Dictionary<Type, List<(ModId Owner, Action<object> Action)>> _subscribers = new();

        public Action<string>? Log { get; set; }
        public Action<Exception>? OnError { get; set; }

        /// <summary>声明处理某类定向/请求消息。</summary>
        public void Handle<T>(ModId owner, Func<T, object?> handler) where T : notnull
        {
            if (!_handlers.TryGetValue(owner, out var map))
            {
                map = new Dictionary<Type, Func<object, object?>>();
                _handlers[owner] = map;
            }
            map[typeof(T)] = msg => handler((T)msg);
        }

        /// <summary>订阅某类广播消息。</summary>
        public void Subscribe<T>(ModId owner, Action<T> handler) where T : notnull
        {
            if (!_subscribers.TryGetValue(typeof(T), out var list))
            {
                list = new List<(ModId, Action<object>)>();
                _subscribers[typeof(T)] = list;
            }
            list.Add((owner, msg => handler((T)msg)));
        }

        /// <summary>定向消息（fire-and-forget）。目标缺失 → 丢弃并记日志，不抛异常。</summary>
        public void SendTo<T>(ModId target, T message) where T : notnull
        {
            if (!_handlers.TryGetValue(target, out var map) || !map.TryGetValue(typeof(T), out var handler))
            {
                Log?.Invoke($"[MessageBus] 目标 Mod '{target}' 未处理消息 {typeof(T).Name}，已丢弃");
                return;
            }
            try { handler(message); }
            catch (Exception e) { OnError?.Invoke(e); }
        }

        /// <summary>请求应答。目标缺失/应答类型不符 → 抛 MessageException。</summary>
        public TReply Request<T, TReply>(ModId target, T message) where T : notnull
        {
            if (!_handlers.TryGetValue(target, out var map) || !map.TryGetValue(typeof(T), out var handler))
                throw new MessageException($"目标 Mod '{target}' 未处理请求 {typeof(T).Name}");

            var reply = handler(message);
            if (reply is TReply r)
                return r;
            throw new MessageException($"请求 {typeof(T).Name} 应答类型不符: 期望 {typeof(TReply).Name}");
        }

        /// <summary>广播消息。订阅者异常被隔离，不影响其他订阅者。</summary>
        public void Broadcast<T>(T message) where T : notnull
        {
            if (!_subscribers.TryGetValue(typeof(T), out var list)) return;
            foreach (var (_, action) in list)
            {
                try { action(message); }
                catch (Exception e) { OnError?.Invoke(e); }
            }
        }
    }

}
