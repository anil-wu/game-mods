using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Leaderboard
{
    /// <summary>
    /// 高分榜 Mod（com.zombtoy.leaderboard）：.NET 8 后端客户端（POST /addScore、GET /getAllScores）。单机 Host。
    /// 无 ECS 组件/系统（纯服务 Mod，契约 §0）：跨 Mod 只导出能力（submit/get_top/refresh）+ 发布消息（TopChanged）。
    ///
    /// 异步约束（契约 §0）：HTTP 是异步的，但 ModCall 是同步的（§12.11）——submit/refresh 只入队立即返回
    /// accepted/ok，实际请求在后台线程（Task.Run）执行；完成时经 SynchronizationContext 回主线程发布
    /// leaderboard:TopChanged 供 UI 刷新（Unity 主线程安全，同 modstore ModStoreView 先例）。get_top 返回最近一次缓存。
    ///
    /// 传输抽象（契约 §1）：ILeaderboardTransport + 静态桥 LeaderboardTransport.Set 注入——无头测试用
    /// MemoryTransport，Unity 侧默认 HttpLeaderboardTransport（ServerUrl 可配）。
    /// </summary>
    public sealed class LeaderboardMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.zombtoy.leaderboard");

        // 导出能力（契约 §2；owner = com.zombtoy.leaderboard）
        public static readonly CapabilityId SubmitCap = new(ModIdValue, "submit");
        public static readonly CapabilityId GetTopCap = new(ModIdValue, "get_top");
        public static readonly CapabilityId RefreshCap = new(ModIdValue, "refresh");

        // 发布消息（契约 §3；谁定义谁发布，Rule 11）
        public static readonly MessageId TopChangedEvent = new(ModIdValue, "TopChanged");

        // 静态桥（Mod 内部状态，同 FPS 先例 PlayerMod.World；测试/宿主访问，Rule 12 不跨 Mod）
        public static IModContext? Context { get; private set; }

        /// <summary>最近一次榜单缓存（get_top 同步返回；refresh 完成后整体替换，volatile 原子引用）。</summary>
        private static volatile ScoreEntry[] _cache = Array.Empty<ScoreEntry>();

        public void Register(IModContext context)
        {
            Context = context;

            // 装配传输（契约 §1）：默认 HttpLeaderboardTransport（LeaderboardConfig.ServerUrl 可配）；
            // 测试经 LeaderboardTransport.Set 注入 MemoryTransport（无头测试不依赖真实后端）。
            var transport = LeaderboardTransport.Current;
            context.Log.Info($"leaderboard 传输已装配: {transport.GetType().Name}");

            // 能力导出（Rule 14 二进制 ABI：DataCodec 编解码纯数据，跨边界零对象引用）
            context.Mods.Export(SubmitCap, reader => DataCodec.Write(Submit(DataCodec.Read(reader))));
            context.Mods.Export(GetTopCap, reader => DataCodec.Write(GetTop(DataCodec.Read(reader))));
            context.Mods.Export(RefreshCap, _ => DataCodec.Write(Refresh()));

            context.Log.Info($"高分榜 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            // 对称撤销（Rule 20）：能力/消息由 Core 按 ModObject 归属强制回收（§9.2/§13.5）；
            // 本 Mod 只清静态桥与本地缓存。传输是外部注入对象（不属于 Mod 资源），不销毁。
            _cache = Array.Empty<ScoreEntry>();
            Context = null;
            context.Log.Info($"高分榜 Mod '{context.Info.Id}' 已注销");
        }

        // ---- 能力实现（契约 §2） ----

        /// <summary>leaderboard:submit：args=[score i32, playerName string] → [accepted bool]。
        /// score&lt;=0 → [false]；入队异步提交，立即 [true]；失败静默记日志（不影响游戏流程）。</summary>
        private static object?[] Submit(object? args)
        {
            if (!CapabilityShapes.TryReadSubmitArgs(args, out var score, out var name))
                return new object?[] { false };
            if (score <= 0) return new object?[] { false }; // 契约 §2
            EnqueueSubmit(score, name);
            return new object?[] { true };
        }

        /// <summary>leaderboard:get_top：args=[n i32]（1..50 越界钳制）→ 行集 [rank u32, score i32, name string]；
        /// 返回最近一次缓存（契约 §0），无缓存 → []。</summary>
        private static object?[] GetTop(object? args)
        {
            if (!CapabilityShapes.TryReadGetTopArgs(args, out var n))
                return Array.Empty<object?>();
            n = Math.Clamp(n, 1, 50); // 契约 §2：n 越界钳制（LeaderboardConfig.MaxEntries=20 为 refresh 拉取上限）
            var cache = _cache;
            var rows = new List<object?[]>();
            var count = Math.Min(n, cache.Length);
            for (var i = 0; i < count; i++)
                rows.Add(CapabilityShapes.Row((uint)(i + 1), cache[i].Score, cache[i].Name));
            return rows.ToArray();
        }

        /// <summary>leaderboard:refresh：args=[] → [ok bool]；入队异步 GetTop(MaxEntries) → 更新缓存 →
        /// 发布 leaderboard:TopChanged；失败发布空表（UI 显示"加载失败"）。</summary>
        private static object?[] Refresh()
        {
            EnqueueRefresh();
            return new object?[] { true };
        }

        // ---- 异步编排（契约 §0 / §12.11：ModCall 同步、HTTP 异步） ----

        /// <summary>入队异步提交：后台线程执行 transport.Submit，失败静默记日志（不发布任何消息）。</summary>
        private static void EnqueueSubmit(int score, string name)
        {
            var mod = Context;
            if (mod is null) return; // 已卸载：丢弃（防御）
            var transport = LeaderboardTransport.Current;
            Task.Run(() =>
            {
                bool ok;
                try { ok = transport.Submit(score, name); }
                catch (Exception e)
                {
                    mod.Log.Warn($"[leaderboard] submit 异常: {e.Message}");
                    return;
                }
                if (!ok)
                    mod.Log.Warn($"[leaderboard] submit 失败（score={score}），静默不影响流程"); // 契约 §2
            });
        }

        /// <summary>入队异步刷新：后台线程 transport.GetTop(MaxEntries) → 完成经同步上下文回主线程更新缓存 + 发布 TopChanged。</summary>
        private static void EnqueueRefresh()
        {
            var mod = Context;
            if (mod is null) return; // 已卸载：丢弃（防御）
            var transport = LeaderboardTransport.Current;
            // Unity 主线程调用时捕获 UnitySynchronizationContext → 完成回调回主线程发布（MessageBus 线程安全）；
            // 无头测试无同步上下文 → TaskScheduler.Default（线程池回调），测试 SpinUntil 等待。
            var ctx = SynchronizationContext.Current;
            var scheduler = ctx is null
                ? TaskScheduler.Default
                : TaskScheduler.FromCurrentSynchronizationContext();

            var task = Task.Run(() =>
            {
                ScoreEntry[] rows;
                try { rows = transport.GetTop(LeaderboardConfig.MaxEntries); }
                catch (Exception e)
                {
                    mod.Log.Warn($"[leaderboard] refresh 拉取异常: {e.Message}");
                    rows = Array.Empty<ScoreEntry>();
                }
                return rows;
            });
            task.ContinueWith(t =>
            {
                try
                {
                    // 失败（任务异常/取消）→ 空表（契约 §2：失败发布空表，UI 显示"加载失败"）
                    ApplyRefreshResult(mod, t.IsCompletedSuccessfully ? t.Result : Array.Empty<ScoreEntry>());
                }
                catch (Exception e)
                {
                    mod.Log.Warn($"[leaderboard] refresh 完成处理异常: {e.Message}");
                    ApplyRefreshResult(mod, Array.Empty<ScoreEntry>());
                }
            }, scheduler);
        }

        /// <summary>更新缓存并发布 leaderboard:TopChanged（契约 §3；在主线程/测试线程执行）。</summary>
        private static void ApplyRefreshResult(IModContext mod, ScoreEntry[] rows)
        {
            _cache = rows; // 更新缓存（get_top 同步读取最近一次结果）
            var buffer = LeaderboardMessagePayload.TopChanged(rows);
            mod.Messages.Publish(TopChangedEvent, 1, in buffer);
        }
    }
}
