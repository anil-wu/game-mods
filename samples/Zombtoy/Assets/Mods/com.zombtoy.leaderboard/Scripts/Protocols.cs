using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Leaderboard
{
    /// <summary>
    /// 行为参数（CONTRACT.md §7 LeaderboardConfig 常量表）。
    /// ServerUrl 默认对齐原版 Leaderboard scoreStorage（http://localhost:3000，M3.2 后端地址）；
    /// Unity 宿主装配时可用 HttpLeaderboardTransport(serverUrl, timeoutMs) 覆盖。
    /// </summary>
    public static class LeaderboardConfig
    {
        /// <summary>后端地址（POST /addScore、GET /getAllScores）。</summary>
        public const string ServerUrl = "http://localhost:3000";

        /// <summary>HTTP 超时（防御性默认）。</summary>
        public const int TimeoutMs = 5000;

        /// <summary>getTop 上限（refresh 拉取条数）。</summary>
        public const int MaxEntries = 20;

        /// <summary>默认玩家名（v1 无玩家名输入）。</summary>
        public const string PlayerName = "player";
    }

    /// <summary>
    /// 能力 ABI 形状（Rule 19 进程内务实形态）：跨 Mod 参数/返回只用 BCL 纯数据——
    /// 基元 + object?[] 元组（字段布局即契约，CONTRACT.md §2/§6），双方各自按文档解析；
    /// 跨边界经 DataCodec 编解码（Rule 14）。本类内的 TryRead 供 owner 自校验与测试参考；
    /// 消费方（score.ScoreSystem 调 submit、ui.ResultView 调 get_top/解析 TopChanged）各自重复定义（Rule 19）。
    /// </summary>
    public static class CapabilityShapes
    {
        // ---- 本 Mod 导出能力入参构建（owner 编码，测试向量/内部调用） ----

        /// <summary>leaderboard:submit 入参：[0]=score(i32) [1]=playerName(string)。</summary>
        public static object?[] SubmitArgs(int score, string playerName)
            => new object?[] { score, playerName };

        /// <summary>leaderboard:get_top 入参：[0]=n(i32)。</summary>
        public static object?[] GetTopArgs(int n) => new object?[] { n };

        /// <summary>leaderboard:get_top 行 / TopChanged.rows 嵌套行：[0]=rank(u32) [1]=score(i32) [2]=name(string)。</summary>
        public static object?[] Row(uint rank, int score, string name)
            => new object?[] { rank, score, name };

        // ---- 本 Mod 能力入参解析（owner 自校验） ----

        /// <summary>leaderboard:submit 入参解析。playerName 缺省 → 默认名（契约 §7 PlayerName，append-only 防御）。</summary>
        public static bool TryReadSubmitArgs(object? args, out int score, out string playerName)
        {
            score = 0;
            playerName = LeaderboardConfig.PlayerName;
            if (args is not object[] a || a.Length < 1) return false;
            if (a[0] is not int s) return false;
            score = s;
            if (a.Length >= 2 && a[1] is string n && n.Length > 0) playerName = n;
            return true;
        }

        /// <summary>leaderboard:get_top 入参解析。</summary>
        public static bool TryReadGetTopArgs(object? args, out int n)
        {
            n = 0;
            if (args is not object[] a || a.Length < 1) return false;
            if (a[0] is not int v) return false;
            n = v;
            return true;
        }

        /// <summary>leaderboard:get_top 行解析（消费方：ui.ResultView / LeaderboardRows）。</summary>
        public static bool TryReadRow(object? row, out uint rank, out int score, out string name)
        {
            rank = 0; score = 0; name = "";
            if (row is not object[] a || a.Length < 3) return false;
            if (a[0] is not uint r || a[1] is not int s || a[2] is not string n) return false;
            rank = r; score = s; name = n;
            return true;
        }
    }

    /// <summary>
    /// 本 Mod 发布消息的载荷编码器（字段编号线格式，CONTRACT.md §3；summary §0.3）。
    /// owner 唯一事实来源：能力实现发布与测试向量（LeaderboardTestVectors）共用，
    /// 保证"向量样例字节 = 线上字节"（§14.11.3）。
    /// </summary>
    public static class LeaderboardMessagePayload
    {
        /// <summary>
        /// leaderboard:TopChanged v1：[1]=count(u32 varint) [2]=rows(bytes length-delimited)，
        /// rows = DataCodec 编码的嵌套数组（每行 [rank u32, score i32, name string]，CONTRACT.md §3）。
        /// 消费方（ui.ResultView）自解析：PayloadReader 读 field1 + TryReadView(field2) → DataCodec 解码行集（Rule 19）。
        /// </summary>
        public static PayloadBuffer TopChanged(ScoreEntry[] entries)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, (uint)entries.Length);
            var rows = new object?[entries.Length];
            for (var i = 0; i < entries.Length; i++)
                rows[i] = CapabilityShapes.Row((uint)(i + 1), entries[i].Score, entries[i].Name);
            w.WriteBytes(2, DataCodec.Write(rows).ToArray());
            return w.ToBuffer();
        }
    }
}
