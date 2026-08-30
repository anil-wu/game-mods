using System;
using System.Security.Cryptography;

namespace Com.Game.Network
{
    /// <summary>
    /// 默认安全提供者（§11.12）：AES-CBC + HMAC-SHA256（encrypt-then-MAC）。
    /// 密文 = IV(16B) || AES-CBC(明文, PKCS7)；认证标签由 SecurityLayer 覆盖 KeyId||Seq||CipherLen||密文。
    /// 只用 System.Security.Cryptography（Aes / HMACSHA256 / RandomNumberGenerator），
    /// netstandard2.1 与 net8.0 双目标可用、零第三方依赖（不用 AesGcm——不在 netstandard2.1 表面）。
    /// </summary>
    public sealed class AesHmacSecurityProvider : INetworkSecurityProvider
    {
        private const int IvSize = 16;

        public string Name => "AES-CBC/HMAC-SHA256";

        /// <summary>每帧安全开销：IV(16) + Tag(16) + 填充上界(16)（§4.2）。</summary>
        public int Overhead => IvSize + 16 + 16;

        private byte[]? _sessionKey;

        /// <summary>装入会话密钥（仅 SecurityLayer 内部调用；Mod 永远拿不到 Key）。</summary>
        public void SetupSession(byte[] sessionKey, ushort keyId)
        {
            _sessionKey = sessionKey ?? throw new ArgumentNullException(nameof(sessionKey));
        }

        public byte[] Encrypt(byte[] frame)
        {
            if (_sessionKey is null) throw new InvalidOperationException("未 SetupSession 会话密钥");
            using var aes = Aes.Create();
            aes.Key = _sessionKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();
            byte[] ciphertext;
            using (var encryptor = aes.CreateEncryptor())
                ciphertext = encryptor.TransformFinalBlock(frame, 0, frame.Length);

            var result = new byte[IvSize + ciphertext.Length];
            Buffer.BlockCopy(aes.IV, 0, result, 0, IvSize);
            Buffer.BlockCopy(ciphertext, 0, result, IvSize, ciphertext.Length);
            return result;
        }

        public byte[]? Decrypt(byte[] data)
        {
            // 最小尺寸：IV(16) + 一个 AES 块(16)（PKCS7 至少填充 1 字节）
            if (_sessionKey is null || data is null || data.Length < IvSize + 16) return null;
            try
            {
                using var aes = Aes.Create();
                aes.Key = _sessionKey;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                var iv = new byte[IvSize];
                Buffer.BlockCopy(data, 0, iv, 0, IvSize);
                aes.IV = iv;
                using var decryptor = aes.CreateDecryptor();
                return decryptor.TransformFinalBlock(data, IvSize, data.Length - IvSize);
            }
            catch (CryptographicException)
            {
                return null; // 密钥不匹配 / 填充损坏（调用方 drop 计数）
            }
        }
    }
}
