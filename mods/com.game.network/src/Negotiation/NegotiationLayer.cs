using System;
using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// 协议版本协商层（§11.6）：连接握手状态机 + 交集计算 + required 判定 + legacy codec 选择。
    ///
    /// 状态机（每连接）：
    ///   Pending（连接首次出现）→ 客户端 ClientReady 后发 hello → 服务器计算交集并回 result →
    ///   accept=1 → Negotiated（保存 accepted 版本表 + disabled 集合）；
    ///   accept=0 → Rejected（客户端后续 Send 抛 NetworkException 带原因）。
    ///
    /// 关键语义（与既有 113 项测试兼容）：
    ///   - 后注册协议（协商完成后才注册）默认立即接受（版本 = 注册版本）——会话内协议 Mod
    ///     禁热卸载（§11.14）且双端同构加载；异常场景由方向/版本校验兜底。
    ///   - 未协商连接上的业务帧 drop + DroppedNotNegotiated（防御：握手完成前业务流量不可注入）。
    ///   - 服务器只保留"当前版本 + 上一个版本"编解码器（废弃窗口硬规则）：
    ///     协商版本 ≠ 注册版本时查 ProtocolEntry.LegacyCodecs。
    /// </summary>
    public sealed class NegotiationLayer
    {
        /// <summary>客户端侧"到服务器的连接"固定 key（单连接模型，§11.6）。</summary>
        public const int ClientConnId = 0;

        public static readonly ProtocolId HelloId = ProtocolId.Of(NetworkMod.ModIdValue, HandshakeHelloProtocol.Path);
        public static readonly ProtocolId ResultId = ProtocolId.Of(NetworkMod.ModIdValue, HandshakeResultProtocol.Path);

        /// <summary>服务器侧 required 策略：核心玩法协议所在 Mod 清单（引导/测试装配，§2.2）。</summary>
        public string[] RequiredMods { get; set; } = Array.Empty<string>();

        private readonly ProtocolRegistry _registry;
        private readonly NetworkRuntimeImpl _runtime;
        private readonly Dictionary<int, ConnectionState> _states = new();

        internal sealed class ConnectionState
        {
            public bool Negotiated;
            public bool Rejected;
            public uint Reason;
            public string Missing = "";
            public readonly Dictionary<ProtocolId, ushort> Versions = new();
            public readonly HashSet<ProtocolId> Disabled = new();
        }

        internal NegotiationLayer(ProtocolRegistry registry, NetworkRuntimeImpl runtime)
        {
            _registry = registry;
            _runtime = runtime;
        }

        public static bool IsHandshake(ProtocolId id) => id == HelloId || id == ResultId;

        // ---- 客户端侧 ----

        /// <summary>客户端传输就绪（ClientReady）→ 构建并发送 hello（§11.6）。</summary>
        public void SendHello()
        {
            var entries = new List<ProtocolVersionEntry>();
            foreach (var entry in _registry.All)
            {
                if (IsHandshake(entry.Id)) continue; // 握手协议不入表
                // 注册 ABI 不携带 path（INetworkProtocol 无路径成员），hello 的 ModId 供服务器
                // required 判定（§2.3），ProtocolId 供交集计算——path 仅为调试信息，服务器不依赖其精确性
                entries.Add(new ProtocolVersionEntry
                {
                    Id = entry.Id,
                    Version = entry.Protocol.Version,
                    ModId = entry.Owner.Value,
                    Path = "declared",
                });
            }
            _runtime.SendToServer(NetworkMod.ModIdValue, HelloId, new HandshakeHello { Entries = entries.ToArray() });
        }

        /// <summary>客户端收到 result：apply 协商结果（Negotiated / Rejected）。</summary>
        public void OnClientResult(HandshakeResult result)
        {
            var state = GetOrCreate(ClientConnId);
            state.Rejected = !result.Accept;
            state.Reason = result.Reason;
            state.Missing = result.MissingMods ?? "";
            state.Versions.Clear();
            state.Disabled.Clear();
            if (result.Accept)
            {
                state.Negotiated = true;
                foreach (var e in result.Accepted) state.Versions[e.Id] = e.Version;
                foreach (var id in result.Disabled) state.Disabled.Add(id);
            }
        }

        // ---- 服务器侧 ----

        /// <summary>服务器收到客户端 hello：计算交集与 required 判定 → 回 result（§2.3）。</summary>
        public void OnServerHello(int connectionId, HandshakeHello hello)
        {
            var state = GetOrCreate(connectionId);
            state.Versions.Clear();
            state.Disabled.Clear();
            state.Negotiated = false;
            state.Rejected = false;
            state.Reason = 0;
            state.Missing = "";

            var result = new HandshakeResult { Accept = true, Reason = 0 };

            // 1. required Mod 存在性检查：客户端 hello 中没有任何 ModId==M 的条目 → 整体拒绝
            var declaredMods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in hello.Entries) declaredMods.Add(e.ModId);
            var missing = new List<string>();
            foreach (var m in RequiredMods)
                if (!declaredMods.Contains(m)) missing.Add(m);
            if (missing.Count > 0)
            {
                result.Accept = false;
                result.Reason = 2;
                result.MissingMods = string.Join(",", missing.ToArray());
                ApplyServerResult(state, result, hello);
                SendResult(connectionId, result);
                return;
            }

            // 2. 对服务器已注册的每个非握手协议：交集判定（客户端声明的版本 ∈ {注册} ∪ {legacy}）
            var declared = new Dictionary<ProtocolId, ProtocolVersionEntry>();
            foreach (var e in hello.Entries)
                if (!declared.ContainsKey(e.Id)) declared[e.Id] = e;

            foreach (var entry in _registry.All)
            {
                if (IsHandshake(entry.Id)) continue;
                if (!declared.TryGetValue(entry.Id, out var clientEntry))
                    continue; // 客户端未声明 → 后注册/未声明协议走"默认接受注册版本"（§2.4）

                var vc = clientEntry.Version;
                if (vc == entry.Protocol.Version || entry.LegacyCodecs.ContainsKey(vc))
                {
                    result.Accepted = Append(result.Accepted, entry.Id, vc);
                }
                else
                {
                    // 无交集：核心玩法协议（owner ∈ RequiredMods）→ 整体拒绝；否则降级禁用
                    if (IsRequiredMod(entry.Owner))
                    {
                        result.Accept = false;
                        result.Reason = 1;
                        ApplyServerResult(state, result, hello);
                        SendResult(connectionId, result);
                        return;
                    }
                    result.Disabled = Append(result.Disabled, entry.Id);
                }
            }

            ApplyServerResult(state, result, hello);
            SendResult(connectionId, result);
        }

        private bool IsRequiredMod(ModId owner)
        {
            foreach (var m in RequiredMods)
                if (owner == new ModId(m)) return true;
            return false;
        }

        private static void ApplyServerResult(ConnectionState state, HandshakeResult result, HandshakeHello hello)
        {
            if (result.Accept)
            {
                state.Negotiated = true;
                foreach (var e in result.Accepted) state.Versions[e.Id] = e.Version;
                foreach (var id in result.Disabled) state.Disabled.Add(id);
            }
            else
            {
                state.Rejected = true;
                state.Reason = result.Reason;
                state.Missing = result.MissingMods ?? "";
            }
        }

        private void SendResult(int connectionId, HandshakeResult result) =>
            _runtime.SendToClient(NetworkMod.ModIdValue, connectionId, ResultId, result);

        private static ProtocolVersionEntry[] Append(ProtocolVersionEntry[] arr, ProtocolId id, ushort version)
        {
            var copy = new ProtocolVersionEntry[arr.Length + 1];
            Array.Copy(arr, copy, arr.Length);
            copy[arr.Length] = new ProtocolVersionEntry { Id = id, Version = version };
            return copy;
        }

        private static ProtocolId[] Append(ProtocolId[] arr, ProtocolId id)
        {
            var copy = new ProtocolId[arr.Length + 1];
            Array.Copy(arr, copy, arr.Length);
            copy[arr.Length] = id;
            return copy;
        }

        // ---- 发送侧校验 / 过滤 ----

        /// <summary>
        /// 客户端 SendToServer 前置校验（§1.2 步骤②）：握手帧放行；
        /// 未协商 / 拒绝 / 降级禁用 → 抛 <see cref="NetworkException"/>。
        /// </summary>
        public void ValidateSend(int connectionId, ProtocolId id)
        {
            if (IsHandshake(id)) return;
            var state = GetState(connectionId);
            if (state is null || !state.Negotiated)
                throw new NetworkException($"网络握手未完成，禁止发送业务帧（协议 {id}，连接 {connectionId}）");
            if (state.Rejected)
                throw new NetworkException($"连接被拒绝（reason={state.Reason}，缺失 Mod: {state.Missing}），禁止发送协议 {id}");
            if (state.Disabled.Contains(id))
                throw new NetworkException($"协议 {id} 已降级禁用（版本无交集），该连接上禁止发送");
        }

        /// <summary>
        /// 服务器 Broadcast / SendToClient 逐连接过滤（§1.2 步骤②）：已协商 ∧ 未拒绝 ∧ 未禁用。
        /// 未协商（无 hello）/ 已拒绝 / 已禁用的连接不发。
        /// </summary>
        public bool CanDeliver(int connectionId, ProtocolId id)
        {
            if (IsHandshake(id)) return true;
            var state = GetState(connectionId);
            if (state is null || !state.Negotiated || state.Rejected) return false;
            return !state.Disabled.Contains(id);
        }

        // ---- 接收侧校验（Dispatch 钩子） ----

        /// <summary>
        /// 接收前置校验：握手帧放行（expectedVersion = 注册版本 1）；
        /// 未协商 / 拒绝 / 禁用 → false（调用方 DroppedNotNegotiated++）。
        /// 期望版本：协商版本表有 → 协商版本；否则注册版本（后注册协议默认接受，§2.4）。
        /// </summary>
        public bool ValidateReceive(int connectionId, ProtocolId id, out ushort expectedVersion)
        {
            expectedVersion = 0;
            if (IsHandshake(id))
            {
                expectedVersion = _registry.ProtocolVersion(id);
                return true;
            }
            var state = GetState(connectionId);
            if (state is null || !state.Negotiated || state.Rejected) return false;
            if (state.Disabled.Contains(id)) return false;
            if (state.Versions.TryGetValue(id, out var v)) expectedVersion = v;
            else expectedVersion = _registry.ProtocolVersion(id);
            return true;
        }

        // ---- 编解码器选择（废弃窗口，§11.6） ----

        /// <summary>服务器发送：协商版本 + codec（协商版本 ≠ 注册版本 → legacy codec）。</summary>
        internal (ushort Version, INetworkProtocol Codec) SelectSendCodec(int connectionId, ProtocolId id, ProtocolEntry entry)
        {
            var v = SendVersion(connectionId, id);
            if (v == entry.Protocol.Version) return (v, entry.Protocol);
            return (v, entry.LegacyCodecs.TryGetValue(v, out var legacy) ? legacy : entry.Protocol);
        }

        /// <summary>服务器发送版本：协商版本表有 → 协商版本（Vc）；否则注册版本。</summary>
        public ushort SendVersion(int connectionId, ProtocolId id)
        {
            var state = GetState(connectionId);
            if (state is not null && state.Versions.TryGetValue(id, out var v)) return v;
            return _registry.ProtocolVersion(id);
        }

        /// <summary>接收 codec：客户端恒用注册 codec（accepted 版本 == 客户端注册版本，无需改码）；
        /// 服务器协商版本 ≠ 注册版本 → legacy codec。</summary>
        internal INetworkProtocol SelectReceiveCodec(int connectionId, ProtocolId id, ProtocolEntry entry, ushort expectedVersion)
        {
            if (expectedVersion == entry.Protocol.Version) return entry.Protocol;
            return entry.LegacyCodecs.TryGetValue(expectedVersion, out var legacy) ? legacy : entry.Protocol;
        }

        // ---- 状态管理 ----

        public bool IsNegotiated(int connectionId) => GetState(connectionId) is { } s && s.Negotiated;

        /// <summary>测试诊断：连接是否被拒绝（reason + missing 清单）。</summary>
        internal bool IsRejected(int connectionId, out uint reason, out string missing)
        {
            reason = 0;
            missing = "";
            if (GetState(connectionId) is not { } s || !s.Rejected) return false;
            reason = s.Reason;
            missing = s.Missing;
            return true;
        }

        /// <summary>测试诊断：连接上协议是否已降级禁用（§11.6）。</summary>
        internal bool IsDisabled(int connectionId, ProtocolId id)
            => GetState(connectionId) is { } s && s.Disabled.Contains(id);

        /// <summary>停止：清空全部连接状态（支持重启）。</summary>
        public void Reset()
        {
            _states.Clear();
        }

        private ConnectionState GetState(int connectionId) =>
            _states.TryGetValue(connectionId, out var s) ? s : null!;

        private ConnectionState GetOrCreate(int connectionId)
        {
            if (!_states.TryGetValue(connectionId, out var s))
            {
                s = new ConnectionState();
                _states[connectionId] = s;
            }
            return s;
        }
    }
}
