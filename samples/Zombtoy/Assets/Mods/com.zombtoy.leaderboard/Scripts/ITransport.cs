using System;

namespace Com.Zombtoy.Leaderboard
{
    /// <summary>
    /// 榜单条目（传输层纯数据，CONTRACT.md §1）。后端 v1 只存分数串，玩家名默认
    /// LeaderboardConfig.PlayerName（"player"）；MemoryTransport 测试可注入自定义名。
    /// </summary>
    public readonly struct ScoreEntry
    {
        public readonly int Score;
        public readonly string Name;

        public ScoreEntry(int score, string name)
        {
            Score = score;
            Name = name ?? "";
        }

        public override string ToString() => $"{Score}:{Name}";
    }

    /// <summary>
    /// 高分榜传输抽象（CONTRACT.md §1）：同步原语 Submit/GetTop（失败返回 false / 空表，不抛），
    /// 异步编排（HTTP 在后台线程执行、完成回主线程发布）由 Mod 内部完成（§12.11 同步 ModCall 约束）。
    ///
    /// 可无头测试的关键：无头测试注入 MemoryTransport；Unity 侧默认 HttpLeaderboardTransport（ServerUrl 可配）。
    /// </summary>
    public interface ILeaderboardTransport
    {
        /// <summary>提交分数。失败（网络错误等）返回 false。</summary>
        bool Submit(int score, string playerName);

        /// <summary>返回最多 n 条 [score, name]，按分数降序。</summary>
        ScoreEntry[] GetTop(int n);
    }

    /// <summary>
    /// 静态桥（Mod 内部状态，同 FPS 先例 PlayerMod.World / modstore 的 Service 静态字段）：
    /// 测试注入 Fake 实现（MemoryTransport），无头测试不依赖真实后端；Unity 侧默认 HttpLeaderboardTransport。
    /// 本类属于 com.zombtoy.leaderboard 内部状态，不跨 Mod 共享（Rule 12 不触线）。
    /// </summary>
    public static class LeaderboardTransport
    {
        // 默认实现：HttpLeaderboardTransport（契约 §1 "默认实现"，ServerUrl 可配）
        private static ILeaderboardTransport _instance = new HttpLeaderboardTransport(
            LeaderboardConfig.ServerUrl, LeaderboardConfig.TimeoutMs);

        public static ILeaderboardTransport Current => _instance;

        /// <summary>注入传输实现（测试 / Unity 宿主装配）。</summary>
        public static void Set(ILeaderboardTransport transport)
            => _instance = transport ?? throw new ArgumentNullException(nameof(transport));
    }
}
