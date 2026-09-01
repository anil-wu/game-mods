using System;
using System.Collections.Generic;

namespace Com.Zombtoy.Leaderboard
{
    /// <summary>
    /// 内存传输（CONTRACT.md §1）：本地列表存储，GetTop 降序截取——供无头测试注入
    /// （Fake 实现，不依赖真实后端）。线程安全（Submit/GetTop 可能被 Mod 的后台任务线程调用，lock 保护）；
    /// 可模拟失败（FailSubmit/FailGetTop）供失败路径测试（契约 §2：submit 失败返回 false、refresh 失败发布空表）。
    /// </summary>
    public sealed class MemoryTransport : ILeaderboardTransport
    {
        private readonly object _gate = new();
        private readonly List<ScoreEntry> _entries = new();

        /// <summary>模拟网络失败：Submit 返回 false（不落库）。</summary>
        public bool FailSubmit { get; set; }

        /// <summary>模拟网络失败：GetTop 返回空表。</summary>
        public bool FailGetTop { get; set; }

        /// <summary>Submit 成功调用次数（测试断言异步提交确实发生）。</summary>
        public int SubmitCount { get; private set; }

        /// <summary>GetTop 调用次数（测试断言异步刷新确实发生）。</summary>
        public int GetTopCount { get; private set; }

        /// <summary>Submit 未带名时的默认玩家名（默认 LeaderboardConfig.PlayerName）。</summary>
        public string DefaultName { get; set; } = LeaderboardConfig.PlayerName;

        public bool Submit(int score, string playerName)
        {
            lock (_gate)
            {
                SubmitCount++;
                if (FailSubmit) return false;
                _entries.Add(new ScoreEntry(score, string.IsNullOrEmpty(playerName) ? DefaultName : playerName));
                return true;
            }
        }

        public ScoreEntry[] GetTop(int n)
        {
            lock (_gate)
            {
                GetTopCount++;
                if (FailGetTop) return Array.Empty<ScoreEntry>();
                if (n <= 0) return Array.Empty<ScoreEntry>();
                var sorted = new List<ScoreEntry>(_entries);
                sorted.Sort((a, b) => b.Score.CompareTo(a.Score)); // 降序（对齐原版 sortScoreArray）
                var count = Math.Min(n, sorted.Count);
                var result = new ScoreEntry[count];
                for (var i = 0; i < count; i++) result[i] = sorted[i];
                return result;
            }
        }

        /// <summary>测试种子：直接注入条目（等价于后端已有分数）。</summary>
        public void Seed(int score, string name)
        {
            lock (_gate) _entries.Add(new ScoreEntry(score, name));
        }

        /// <summary>当前条目数（测试断言）。</summary>
        public int EntryCount
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <summary>全部条目快照（按提交顺序，测试断言）。</summary>
        public ScoreEntry[] Snapshot()
        {
            lock (_gate) return _entries.ToArray();
        }
    }
}
