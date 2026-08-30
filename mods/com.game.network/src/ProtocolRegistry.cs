using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>协议注册表条目（§11.6）。</summary>
    internal sealed class ProtocolEntry
    {
        public ProtocolId Id;
        public ModId Owner;
        public INetworkProtocol Protocol = null!;
        public INetworkHandler? Handler;

        /// <summary>旧版本编解码器（§11.6 废弃窗口：当前版本 + 上一个版本，按版本号索引）。</summary>
        public readonly Dictionary<ushort, INetworkProtocol> LegacyCodecs = new();
    }

    /// <summary>
    /// 协议注册表（§11.6，从 NetworkRuntimeImpl 迁出的瘦身层）：
    /// ProtocolEntry（含 LegacyCodecs）/ 注册与冲突检测 / owner 清理 / 方向与归属校验 /
    /// 协商版本校验与 codec 选择。NetworkRuntimeImpl 只做编排，不拥有注册表细节。
    /// </summary>
    internal sealed class ProtocolRegistry
    {
        private readonly Dictionary<ProtocolId, ProtocolEntry> _protocols = new();

        public IReadOnlyCollection<ProtocolEntry> All => _protocols.Values;

        public bool IsRegistered(ProtocolId id) => _protocols.ContainsKey(id);

        public bool TryGet(ProtocolId id, out ProtocolEntry entry) => _protocols.TryGetValue(id, out entry!);

        /// <summary>注册协议（owner = 声明 Mod；同 ID 异 owner 冲突拒绝；同 owner 重复注册幂等）。</summary>
        public void Register(ModId owner, INetworkProtocol protocol, INetworkHandler? handler)
        {
            if (protocol is null) throw new System.ArgumentNullException(nameof(protocol));
            if (_protocols.TryGetValue(protocol.Id, out var existing))
            {
                if (existing.Owner != owner)
                    throw new NetworkException(
                        $"协议 ID {protocol.Id} 冲突: '{owner}' 与 '{existing.Owner}'（构建期查重漏网，§11.5）");
                return; // 幂等
            }
            _protocols[protocol.Id] = new ProtocolEntry
            {
                Id = protocol.Id,
                Owner = owner,
                Protocol = protocol,
                Handler = handler,
            };
        }

        public void Unregister(ModId owner, ProtocolId id)
        {
            if (_protocols.TryGetValue(id, out var entry) && entry.Owner == owner)
                _protocols.Remove(id);
        }

        /// <summary>按 owner 一次性清理协议（含其 legacy codecs）（Rule 20）。</summary>
        public void UnregisterAll(ModId owner)
        {
            var stale = new List<ProtocolId>();
            foreach (var (id, entry) in _protocols)
                if (entry.Owner == owner) stale.Add(id);
            foreach (var id in stale) _protocols.Remove(id);
        }

        /// <summary>注册协议旧版本编解码器（§11.6 废弃窗口：仅 owner 可注册，UnregisterAll 一并清理）。</summary>
        public void RegisterLegacyCodec(ModId owner, ProtocolId id, ushort version, INetworkProtocol codec)
        {
            if (codec is null) throw new System.ArgumentNullException(nameof(codec));
            if (!_protocols.TryGetValue(id, out var entry))
                throw new NetworkException($"协议 {id} 未注册，不能注册旧版本 codec");
            if (entry.Owner != owner)
                throw new NetworkException($"Mod '{owner}' 不能为其他 Mod 的协议 {id} 注册旧版本 codec（归属 '{entry.Owner}'）");
            entry.LegacyCodecs[version] = codec;
        }

        /// <summary>发送前归属 / 方向校验（§11.7 ModChannel 隔离）。</summary>
        public ProtocolEntry ValidateSend(ModId sender, ProtocolId id, NetworkDirection direction)
        {
            if (!_protocols.TryGetValue(id, out var entry))
                throw new NetworkException($"协议 {id} 未注册");
            if (entry.Owner != sender)
                throw new NetworkException($"Mod '{sender}' 不能发送其他 Mod 的协议 {id}（归属 '{entry.Owner}'）");
            if (entry.Protocol.Direction != direction)
                throw new NetworkException($"协议 {id} 方向为 {entry.Protocol.Direction}，不能以 {direction} 发送");
            return entry;
        }

        public ushort ProtocolVersion(ProtocolId id) =>
            _protocols.TryGetValue(id, out var entry) ? entry.Protocol.Version : (ushort)0;
    }
}
