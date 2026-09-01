using System;
using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Score
{
    /// <summary>
    /// 分数系统（契约 §2：无周期系统——逻辑在消息 handler + 能力实现内，全部可无头测试；不注册 ECS System）。
    /// ScoreSystem：订阅 enemy:EnemyKilled 累加计分 + 击杀数 + 最高分更新/持久化 + 发 score:ScoreChanged。
    /// 连击（config comboMultiplier=2）：v1 不实现（契约 §2：原版仅配置未生效），Combo 字段预埋待 append-only 扩展。
    /// 消费方解析（Rule 19）：enemy:EnemyKilled 的字段布局各自重复定义，不引用 enemy 程序集（Rule 12）。
    /// </summary>
    public static class ScoreSystem
    {
        /// <summary>
        /// enemy:EnemyKilled 消费方解析器（契约 enemy §5：[1]=enemyType(u32) [2]=scoreValue(i32) [3..5]=xyz(f32)）。
        /// score 只需字段 1/2（计分入口，契约 score §2 OnEnemyKilled）；xyz 为死亡特效位置，本 Mod 不消费。
        /// </summary>
        public static bool TryReadEnemyKilled(in PayloadReader reader, out uint enemyType, out int scoreValue)
        {
            enemyType = 0;
            scoreValue = 0;
            if (!reader.TryReadUInt32(1, out enemyType)) return false;
            if (!reader.TryReadInt32(2, out scoreValue)) return false;
            return true;
        }

        /// <summary>
        /// 订阅处理：击杀计分（契约 §2 OnEnemyKilled）。
        /// Current += scoreValue（字段 2：100 普通 / 500 首领，enemy 侧按 EnemyConfig 携带）；
        /// Kills++；IsNewHigh = Current > HighScore（更新 HighScore 并持久化，契约 §8）；发 score:ScoreChanged。
        /// </summary>
        public static void OnEnemyKilled(in MessageEnvelope envelope, PayloadReader reader)
        {
            if (!TryReadEnemyKilled(in reader, out _, out var scoreValue)) return; // 未知字段/类型不符 → 忽略（防御）
            if (scoreValue <= 0) return;                                          // 防御：零/负分不计
            var mod = ScoreMod.Context;
            var world = ScoreMod.World;
            if (mod is null || world is null || ScoreMod.ScoreEntityId == 0) return; // 已卸载（防御竞态）
            var e = new Entity(ScoreMod.ScoreEntityId);
            if (!world.TryGet<ScoreState>(e, out var state)) return;

            state.Current += scoreValue;
            state.Kills++;
            if (state.Current > state.HighScore)
            {
                state.HighScore = state.Current;
                state.IsNewHigh = true;
                ScorePersistence.Current.SaveHighScore(state.HighScore); // 最高分持久化（契约 §8）
            }
            else
            {
                state.IsNewHigh = false;
            }
            world.Add(e, state);
            ScoreMod.PublishScoreChanged(mod, state);
        }
    }

    /// <summary>
    /// 结算系统（契约 §2：无周期系统——订阅 player:Died 触发结算，全部可无头测试）。
    /// 结算链路（summary §5.2）：player:Died → 结算（写 RunResult、发 score:GameOver、调 leaderboard:submit）→
    /// game 订阅 GameOver 收尾。
    /// 消费方解析（Rule 19）：player:Died 的字段布局各自重复定义，不引用 player 程序集（Rule 12）。
    /// </summary>
    public static class GameOverSystem
    {
        /// <summary>player:Died 消费方解析器（契约 player §4：[1]=source(u32)，伤害来源实体；本 Mod 只作触发信号，不消费 source）。</summary>
        public static bool TryReadPlayerDied(in PayloadReader reader, out uint source)
        {
            source = 0;
            return reader.TryReadUInt32(1, out source);
        }

        /// <summary>
        /// 订阅处理：玩家死亡结算（契约 §2 OnPlayerDied）。
        /// 若未结算 → 写 RunResult（FinalScore/Kills/IsNewHigh/Finalized=true）、发 score:GameOver、
        /// 调 leaderboard:submit([FinalScore, PlayerName])（二进制 ModCall，Rule 14）；失败不影响结算流程（契约 §6）。
        /// </summary>
        public static void OnPlayerDied(in MessageEnvelope envelope, PayloadReader reader)
        {
            _ = TryReadPlayerDied(in reader, out _); // 解析校验（未知字段/类型不符 → source=0，仍按结算处理）
            var mod = ScoreMod.Context;
            var world = ScoreMod.World;
            if (mod is null || world is null || ScoreMod.ScoreEntityId == 0) return; // 已卸载（防御竞态）
            var e = new Entity(ScoreMod.ScoreEntityId);
            if (!world.TryGet<ScoreState>(e, out var state)) return;
            // 已结算 → 忽略（防重复结算：player:Died 只发一次，防御重复订阅/消息重放）
            if (world.TryGet<RunResult>(e, out var existing) && existing.Finalized) return;

            world.Add(e, new RunResult
            {
                FinalScore = state.Current,
                Kills = state.Kills,
                IsNewHigh = state.IsNewHigh,
                Finalized = true,
            });

            // 发 score:GameOver（契约 §4：finalScore/kills/isNewHigh；game 收尾 + ui Result 订阅）
            var buffer = ScoreMessagePayload.GameOver(state.Current, state.Kills, state.IsNewHigh);
            mod.Messages.Publish(ScoreMod.GameOverEvent, 1, in buffer);

            // 调 leaderboard:submit（契约 §6/§2；异步编排在 leaderboard 侧，本端同步入队立即返回）
            SubmitLeaderboard(mod, state.Current);
        }

        /// <summary>
        /// 调 leaderboard:submit([FinalScore, PlayerName])（契约 §6：提交最高分，PlayerName 用默认名）。
        /// 失败静默记日志、不影响游戏流程（契约 leaderboard §2；leaderboard 未加载/拒绝/异常都走 catch）。
        /// </summary>
        private static void SubmitLeaderboard(IModContext mod, int finalScore)
        {
            try
            {
                var buf = mod.Mods.Call(ScoreMod.LeaderboardModId, ScoreMod.LeaderboardSubmitCap,
                    DataCodec.Write(new object?[] { finalScore, ScoreConfig.PlayerName }));
                var row = DataCodec.Read(new PayloadReader(buf));
                if (row.Length == 0 || row[0] is not bool accepted)
                {
                    mod.Log.Warn($"[score] leaderboard:submit 返回异常（score={finalScore}），静默忽略");
                    return;
                }
                if (!accepted)
                    mod.Log.Warn($"[score] leaderboard:submit 拒绝（score={finalScore}），静默忽略"); // 契约 leaderboard §2：score<=0 拒绝
            }
            catch (Exception e)
            {
                // leaderboard 未加载/卸载竞态等：结算不受影响（Rule 12 通道纪律，失败降级）
                mod.Log.Warn($"[score] leaderboard:submit 调用异常: {e.Message}");
            }
        }
    }
}
