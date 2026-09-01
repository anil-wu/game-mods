using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

namespace Com.Zombtoy.Leaderboard
{
    /// <summary>
    /// 默认 HTTP 传输（CONTRACT.md §1）：HttpClient → ServerUrl（默认 http://localhost:3000，原版 Leaderboard scoreStorage）。
    ///
    /// 后端契约（沿用原项目 ZombtoyBackend，samples/Zombtoy/Backend/）：
    ///   POST {ServerUrl}/addScore（body 纯文本分数，如 "100"，或 {"score":"100"}）→ 存储；
    ///   GET  {ServerUrl}/getAllScores → 逗号分隔分数串（v1 后端只存分数，玩家名由本 Mod 补默认名）。
    ///
    /// netstandard2.1 兼容写法（System.Net.Http，Unity 桌面平台可用）；接口为同步原语（ITransport.cs），
    /// 内部 sync-over-async（ConfigureAwait(false)，后台线程执行，无死锁）。
    /// 失败防御：Submit 返回 false / GetTop 返回空表，绝不抛到调用方（Mod 内静默记日志，契约 §2）。
    /// </summary>
    public sealed class HttpLeaderboardTransport : ILeaderboardTransport
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;

        /// <param name="serverUrl">后端根地址（不带尾斜杠处理）.</param>
        /// <param name="timeoutMs">HTTP 超时（毫秒；&lt;=0 用 LeaderboardConfig.TimeoutMs）。</param>
        public HttpLeaderboardTransport(string serverUrl, int timeoutMs)
        {
            _baseUrl = (serverUrl ?? "").TrimEnd('/');
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs <= 0 ? LeaderboardConfig.TimeoutMs : timeoutMs),
            };
        }

        /// <summary>默认装配：LeaderboardConfig 常量表（契约 §7）。</summary>
        public HttpLeaderboardTransport()
            : this(LeaderboardConfig.ServerUrl, LeaderboardConfig.TimeoutMs)
        {
        }

        /// <summary>当前后端根地址（诊断/日志）。</summary>
        public string BaseUrl => _baseUrl;

        /// <summary>POST {ServerUrl}/addScore（body 纯文本分数）→ 2xx 视为成功。</summary>
        public bool Submit(int score, string playerName)
        {
            try
            {
                using var content = new StringContent(score.ToString(), Encoding.UTF8, "text/plain");
                var resp = _http.PostAsync($"{_baseUrl}/addScore", content).GetAwaiter().GetResult();
                return resp.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false; // 网络错误等 → false（契约 §1）
            }
        }

        /// <summary>GET {ServerUrl}/getAllScores → 逗号分隔分数串 → 降序取前 n 条（玩家名补默认名）。失败 → 空表。</summary>
        public ScoreEntry[] GetTop(int n)
        {
            try
            {
                var text = _http.GetStringAsync($"{_baseUrl}/getAllScores").GetAwaiter().GetResult();
                var scores = ParseScores(text);
                var count = Math.Min(n < 0 ? 0 : n, scores.Count);
                var result = new ScoreEntry[count];
                for (var i = 0; i < count; i++)
                    result[i] = new ScoreEntry(scores[i], LeaderboardConfig.PlayerName);
                return result;
            }
            catch (Exception)
            {
                return Array.Empty<ScoreEntry>(); // 失败 → 空表（契约 §2：refresh 失败发布空表）
            }
        }

        /// <summary>解析逗号分隔分数串为 int 列表，降序（对齐原版 sortScoreArray：Sort + Reverse）。</summary>
        private static List<int> ParseScores(string text)
        {
            var list = new List<int>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var part in text.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0 && int.TryParse(trimmed, out var v))
                    list.Add(v);
            }
            list.Sort();
            list.Reverse();
            return list;
        }
    }
}
