using System;
using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// 流量统计（§11.13）：Connection / Mod / Protocol 三级滑动 1 秒窗口统计，
    /// 累计写入 NetworkStats（新字段 BytesByConnection / BytesByProtocol 与既有字段 additive，不删改既有）。
    /// Mod 级两档：逻辑字节（§11.9 压缩前——本期无压缩层，即 Encode 后帧长）与传输字节（加密后）。
    /// 窗口即"速率"依据：同一窗口被 TrafficLayer 用于配额执行（单一事实源，不重复计数）。
    /// </summary>
    internal sealed class TrafficMonitor
    {
        private const long WindowMs = 1000;

        private sealed class ConnStat
        {
            public long WinStart;
            public long Recv;
            public long Sent;
        }

        private sealed class ModStat
        {
            public long WinStart;
            public int Messages;
            public long Logical;
            public long Transport;
        }

        private sealed class ProtoStat
        {
            public long WinStart;
            public int Messages;
            public long Bytes;
        }

        private readonly NetworkStats _stats;
        private readonly Dictionary<int, ConnStat> _conns = new();
        private readonly Dictionary<ModId, ModStat> _mods = new();
        private readonly Dictionary<ProtocolId, ProtoStat> _protos = new();

        public TrafficMonitor(NetworkStats stats) => _stats = stats ?? throw new ArgumentNullException(nameof(stats));

        /// <summary>发送逻辑统计（业务口径：握手帧由调用方排除）：Mod/Protocol 窗口 + NetworkStats 累计。</summary>
        public void RecordSendLogical(ModId owner, ProtocolId pid, int bytes, long now)
        {
            _stats.BytesSent += bytes;
            _stats.MessagesSent++;
            _stats.BytesByMod[owner] = _stats.BytesByMod.TryGetValue(owner, out var m) ? m + bytes : bytes;
            _stats.BytesByProtocol[pid] = _stats.BytesByProtocol.TryGetValue(pid, out var pb) ? pb + bytes : bytes;
            _stats.MessagesByProtocol[pid] = _stats.MessagesByProtocol.TryGetValue(pid, out var p) ? p + 1 : 1;

            var ms = GetMod(owner, now);
            ms.Messages++;
            ms.Logical += bytes;
            var ps = GetProto(pid, now);
            ps.Messages++;
            ps.Bytes += bytes;
        }

        /// <summary>发送传输字节（加密后）：连接级 Sent + Mod 级 Transport 窗口。</summary>
        public void RecordSendWire(ModId owner, int connectionId, int bytes, long now)
        {
            var cs = GetConn(connectionId, now);
            cs.Sent += bytes;
            var ms = GetMod(owner, now);
            ms.Transport += bytes;
        }

        /// <summary>接收统计：连接级 Recv 窗口 + NetworkStats.BytesByConnection 累计。</summary>
        public void RecordReceive(int connectionId, int bytes, long now)
        {
            var cs = GetConn(connectionId, now);
            cs.Recv += bytes;
            _stats.BytesByConnection[connectionId] =
                _stats.BytesByConnection.TryGetValue(connectionId, out var c) ? c + bytes : bytes;
        }

        // ---- 窗口读取（配额执行） ----

        public (int Messages, long LogicalBytes) ModWindow(ModId owner, long now)
        {
            var s = GetMod(owner, now);
            return (s.Messages, s.Logical);
        }

        public long ConnRecvWindow(int connectionId, long now) => GetConn(connectionId, now).Recv;

        // ---- 诊断（测试） ----

        public long ConnSent(int connectionId) => _conns.TryGetValue(connectionId, out var s) ? s.Sent : 0;
        public long ConnRecv(int connectionId) => _conns.TryGetValue(connectionId, out var s) ? s.Recv : 0;
        public int ModMessages(ModId owner) => _mods.TryGetValue(owner, out var s) ? s.Messages : 0;
        public long ModLogical(ModId owner) => _mods.TryGetValue(owner, out var s) ? s.Logical : 0;
        public long ModTransport(ModId owner) => _mods.TryGetValue(owner, out var s) ? s.Transport : 0;
        public long ProtoBytes(ProtocolId pid) => _protos.TryGetValue(pid, out var s) ? s.Bytes : 0;

        /// <summary>按 owner 清理 Mod 级统计（Rule 20；Protocol/Connection 为历史累计，保留可观测性）。</summary>
        public void RemoveOwner(ModId owner) => _mods.Remove(owner);

        /// <summary>清空全部窗口统计（Stop 重启支持）。</summary>
        public void Clear()
        {
            _conns.Clear();
            _mods.Clear();
            _protos.Clear();
        }

        private ConnStat GetConn(int connectionId, long now)
        {
            if (_conns.TryGetValue(connectionId, out var s) && now - s.WinStart < WindowMs) return s;
            s = new ConnStat { WinStart = now };
            _conns[connectionId] = s;
            return s;
        }

        private ModStat GetMod(ModId owner, long now)
        {
            if (_mods.TryGetValue(owner, out var s) && now - s.WinStart < WindowMs) return s;
            s = new ModStat { WinStart = now };
            _mods[owner] = s;
            return s;
        }

        private ProtoStat GetProto(ProtocolId pid, long now)
        {
            if (_protos.TryGetValue(pid, out var s) && now - s.WinStart < WindowMs) return s;
            s = new ProtoStat { WinStart = now };
            _protos[pid] = s;
            return s;
        }
    }
}
