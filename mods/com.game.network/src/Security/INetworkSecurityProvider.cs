namespace Com.Game.Network
{
    /// <summary>
    /// 网络加密提供者（§11.12）：算法可替换（原型：AES-CBC + HMAC-SHA256；
    /// ECDH/TLS 化作为后续 Provider 实现，密钥交换不涉及接口变化）。
    /// 非泛型、纯字节（Rule 13/14）：只看到 byte[]，不接触任何 Mod 类型。
    /// 密钥经 <see cref="SetupSession"/> 一次装入——仅 Network.Mod 内部（SecurityLayer）调用，
    /// 业务 Mod 的 API 面（INetworkContext）没有任何密钥成员（§11.12 硬要求：反编译也拿不到 Session Key）。
    /// </summary>
    public interface INetworkSecurityProvider
    {
        /// <summary>算法名（诊断/日志）。</summary>
        string Name { get; }

        /// <summary>每帧安全开销（安全头 + 认证标签 + 填充），用于配额与预算。</summary>
        int Overhead { get; }

        /// <summary>装入会话密钥（仅 Network.Mod 内部调用；Mod 永远拿不到 Key）。</summary>
        void SetupSession(byte[] sessionKey, ushort keyId);

        /// <summary>加密整帧（含 12B 协议头）→ 密文（IV + ciphertext）。</summary>
        byte[] Encrypt(byte[] frame);

        /// <summary>解密 + 认证；失败返回 null（调用方 drop 计数）。</summary>
        byte[]? Decrypt(byte[] ciphertext);
    }
}
