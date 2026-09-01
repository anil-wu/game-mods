using System;
using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Score
{
    /// <summary>
    /// com.zombtoy.score 的契约测试向量（§14.11.3）：随版本发布，供消费方
    /// （game.GameFlow 调 reset、ui.ResultView 调 get_state / 解析 ScoreChanged/GameOver）CI 校验
    /// 各自重复定义的解析器未与本 Mod 的字节布局漂移。
    ///
    /// 两类向量（契约 §7）：
    /// - DataCodec 形状（ModCall args/rows）：TestVector.Of —— owner 编码器（DataCodec）产出规范字节；
    /// - 消息载荷（字段编号形状）：手工 new TestVector —— owner 消息编码器（ScoreMessagePayload）产出规范字节。
    /// append-only：新向量只增不改。
    /// </summary>
    public static class ScoreTestVectors
    {
        // DataCodec 形状契约名（对应 CONTRACT.md §3 能力 args/返回 / §7）
        public const string ResetArgsContract = "score_reset_args";
        public const string GetStateRowContract = "score_get_state_row";

        // 消息载荷契约名（对应 CONTRACT.md §4 / §7）
        public const string ScoreChangedMsgContract = "score_changed_msg";
        public const string GameOverMsgContract = "score_gameover_msg";

        /// <summary>全部测试向量（常规/边界；append-only）。</summary>
        public static TestVector[] All() => new[]
        {
            // ---- ModCall 形状（DataCodec 规范字节） ----
            TestVector.Of(ResetArgsContract, CapabilityShapes.ResetArgs()), // 契约 §7 样例 []

            // get_state 行：[score, highScore, kills, isNewHigh]
            TestVector.Of(GetStateRowContract, CapabilityShapes.GetStateRow(1200, 5000, 12, false)), // 契约 §7 样例
            TestVector.Of(GetStateRowContract, CapabilityShapes.GetStateRow(500, 5000, 5, true)),    // 新纪录边界

            // ---- 消息载荷（owner 消息编码器产出规范字节，消费方用 PayloadReader 解析） ----
            // score:ScoreChanged：[1]=current(i32) [2]=highScore(i32) [3]=kills(i32)
            new TestVector(ScoreChangedMsgContract, new object?[] { 1200, 5000, 12 },
                ScoreMessagePayload.ScoreChanged(1200, 5000, 12).ToArray()), // 契约 §7 样例
            new TestVector(ScoreChangedMsgContract, new object?[] { 0, 5000, 0 },
                ScoreMessagePayload.ScoreChanged(0, 5000, 0).ToArray()),     // reset 发布

            // score:GameOver：[1]=finalScore(i32) [2]=kills(i32) [3]=isNewHigh(bool)
            new TestVector(GameOverMsgContract, new object?[] { 1200, 12, true },
                ScoreMessagePayload.GameOver(1200, 12, true).ToArray()),     // 契约 §7 样例
            new TestVector(GameOverMsgContract, new object?[] { 500, 5, false },
                ScoreMessagePayload.GameOver(500, 5, false).ToArray()),      // 非新纪录
        };
    }
}
