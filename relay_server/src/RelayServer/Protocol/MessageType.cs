namespace RelayServer.Protocol;

public enum MessageType : byte
{
    BindHost = 1,
    BindClient = 2,
    BindAck = 3,
    BindFail = 4,
    PeerJoined = 5,
    PeerLeft = 6,
    /// <summary>可靠流量（KCP 段，端到端）。</summary>
    DataKcp = 7,
    /// <summary>不可靠流量（裸游戏报文，不进 KCP）。</summary>
    DataRaw = 8,
    Ping = 9,
    Pong = 10,
    /// <summary>退房（客户端/房主主动离开；节点收到后移除 peer 并通知 Host PeerLeft）。</summary>
    Leave = 11,
}
