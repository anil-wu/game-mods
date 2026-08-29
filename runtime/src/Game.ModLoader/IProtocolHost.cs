using System;

namespace Game.ModLoader
{
    /// <summary>
    /// 协议宿主契约：协议核心 Mod 实现此接口，供框架网络层（Mirror）接线。
    /// 纯传输抽象，不绑定具体网络实现。
    /// </summary>
    public interface IProtocolHost
    {
        /// <summary>绑定客户端侧传输：发送二进制给服务端。</summary>
        void BindClient(Func<byte[], int> sendToServer);

        /// <summary>绑定服务端侧传输：广播 / 单发二进制给客户端。</summary>
        void BindServer(Func<byte[], int> sendToAll, Func<int, byte[], int> sendToOne);

        /// <summary>服务端收到客户端二进制（由网络层回调）。</summary>
        void OnServerBinary(byte[] data);

        /// <summary>客户端收到服务端二进制（由网络层回调）。</summary>
        void OnClientBinary(byte[] data);
    }
}
