using System;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// 会话信息（§11.11 Session 模型）：Host CreateSession() → Relay 返回房间码；Client JoinSession(房间码) → Relay 找到 Host。
    /// 控制面产物 → 传输 Config 的桥。
    /// </summary>
    public sealed class RelaySessionInfo
    {
        public string JoinCode = "";
        public ulong RoomId;
        public string RelayHost = "";
        public int RelayPort;
        public byte[] Token = Array.Empty<byte>();
        public long ExpiryMs;

        /// <summary>组装 RelayClientTransport 配置（IsHost 决定 BindHost 或 BindClient 角色）。</summary>
        public RelayClientTransport.Config BuildTransportConfig(bool isHost) => new()
        {
            RelayHost = RelayHost,
            RelayPort = RelayPort,
            RoomId = RoomId,
            Token = Token,
            ExpiryMs = ExpiryMs,
            IsHost = isHost,
        };
    }

    /// <summary>
    /// 会话模型入口（§11.11）：CreateSession / JoinSession 的纯逻辑封装。
    /// Host：CreateSession() → /allocate → 得房间码（对外公布）→ BuildTransportConfig(host)。
    /// Client：JoinSession(joinCode) → /resolve → 得 token → BuildTransportConfig(client) → BindClient → 握手。
    /// Relay 节点视角只有一个 Session 下的连接集合，不认识任何游戏概念（§11.11）。
    /// </summary>
    public static class RelaySessions
    {
        /// <summary>Host：CreateSession() → 控制面分配房间，返回会话信息（含房间码）。</summary>
        public static RelaySessionInfo CreateSession(RelayAllocationClient allocation, string relayHost, int relayPort)
        {
            if (allocation is null) throw new ArgumentNullException(nameof(allocation));
            var a = allocation.Allocate();
            return new RelaySessionInfo
            {
                JoinCode = a.JoinCode,
                RoomId = a.RoomId,
                RelayHost = relayHost,
                RelayPort = relayPort,
                Token = a.Token,
                ExpiryMs = a.ExpiryMs,
            };
        }

        /// <summary>Client：JoinSession(房间码) → 解析出 token。返回 false = 房间码无效（找不到 Host 会话）。</summary>
        public static bool TryJoinSession(RelayAllocationClient allocation, string joinCode,
            string relayHost, int relayPort, out RelaySessionInfo session)
        {
            session = null!;
            if (allocation is null) return false;
            var a = allocation.TryResolve(joinCode);
            if (a is null) return false;
            session = new RelaySessionInfo
            {
                JoinCode = a.JoinCode,
                RoomId = a.RoomId,
                RelayHost = relayHost,
                RelayPort = relayPort,
                Token = a.Token,
                ExpiryMs = a.ExpiryMs,
            };
            return true;
        }
    }

    /// <summary>
    /// Auto 路径选择（§11.3）：先尝试 Direct，失败自动回退 Relay——连接协商由 Network Mod 与云端
    /// Session Service 完成，业务无感知。Direct 连通性探测由调用方注入（如 UDP 地址发现/打洞握手，
    /// 原型期可用简单的 connect 探测；Mirror 接入时在 Direct Provider 内实现），测试注入假探测。
    /// 探测成功 → NetworkPath.Direct；失败 → NetworkPath.Relay（此时引导方按 §11.11 走
    /// CreateSession/JoinSession 建 Relay 会话）。
    /// </summary>
    public static class AutoPathResolver
    {
        public static NetworkPath Resolve(Func<bool>? directProbe)
        {
            var directOk = directProbe?.Invoke() ?? false;
            return directOk ? NetworkPath.Direct : NetworkPath.Relay;
        }
    }
}
