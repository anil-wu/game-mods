using System;
using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Game
{
    /// <summary>
    /// com.zombtoy.game 的契约测试向量（§14.11.3）：随版本发布，供消费方
    /// （ui.MenuView 调 start、ui.ResultView/PauseView 调 to_menu/pause/resume、QA/调试调 status、
    /// 各 View 解析 StateChanged）CI 校验各自重复定义的解析器未与本 Mod 的字节布局漂移。
    ///
    /// 两类向量（契约 §9）：
    /// - DataCodec 形状（ModCall args/rows）：TestVector.Of —— owner 编码器（DataCodec）产出规范字节；
    /// - 消息载荷（字段编号形状）：手工 new TestVector —— owner 消息编码器（GameMessagePayload）产出规范字节。
    /// append-only：新向量只增不改。
    /// </summary>
    public static class GameTestVectors
    {
        // DataCodec 形状契约名（对应 CONTRACT.md §4 能力 args/返回 / §9）
        public const string StatusRowContract = "game_status_row";
        public const string StartArgsContract = "game_start_args";
        public const string ToMenuArgsContract = "game_to_menu_args";
        public const string PauseArgsContract = "game_pause_args";
        public const string ResumeArgsContract = "game_resume_args";

        // 消息载荷契约名（对应 CONTRACT.md §5 / §9）
        public const string StateChangedMsgContract = "game_state_msg";

        /// <summary>全部测试向量（常规/边界；append-only）。</summary>
        public static TestVector[] All() => new[]
        {
            // ---- ModCall 形状（DataCodec 规范字节） ----
            TestVector.Of(StatusRowContract, CapabilityShapes.StatusRow((byte)GamePhase.Playing)), // 契约 §9 样例 [1u]（QA/调试）
            TestVector.Of(StatusRowContract, CapabilityShapes.StatusRow((byte)GamePhase.Menu)),    // 初始相位边界

            TestVector.Of(StartArgsContract, CapabilityShapes.NoArgs()),   // 契约 §9 样例 []（ui.MenuView 回调侧）
            TestVector.Of(ToMenuArgsContract, CapabilityShapes.NoArgs()),  // 契约 §9 样例 []（ui.ResultView/PauseView）
            TestVector.Of(PauseArgsContract, CapabilityShapes.NoArgs()),   // 补充（ui.PauseView 暂停按钮）
            TestVector.Of(ResumeArgsContract, CapabilityShapes.NoArgs()),  // 补充（ui.PauseView 恢复按钮）

            // ---- 消息载荷（owner 消息编码器产出规范字节，消费方用 PayloadReader 解析） ----
            // game:StateChanged：[1]=phase(u32) [2]=reason(u32)
            new TestVector(StateChangedMsgContract, new object?[] { 1u, 1u },
                GameMessagePayload.StateChanged((byte)GamePhase.Playing, (byte)GameReason.Started).ToArray()),    // 契约 §9 样例 [1u,1u]（开局）
            new TestVector(StateChangedMsgContract, new object?[] { 0u, 5u },
                GameMessagePayload.StateChanged((byte)GamePhase.Menu, (byte)GameReason.ToMenu).ToArray()),        // 回主菜单
            new TestVector(StateChangedMsgContract, new object?[] { 3u, 4u },
                GameMessagePayload.StateChanged((byte)GamePhase.GameOver, (byte)GameReason.PlayerDied).ToArray()), // 结算收尾
            new TestVector(StateChangedMsgContract, new object?[] { 2u, 2u },
                GameMessagePayload.StateChanged((byte)GamePhase.Paused, (byte)GameReason.Paused).ToArray()),       // 暂停
            new TestVector(StateChangedMsgContract, new object?[] { 1u, 3u },
                GameMessagePayload.StateChanged((byte)GamePhase.Playing, (byte)GameReason.Resumed).ToArray()),     // 恢复
        };
    }
}
