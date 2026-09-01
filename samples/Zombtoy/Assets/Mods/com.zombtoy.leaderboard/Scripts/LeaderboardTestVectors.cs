using System;
using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Leaderboard
{
    /// <summary>
    /// com.zombtoy.leaderboard 的契约测试向量（§14.11.3）：随版本发布，供消费方
    /// （score.ScoreSystem 调 submit、ui.ResultView 调 get_top / 解析 TopChanged.rows）CI 校验
    /// 各自重复定义的解析器未与本 Mod 的字节布局漂移。
    ///
    /// 两类向量（契约 §6）：
    /// - DataCodec 形状（ModCall args/rows）：TestVector.Of —— owner 编码器（DataCodec）产出规范字节；
    /// - 消息载荷（字段编号形状）：手工 new TestVector —— owner 消息编码器（LeaderboardMessagePayload）
    ///   产出规范字节（field2 嵌套数组用 DataCodec 编码）。
    /// append-only：新向量只增不改。
    /// </summary>
    public static class LeaderboardTestVectors
    {
        // DataCodec 形状契约名（对应 CONTRACT.md §2 能力 args / §6 行）
        public const string SubmitArgsContract = "leaderboard_submit_args";
        public const string GetTopArgsContract = "leaderboard_gettop_args";
        public const string RowContract = "leaderboard_row";

        // 消息载荷契约名（对应 CONTRACT.md §3 / §6）
        public const string TopMsgContract = "leaderboard_top_msg";

        /// <summary>全部测试向量（常规/边界；append-only）。</summary>
        public static TestVector[] All() => new[]
        {
            // ---- ModCall 形状（DataCodec 规范字节） ----
            TestVector.Of(SubmitArgsContract, CapabilityShapes.SubmitArgs(1200, "player1")),
            TestVector.Of(SubmitArgsContract, CapabilityShapes.SubmitArgs(0, "player")), // score<=0 边界
            TestVector.Of(GetTopArgsContract, CapabilityShapes.GetTopArgs(10)),
            TestVector.Of(GetTopArgsContract, CapabilityShapes.GetTopArgs(50)),         // 上限钳制边界
            TestVector.Of(RowContract, CapabilityShapes.Row(1u, 5000, "alice")),
            TestVector.Of(RowContract, CapabilityShapes.Row(2u, 3000, "bob")),

            // ---- 消息载荷（owner 消息编码器产出规范字节，消费方用 PayloadReader 解析） ----
            new TestVector(TopMsgContract, new object?[] { 2u, new object?[] { new object?[] { 1u, 5000, "alice" }, new object?[] { 2u, 3000, "bob" } } },
                LeaderboardMessagePayload.TopChanged(new[]
                {
                    new ScoreEntry(5000, "alice"),
                    new ScoreEntry(3000, "bob"),
                }).ToArray()),
            new TestVector(TopMsgContract, new object?[] { 0u, Array.Empty<object?[]>() },
                LeaderboardMessagePayload.TopChanged(Array.Empty<ScoreEntry>()).ToArray()),
        };
    }
}
