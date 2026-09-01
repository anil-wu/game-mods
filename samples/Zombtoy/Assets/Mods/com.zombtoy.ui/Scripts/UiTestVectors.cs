using System;
using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Ui
{
    /// <summary>
    /// com.zombtoy.ui 的契约测试向量回灌（ui CONTRACT.md §5）：本 Mod 是消费方、不发布数据，
    /// 故向量 = 各 owner 消息/能力行规范字节的**本地黄金副本**（不引用 owner 程序集，Rule 12）。
    ///
    /// 两类向量（summary §6）：
    /// - 消息载荷（字段编号形状）：new TestVector —— 本文件 UiWireBytes 复刻 owner 编码器字段布局产出规范字节；
    /// - DataCodec 形状（ModCall 能力行）：TestVector.Of —— 与 owner 的 TestVector.Of 同源（DataCodec），字节等价。
    ///
    /// 权威漂移校验在共享测试程序集（tests/TestRunner/ZombtoyUiTests.cs）：以 owner 实时向量
    /// （PlayerTestVectors.All() 等）喂入本 Mod 解析器（HudHealth.TryRead 等），与样例一致即
    /// "可重复定义未漂移"（§14.11.3）；owner 改布局（append-only 之外）应被检出。
    /// </summary>
    public static class UiTestVectors
    {
        // ---- 消费的消息契约名（对应各 owner CONTRACT.md 向量表） ----
        public const string PlayerHealthMsgContract = "player_health_msg";
        public const string PlayerStaminaMsgContract = "player_stamina_msg";
        public const string EnemyCountMsgContract = "enemy_count_msg";
        public const string ScoreChangedMsgContract = "score_changed_msg";
        public const string ScoreGameOverMsgContract = "score_gameover_msg";
        public const string WeaponAmmoMsgContract = "weapon_ammo_msg";
        public const string WeaponSwitchedMsgContract = "weapon_switched_msg";
        public const string GameStateMsgContract = "game_state_msg";
        public const string LeaderboardTopMsgContract = "leaderboard_top_msg";

        // ---- 消费的能力行契约名（对应 owner CONTRACT.md 向量表） ----
        public const string GameStatusRowContract = "game_status_row";
        public const string GameStartArgsContract = "game_start_args";
        public const string GameToMenuArgsContract = "game_to_menu_args";
        public const string GamePauseArgsContract = "game_pause_args";
        public const string GameResumeArgsContract = "game_resume_args";
        public const string PlayerGetStateRowContract = "player_get_state_row";
        public const string EnemyGetCountRowContract = "enemy_get_count_row";
        public const string WeaponGetStateRowContract = "weapon_get_state_row";
        public const string ScoreGetStateRowContract = "score_get_state_row";
        public const string LeaderboardRowContract = "leaderboard_row";
        public const string LeaderboardGetTopArgsContract = "leaderboard_gettop_args";

        /// <summary>全部消费方解析器向量（黄金副本；append-only：新向量只增不改）。</summary>
        public static TestVector[] All() => new[]
        {
            // ---- 消息载荷（UiWireBytes 复刻 owner 编码器字段布局，ui CONTRACT.md §2/§5） ----
            new TestVector(PlayerHealthMsgContract, new object?[] { 95, 100 },
                UiWireBytes.HealthChanged(95, 100).ToArray()),                       // player CONTRACT §7 样例 [95,100]
            new TestVector(PlayerStaminaMsgContract, new object?[] { 0.4f, 1f },
                UiWireBytes.StaminaChanged(0.4f, 1f).ToArray()),                     // player CONTRACT §7 样例 [0.4,1]
            new TestVector(EnemyCountMsgContract, new object?[] { 7 },
                UiWireBytes.ZombieCountChanged(7).ToArray()),                        // enemy CONTRACT §10 样例 [7]
            new TestVector(ScoreChangedMsgContract, new object?[] { 1200, 5000, 12 },
                UiWireBytes.ScoreChanged(1200, 5000, 12).ToArray()),                 // score CONTRACT §7 样例 [1200,5000,12]
            new TestVector(ScoreGameOverMsgContract, new object?[] { 1200, 12, true },
                UiWireBytes.GameOver(1200, 12, true).ToArray()),                     // score CONTRACT §7 样例 [1200,12,true]
            new TestVector(WeaponAmmoMsgContract, new object?[] { 0u, 25, 50, 25 },
                UiWireBytes.AmmoChanged(0u, 25, 50, 25).ToArray()),                  // weapon CONTRACT §10 样例 [0u,25,50,25]
            new TestVector(WeaponSwitchedMsgContract, new object?[] { 1u },
                UiWireBytes.WeaponSwitched(1u).ToArray()),                           // weapon CONTRACT §10 样例 [1u]
            new TestVector(GameStateMsgContract, new object?[] { 1u, 1u },
                UiWireBytes.StateChanged((byte)GamePhase.Playing, (byte)GameReason.Started).ToArray()), // 开局
            new TestVector(GameStateMsgContract, new object?[] { 0u, 5u },
                UiWireBytes.StateChanged((byte)GamePhase.Menu, (byte)GameReason.ToMenu).ToArray()),     // 回主菜单
            new TestVector(GameStateMsgContract, new object?[] { 3u, 4u },
                UiWireBytes.StateChanged((byte)GamePhase.GameOver, (byte)GameReason.PlayerDied).ToArray()), // 结算
            new TestVector(GameStateMsgContract, new object?[] { 2u, 2u },
                UiWireBytes.StateChanged((byte)GamePhase.Paused, (byte)GameReason.Paused).ToArray()),   // 暂停
            new TestVector(GameStateMsgContract, new object?[] { 1u, 3u },
                UiWireBytes.StateChanged((byte)GamePhase.Playing, (byte)GameReason.Resumed).ToArray()), // 恢复
            new TestVector(LeaderboardTopMsgContract,
                new object?[] { 2u, new object?[] { new object?[] { 1u, 5000, "alice" }, new object?[] { 2u, 3000, "bob" } } },
                UiWireBytes.TopChanged(new object?[] { new object?[] { 1u, 5000, "alice" }, new object?[] { 2u, 3000, "bob" } }).ToArray()),
            new TestVector(LeaderboardTopMsgContract, new object?[] { 0u, Array.Empty<object?[]>() },
                UiWireBytes.TopChanged(Array.Empty<object?[]>()).ToArray()),         // 空榜（leaderboard 契约 §6）

            // ---- 能力行（DataCodec 形状，与 owner TestVector.Of 同源字节等价） ----
            TestVector.Of(GameStatusRowContract, new object?[] { 1u }),              // game:status 返回 [phase]（QA/调试）
            TestVector.Of(GameStartArgsContract, Array.Empty<object?>()),            // game:start args []（MenuView 回调）
            TestVector.Of(GameToMenuArgsContract, Array.Empty<object?>()),           // game:to_menu args []（Result/暂停）
            TestVector.Of(GamePauseArgsContract, Array.Empty<object?>()),            // game:pause args []（Esc）
            TestVector.Of(GameResumeArgsContract, Array.Empty<object?>()),           // game:resume args []（继续）
            TestVector.Of(PlayerGetStateRowContract, new object?[] { 100, 100, 1f, 1f, true }),  // player:get_state 行（HUD 填充）
            TestVector.Of(EnemyGetCountRowContract, new object?[] { 7 }),            // enemy:get_count 行（HUD 填充）
            TestVector.Of(WeaponGetStateRowContract, new object?[] { 0u, 4u, 25, 50, 25 }),      // weapon:get_state 行（HUD 填充）
            TestVector.Of(ScoreGetStateRowContract, new object?[] { 1200, 5000, 12, false }),    // score:get_state 行（Result 填充）
            TestVector.Of(LeaderboardRowContract, new object?[] { 1u, 5000, "alice" }),          // 榜单行
            TestVector.Of(LeaderboardGetTopArgsContract, new object?[] { 10 }),      // leaderboard:get_top args [n]
        };
    }

    /// <summary>
    /// 本地线格式复刻（Rule 19：消费方各自写自己的；字段布局 = 各 owner CONTRACT.md，append-only 兼容）。
    /// 仅用于 UiTestVectors 黄金副本与消费方自校验——不发布，不出本 Mod 程序集。
    /// </summary>
    public static class UiWireBytes
    {
        /// <summary>player:HealthChanged v1：[1]=current(i32) [2]=max(i32)。</summary>
        public static PayloadBuffer HealthChanged(int current, int max)
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, current);
            w.WriteInt32(2, max);
            return w.ToBuffer();
        }

        /// <summary>player:StaminaChanged v1：[1]=current(f32) [2]=max(f32)。</summary>
        public static PayloadBuffer StaminaChanged(float current, float max)
        {
            var w = new PayloadWriter();
            w.WriteFloat(1, current);
            w.WriteFloat(2, max);
            return w.ToBuffer();
        }

        /// <summary>enemy:ZombieCountChanged v1：[1]=count(i32)。</summary>
        public static PayloadBuffer ZombieCountChanged(int count)
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, count);
            return w.ToBuffer();
        }

        /// <summary>score:ScoreChanged v1：[1]=current(i32) [2]=highScore(i32) [3]=kills(i32)。</summary>
        public static PayloadBuffer ScoreChanged(int current, int highScore, int kills)
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, current);
            w.WriteInt32(2, highScore);
            w.WriteInt32(3, kills);
            return w.ToBuffer();
        }

        /// <summary>score:GameOver v1：[1]=finalScore(i32) [2]=kills(i32) [3]=isNewHigh(bool)。</summary>
        public static PayloadBuffer GameOver(int finalScore, int kills, bool isNewHigh)
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, finalScore);
            w.WriteInt32(2, kills);
            w.WriteBool(3, isNewHigh);
            return w.ToBuffer();
        }

        /// <summary>weapon:AmmoChanged v1：[1]=slot(u32) [2]=inMag(i32) [3]=reserve(i32) [4]=maxMag(i32)。</summary>
        public static PayloadBuffer AmmoChanged(uint slot, int inMag, int reserve, int maxMag)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, slot);
            w.WriteInt32(2, inMag);
            w.WriteInt32(3, reserve);
            w.WriteInt32(4, maxMag);
            return w.ToBuffer();
        }

        /// <summary>weapon:WeaponSwitched v1：[1]=slot(u32)。</summary>
        public static PayloadBuffer WeaponSwitched(uint slot)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, slot);
            return w.ToBuffer();
        }

        /// <summary>game:StateChanged v1：[1]=phase(u32) [2]=reason(u32)。</summary>
        public static PayloadBuffer StateChanged(byte phase, byte reason)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, phase);
            w.WriteUInt32(2, reason);
            return w.ToBuffer();
        }

        /// <summary>leaderboard:TopChanged v1：[1]=count(u32) [2]=rows(bytes 嵌套，DataCodec 行集，每行 [rank u32, score i32, name string])。</summary>
        public static PayloadBuffer TopChanged(object?[] rows)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, (uint)rows.Length);
            w.WriteBytes(2, DataCodec.Write(rows).ToArray());
            return w.ToBuffer();
        }
    }
}
