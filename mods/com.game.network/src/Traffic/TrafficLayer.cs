using System;
using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// 流量配额/限流层（§11.13）：每 Mod 网络预算（maxMessageSize / maxPacketRate / maxBandwidth）执行 +
    /// Throttle（窗口内违规 ≥3 → 挂起 ThrottleSuspendMs，期间一切发送直接 drop 计数）+
    /// 服务器连接级收包防御（MaxConnectionBandwidth）。
    ///
    /// 框架基础设施（com.game.network：握手 + ECS Replication）视为可信，不执行配额
    /// （§11.13 防的是恶意/写错的业务 Mod 洪水，如 Update()→Send()；Network.Mod 自身是 Framework Mod，§10.4）；
    /// 业务 Mod 未配置预算时按默认值执行。
    /// 统计无条件记录（可观测性不因限流丢失）；执行只发生在超限时（§5.4）。
    /// </summary>
    public sealed class TrafficLayer
    {
        /// <summary>挂起阈值：窗口内违规次数 ≥3 → Throttle 挂起（§5.4）。</summary>
        public const int SuspendViolationThreshold = 3;

        private const long WindowMs = 1000;

        private readonly NetworkStats _stats;
        private readonly TrafficMonitor _monitor;
        private readonly Dictionary<ModId, NetworkBudget> _budgets = new();
        private readonly Dictionary<ModId, Counter> _violations = new();
        private readonly Dictionary<ModId, long> _suspendedUntil = new();
        private readonly NetworkBudget _defaultBudget = new();
        private int _connReceiveLimit = new NetworkBudget().MaxConnectionBandwidth;

        /// <summary>时间源（测试可注入伪时钟，驱动窗口/挂起推进；netstandard2.1 无 TickCount64）。</summary>
        public Func<long> TimeMs { get; set; } = () => Environment.TickCount;

        /// <summary>当前生效的单连接收包限速（取各 Mod 预算 MaxConnectionBandwidth 的最大容限，默认 16384）。</summary>
        public int ConnectionReceiveLimit => _connReceiveLimit;

        internal TrafficLayer(NetworkStats stats)
        {
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _monitor = new TrafficMonitor(stats);
        }

        internal TrafficMonitor Monitor => _monitor;

        /// <summary>配置 Mod 预算（null = 恢复默认）；连接级收包限速取各配置预算 MaxConnectionBandwidth 的最大容限（无配置 = 默认 16384）。</summary>
        public void ConfigureBudget(ModId owner, NetworkBudget? budget)
        {
            if (budget is null) _budgets.Remove(owner);
            else _budgets[owner] = budget;
            _connReceiveLimit = ComputeConnLimit();
        }

        private int ComputeConnLimit()
        {
            if (_budgets.Count == 0) return new NetworkBudget().MaxConnectionBandwidth; // 无配置 → 默认 16384
            var limit = 0;
            foreach (var b in _budgets.Values)
                if (b.MaxConnectionBandwidth > limit) limit = b.MaxConnectionBandwidth;
            return limit;
        }

        /// <summary>
        /// 发送前置配额执行（§1.2 步骤⑤）：返回 false = 本条消息被限流 drop（已计 DroppedThrottle）。
        /// 统计无条件记录；握手帧不计业务统计且放行；框架基础设施不执行配额。
        /// </summary>
        public bool CheckSend(ModId owner, ProtocolId pid, int logicalLen)
        {
            var now = TimeMs();
            var handshake = NegotiationLayer.IsHandshake(pid);
            if (!handshake) _monitor.RecordSendLogical(owner, pid, logicalLen, now); // 业务口径统计
            if (handshake || IsInfrastructure(owner)) return true; // 基础设施帧不执行配额

            var budget = BudgetOf(owner);

            // 挂起窗口：ThrottleSuspendMs 内一切发送直接 drop 计数
            if (_suspendedUntil.TryGetValue(owner, out var until) && now < until)
            {
                _stats.DroppedThrottle++;
                return false;
            }

            // Mod 级单消息硬顶（协议 MaxSize 之外的 Mod 自设上限，§11.12 UGC 防护）
            if (logicalLen > budget.MaxMessageSize) { Violate(owner, now); return false; }

            // 窗口速率/带宽（消息数 / 逻辑字节，§5.4）
            var window = _monitor.ModWindow(owner, now);
            if (window.Messages > budget.MaxPacketRate || window.LogicalBytes > budget.MaxBandwidth)
            {
                Violate(owner, now);
                return false;
            }
            return true;
        }

        /// <summary>发送传输字节记账（加密后）：连接级 Sent + Mod 级 Transport（§5.3 加密前后两档）。</summary>
        public void AccountSendWire(ModId owner, int connectionId, int transportBytes)
        {
            var now = TimeMs();
            _monitor.RecordSendWire(owner, connectionId, transportBytes, now);
        }

        /// <summary>
        /// 接收记账 + 连接级收包预算（§1.3 步骤⑤'，服务器侧为主）：恶意/失控客户端拖垮服务器的防线。
        /// 统计无条件记录；仅超限时执行 drop。返回 false = drop（已计 DroppedThrottle）。
        /// </summary>
        public bool AccountReceive(int connectionId, int frameLen, bool enforce)
        {
            var now = TimeMs();
            _monitor.RecordReceive(connectionId, frameLen, now);
            if (!enforce) return true;
            if (_monitor.ConnRecvWindow(connectionId, now) > _connReceiveLimit)
            {
                _stats.DroppedThrottle++;
                return false;
            }
            return true;
        }

        /// <summary>MaxSize 违规记账（§11.12 UGC 防护：对该 Mod 记违规；DroppedQuota 由调用方计数）。</summary>
        public void RecordViolation(ModId owner) => Violate(owner, TimeMs());

        /// <summary>按 owner 清理预算与统计（Rule 20）。</summary>
        public void UnregisterAll(ModId owner)
        {
            _budgets.Remove(owner);
            _violations.Remove(owner);
            _suspendedUntil.Remove(owner);
            _monitor.RemoveOwner(owner);
            ConfigureBudget(owner, null); // 重算连接级限速
        }

        /// <summary>停止：清空窗口/违规/挂起/预算（支持重启）。</summary>
        public void Reset()
        {
            _budgets.Clear();
            _violations.Clear();
            _suspendedUntil.Clear();
            _monitor.Clear();
            _connReceiveLimit = ComputeConnLimit();
        }

        // ---- 诊断（测试/可观测性钩子） ----

        public bool IsSuspended(ModId owner) =>
            _suspendedUntil.TryGetValue(owner, out var until) && TimeMs() < until;

        public bool HasBudget(ModId owner) => _budgets.ContainsKey(owner);

        public long ConnSentBytes(int connectionId) => _monitor.ConnSent(connectionId);
        public long ConnRecvBytes(int connectionId) => _monitor.ConnRecv(connectionId);
        public int ModSendMessages(ModId owner) => _monitor.ModMessages(owner);
        public long ModLogicalBytes(ModId owner) => _monitor.ModLogical(owner);
        public long ModTransportBytes(ModId owner) => _monitor.ModTransport(owner);
        public long ProtocolBytes(ProtocolId pid) => _monitor.ProtoBytes(pid);

        private NetworkBudget BudgetOf(ModId owner) =>
            _budgets.TryGetValue(owner, out var b) ? b : _defaultBudget;

        /// <summary>框架基础设施 Mod（com.game.network：握手 + 复制协议）视为可信，不执行配额（§10.4）。</summary>
        private static bool IsInfrastructure(ModId owner) => owner == NetworkMod.ModIdValue;

        private sealed class Counter
        {
            public long StartMs;
            public int Count;
        }

        private void Violate(ModId owner, long now)
        {
            var vio = GetViolation(owner, now);
            vio.Count++;
            _stats.DroppedThrottle++;
            if (vio.Count >= SuspendViolationThreshold)
                _suspendedUntil[owner] = now + BudgetOf(owner).ThrottleSuspendMs;
        }

        private Counter GetViolation(ModId owner, long now)
        {
            if (_violations.TryGetValue(owner, out var c) && now - c.StartMs < WindowMs) return c;
            c = new Counter { StartMs = now };
            _violations[owner] = c;
            return c;
        }
    }
}
