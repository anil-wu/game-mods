using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// 加密层（§11.12）：信封封装/解析 + 序号防重放 + 连接密钥派生。
    ///
    /// 密钥只存在于 SecurityLayer（业务 Mod 的 API 面无任何密钥成员——反编译也拿不到 Session Key）。
    /// 未 Enable(psk) = 直通明文兼容模式（现有测试零影响）；Enable 后全量加密（含握手帧）。
    /// 明文帧 = 现有 12B 帧头（含 ProtocolId）+ payload → 整个进加密：
    /// ProtocolId / ProtocolVersion / 业务数据全部在密文内，Relay 只见信封（§11.12 Relay 不可见）。
    ///
    /// 信封（每连接、每方向）：[Magic u16=0x4E54][Flags u8][KeyId u16][Seq u64][CipherLen u32][密文][Tag 16B]
    /// 密钥派生：sessionKey = HMAC-SHA256(psk, "conn:"||connId u32 LE||role u8)（截 32B）；
    /// 本期 psk 共享于所有连接（connId 恒 0 = NegotiationLayer.ClientConnId 客户端常量），
    /// 每连接/每方向差异化密钥列入 ECDH Provider 后续实现。keyId 本期恒 1（预留给轮换）。
    /// 防重放：每方向单调 Seq + 滑动窗口（256 位 bitmap）：seq ∈ (floor, floor+256] 且未见过 → 接受，
    /// 容忍乱序丢包（Relay/UDP 场景），拒绝重放。
    /// </summary>
    public sealed class SecurityLayer
    {
        /// <summary>信封魔数 0x4E54（"NT"，Network Transport）。</summary>
        public const ushort Magic = 0x4E54;

        /// <summary>信封头：Magic2 + Flags1 + KeyId2 + Seq8 + CipherLen4 = 17B。</summary>
        public const int HeaderSize = 17;

        /// <summary>HMAC-SHA256 截断标签。</summary>
        public const int TagSize = 16;

        private const int MaxCipherBytes = 1 << 20; // 密文长度防御上限（防内存 DoS）

        private readonly INetworkSecurityProvider _providerC2S;
        private readonly INetworkSecurityProvider _providerS2C;
        private readonly NetworkStats _stats;
        private bool _enabled;
        private ushort _keyId = 1;
        private byte[] _keyC2S = Array.Empty<byte>();
        private byte[] _keyS2C = Array.Empty<byte>();
        private readonly Dictionary<int, long> _sendSeq = new();
        private readonly Dictionary<int, ReplayWindow> _recv = new();

        /// <summary>构造：provider 工厂（算法可替换），每次调用产出独立实例（C2S/S2C 各一）。</summary>
        public SecurityLayer(Func<INetworkSecurityProvider> providerFactory, NetworkStats stats)
        {
            if (providerFactory is null) throw new ArgumentNullException(nameof(providerFactory));
            _providerC2S = providerFactory();
            _providerS2C = providerFactory();
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        }

        public bool IsEnabled => _enabled;

        /// <summary>总安全开销（安全头 + 认证标签 + 填充上界），用于配额与预算。</summary>
        public int Overhead => HeaderSize + TagSize + 16;

        /// <summary>
        /// 启用会话加密：psk（引导/配置，如房间密码、游戏密钥）经密钥派生进入 provider；
        /// 派生密钥仅存在于 SecurityLayer 内部，任何 Mod API 都不暴露（§11.12 硬要求）。
        /// </summary>
        public void Enable(byte[] psk)
        {
            if (psk is null || psk.Length == 0)
                throw new ArgumentException("psk 不能为空", nameof(psk));
            _keyC2S = DeriveKey(psk, role: 0); // client → server
            _keyS2C = DeriveKey(psk, role: 1); // server → client
            _providerC2S.SetupSession(_keyC2S, _keyId);
            _providerS2C.SetupSession(_keyS2C, _keyId);
            _enabled = true;
        }

        // ---- 发送（未启用 → 直通，密文兼容模式） ----

        /// <summary>客户端发送（C2S，到服务器的连接恒为 ClientConnId）。</summary>
        public byte[] EncryptC2S(byte[] frame) => Encrypt(NegotiationLayer.ClientConnId, frame, _keyC2S, _providerC2S);

        /// <summary>服务器发送（S2C，目标连接）。</summary>
        public byte[] EncryptS2C(int connectionId, byte[] frame) => Encrypt(connectionId, frame, _keyS2C, _providerS2C);

        // ---- 接收（失败返回 null，调用方已计数 DroppedAuth/DroppedReplay） ----

        /// <summary>服务器接收（C2S，来源连接）。</summary>
        public byte[]? DecryptC2S(int connectionId, byte[] wire) => Decrypt(connectionId, wire, _keyC2S, _providerC2S);

        /// <summary>客户端接收（S2C）。</summary>
        public byte[]? DecryptS2C(byte[] wire) => Decrypt(NegotiationLayer.ClientConnId, wire, _keyS2C, _providerS2C);

        /// <summary>重启：清空序号与重放窗口（保持启用状态与密钥）。</summary>
        public void Reset()
        {
            _sendSeq.Clear();
            _recv.Clear();
        }

        private byte[] Encrypt(int connectionId, byte[] frame, byte[] key, INetworkSecurityProvider provider)
        {
            if (!_enabled) return frame;
            _sendSeq.TryGetValue(connectionId, out var seq);
            seq++;
            _sendSeq[connectionId] = seq;

            var ciphertext = provider.Encrypt(frame); // IV(16) || AES-CBC(PKCS7)
            var wire = new byte[HeaderSize + ciphertext.Length + TagSize];
            WriteU16(wire, 0, Magic);
            wire[2] = 0; // Flags 保留（本期恒 0）
            WriteU16(wire, 3, _keyId);
            WriteU64(wire, 5, (ulong)seq);
            WriteU32(wire, 13, (uint)ciphertext.Length);
            Buffer.BlockCopy(ciphertext, 0, wire, HeaderSize, ciphertext.Length);
            var tag = ComputeTag(key, (ulong)seq, ciphertext);
            Buffer.BlockCopy(tag, 0, wire, HeaderSize + ciphertext.Length, TagSize);
            return wire;
        }

        private byte[]? Decrypt(int connectionId, byte[] wire, byte[] key, INetworkSecurityProvider provider)
        {
            if (!_enabled) return wire;
            if (wire is null || wire.Length < HeaderSize + TagSize + 1) { _stats.DroppedAuth++; return null; }
            if (ReadU16(wire, 0) != Magic) { _stats.DroppedAuth++; return null; }
            if (ReadU16(wire, 3) != _keyId) { _stats.DroppedAuth++; return null; }
            var cipherLen = (int)ReadU32(wire, 13);
            if (cipherLen <= 0 || cipherLen > MaxCipherBytes) { _stats.DroppedAuth++; return null; }
            if (HeaderSize + cipherLen + TagSize != wire.Length) { _stats.DroppedAuth++; return null; }

            var seq = (long)ReadU64(wire, 5);
            var ciphertext = new byte[cipherLen];
            Buffer.BlockCopy(wire, HeaderSize, ciphertext, 0, cipherLen);
            var expect = new byte[TagSize];
            Buffer.BlockCopy(wire, HeaderSize + cipherLen, expect, 0, TagSize);
            var actual = ComputeTag(key, (ulong)seq, ciphertext);
            if (!ConstantTimeEquals(expect, actual)) { _stats.DroppedAuth++; return null; } // encrypt-then-MAC

            // 防重放（认证通过后才检查，避免向未认证方泄露序号状态）
            var window = GetReplayWindow(connectionId);
            if (!window.TryAccept(seq)) { _stats.DroppedReplay++; return null; }

            var frame = provider.Decrypt(ciphertext);
            if (frame is null) { _stats.DroppedAuth++; return null; }
            return frame;
        }

        /// <summary>密钥派生：sessionKey = HMAC-SHA256(psk, "conn:" || connId u32 LE || role u8)；connId 原型期恒 0。</summary>
        private static byte[] DeriveKey(byte[] psk, byte role)
        {
            var data = new byte[10];
            var prefix = Encoding.UTF8.GetBytes("conn:"); // 5B
            Buffer.BlockCopy(prefix, 0, data, 0, prefix.Length);
            // data[5..9] = connId u32 LE（恒 0）
            data[9] = role;
            using var hmac = new HMACSHA256(psk);
            return hmac.ComputeHash(data); // 32B（"截 32B" 天然满足）
        }

        /// <summary>认证标签：HMAC-SHA256(key, KeyId||Seq||CipherLen||密文) 截 16B。</summary>
        private byte[] ComputeTag(byte[] key, ulong seq, byte[] ciphertext)
        {
            var input = new byte[2 + 8 + 4 + ciphertext.Length];
            WriteU16(input, 0, _keyId);
            WriteU64(input, 2, seq);
            WriteU32(input, 10, (uint)ciphertext.Length);
            Buffer.BlockCopy(ciphertext, 0, input, 14, ciphertext.Length);
            using var hmac = new HMACSHA256(key);
            var full = hmac.ComputeHash(input);
            var tag = new byte[TagSize];
            Buffer.BlockCopy(full, 0, tag, 0, TagSize);
            return tag;
        }

        private ReplayWindow GetReplayWindow(int connectionId)
        {
            if (!_recv.TryGetValue(connectionId, out var window))
            {
                window = new ReplayWindow();
                _recv[connectionId] = window;
            }
            return window;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a is null || b is null || a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // ---- LE 读写辅助 ----

        private static void WriteU16(byte[] d, int o, ushort v)
        {
            d[o] = (byte)(v & 0xFF);
            d[o + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v & 0xFF);
            d[o + 1] = (byte)((v >> 8) & 0xFF);
            d[o + 2] = (byte)((v >> 16) & 0xFF);
            d[o + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static void WriteU64(byte[] d, int o, ulong v)
        {
            for (var i = 0; i < 8; i++) d[o + i] = (byte)((v >> (8 * i)) & 0xFF);
        }

        private static ushort ReadU16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));

        private static uint ReadU32(byte[] d, int o) =>
            (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

        private static ulong ReadU64(byte[] d, int o)
        {
            ulong v = 0;
            for (var i = 7; i >= 0; i--) v = (v << 8) | d[o + i];
            return v;
        }

        /// <summary>滑动窗口防重放（256 位 bitmap）：接受 seq ∈ (floor, floor+256] 且未见过；容忍乱序丢包。</summary>
        private sealed class ReplayWindow
        {
            private const int WindowSize = 256;
            private long _floor;
            private readonly ulong[] _bits = new ulong[4]; // 4×64 = 256 位

            public bool TryAccept(long seq)
            {
                if (seq <= _floor) return false;
                if (seq - _floor > WindowSize) return false;
                var idx = (int)(seq - _floor - 1);
                var word = idx >> 6;
                var mask = 1UL << (idx & 63);
                if ((_bits[word] & mask) != 0) return false; // 已见（重放）
                _bits[word] |= mask;
                Advance();
                return true;
            }

            private void Advance()
            {
                while ((_bits[0] & 1UL) != 0)
                {
                    _floor++;
                    for (var i = 0; i < 3; i++)
                        _bits[i] = (_bits[i] >> 1) | (_bits[i + 1] << 63);
                    _bits[3] >>= 1;
                }
            }
        }
    }
}
