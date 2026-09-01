using System;
using System.Threading;
using Com.Game.Core;
using Com.Zombtoy.Enemy;
using Com.Zombtoy.Leaderboard;
using Com.Zombtoy.Player;
using Com.Zombtoy.Score;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// com.zombtoy.score 无头测试（单机 Host 路径，契约 §2：无周期系统——消息 handler + 能力实现）：
    /// 击杀计分（普通 100 / 首领 500，分值由 enemy:EnemyKilled.scoreValue 携带）、连击配置（v1 不实现，
    /// 仅 config comboMultiplier=2 + Combo 字段预埋）、最高分更新与持久化（ScorePersistence 内存实现）、
    /// score:reset 清零（保留最高分）/ get_state 行、player:Died → 结算（score:GameOver + leaderboard:submit
    /// 二进制 ModCall）、消息发布字段解析、契约测试向量自校验（§14.11.3）、跨 owner 字节消费一致性
    /// （score 解析 enemy/player 消息向量）、卸载清理（Rule 20）。
    /// 消息路径：测试经 Host.MessageBus 以 owner（enemy/player）编码器发布真实线上字节（Rule 14）。
    /// 依赖链（契约 §5 mod.json）：core → player → enemy → leaderboard → score。
    /// </summary>
    public static class ZombtoyScoreTests
    {
        private static readonly ModId ScoreId = new("com.zombtoy.score");
        private static readonly ModId PlayerId = new("com.zombtoy.player");
        private static readonly ModId EnemyId = new("com.zombtoy.enemy");
        private static readonly ModId LeaderboardId = new("com.zombtoy.leaderboard");
        private static readonly ModId TestSubscriber = new("com.test.runner");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public World World => Host.World;
            public IModContext ScoreCtx = null!;
            public MemoryTransport Transport = null!;
            public IScorePersistence Persistence = null!;

            // 消费方视角订阅 score 消息（PayloadReader 字段解析，Rule 14；契约 §4）
            public int ScoreChangedEvents;
            public int LastCurrent, LastHighScore, LastKills;
            public int GameOverEvents;
            public int LastFinalScore, LastGameOverKills;
            public bool LastIsNewHigh;

            public static Fixture Create(IScorePersistence? persistence = null)
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };

                // 注入（先于 Mod 装配，契约 §8/leaderboard §1）：leaderboard 传输 + 最高分持久化
                f.Transport = new MemoryTransport();
                LeaderboardTransport.Set(f.Transport); // 类型名与字段名无冲突（字段为 Transport）
                f.Persistence = persistence ?? new MemoryScorePersistence();
                ScorePersistence.Set(f.Persistence);

                // 依赖（契约 §5）：core → player → enemy → leaderboard → score
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.core")), typeof(CoreMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.player")),
                    typeof(PlayerMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.player"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.enemy")),
                    typeof(EnemyMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.enemy"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.leaderboard")),
                    typeof(LeaderboardMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.leaderboard"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.score")),
                    typeof(ScoreMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.score"));
                f.ScoreCtx = f.Host.Manager.Get(ScoreId)!.Context;

                // 消费方订阅（契约 §4：ui 视角解析 ScoreChanged/GameOver）
                f.Host.Messages.Subscribe(TestSubscriber, ScoreMod.ScoreChangedEvent,
                    (in Game.Messaging.MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.ScoreChangedEvents++;
                        if (reader.TryReadInt32(1, out var cur)) f.LastCurrent = cur;
                        if (reader.TryReadInt32(2, out var high)) f.LastHighScore = high;
                        if (reader.TryReadInt32(3, out var kills)) f.LastKills = kills;
                    });
                f.Host.Messages.Subscribe(TestSubscriber, ScoreMod.GameOverEvent,
                    (in Game.Messaging.MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.GameOverEvents++;
                        if (reader.TryReadInt32(1, out var score)) f.LastFinalScore = score;
                        if (reader.TryReadInt32(2, out var kills)) f.LastGameOverKills = kills;
                        if (reader.TryReadBool(3, out var isNew)) f.LastIsNewHigh = isNew;
                    });
                return f;
            }

            public object?[] Call(CapabilityId cap, params object?[] args)
                => ModCall.Invoke(ScoreCtx.Mods, ScoreId, cap, args);

            public bool CallBool(CapabilityId cap, params object?[] args)
                => ModCall.InvokeBool(ScoreCtx.Mods, ScoreId, cap, args);

            public Entity ScoreEntity => new(ScoreMod.ScoreEntityId);

            public ScoreState State() => World.Get<ScoreState>(ScoreEntity);

            /// <summary>以 enemy owner 编码器发布 enemy:EnemyKilled（真实线上字节，契约 enemy §5）。</summary>
            public void KillEnemy(byte kind, int scoreValue)
            {
                var buf = EnemyMessagePayload.EnemyKilled(kind, scoreValue, 0f, 0f, 0f);
                Host.Messages.Publish(EnemyId, EnemyMod.EnemyKilledEvent, 1, in buf);
            }

            /// <summary>以 player owner 编码器发布 player:Died（真实线上字节，契约 player §4）。</summary>
            public void PlayerDied(uint source)
            {
                var buf = PlayerMessagePayload.Died(source);
                Host.Messages.Publish(PlayerId, PlayerMod.DiedEvent, 1, in buf);
            }
        }

        /// <summary>等待 leaderboard 后台异步 submit 完成（leaderboard 契约 §0：入队立即返回，异步落库）。</summary>
        private static bool SpinUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return condition();
        }

        // ---- 注册与默认状态 ----

        [Test]
        public static void Register_DefaultState_EntityAndSubscriptions()
        {
            var f = Fixture.Create();
            Assert.True(ScoreMod.ScoreEntityId != 0, "分数单实体未创建");
            var s = f.State();
            Assert.Equal(0, s.Current);
            Assert.Equal(0, s.HighScore); // 全新内存持久化
            Assert.Equal(0, s.Kills);
            Assert.True(!s.IsNewHigh);
            Assert.Equal(0, s.Combo);
            Assert.Equal(0, f.Host.Systems.CountOf(ScoreId)); // 无周期系统（契约 §2）
            Assert.Equal(2, f.Host.Messages.SubscriptionCount(ScoreId)); // EnemyKilled + Died（契约 §2）
            // 实体：player(1) + enemy 生成器(1) + score(1) = 3
            Assert.Equal(3, f.World.EntityCount);
        }

        [Test]
        public static void Register_HighScoreLoadedFromPersistence()
        {
            var f = Fixture.Create(new MemoryScorePersistence(3000)); // 预置最高分 3000（契约 §8 加载）
            Assert.Equal(3000, f.State().HighScore);
            Assert.Equal(0, f.State().Current);
        }

        // ---- 击杀计分（契约 §2 OnEnemyKilled） ----

        [Test]
        public static void KillEnemy_Normal_AccumulatesAndPublishes()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100); // Zombunny 普通 100
            var s = f.State();
            Assert.Equal(100, s.Current);
            Assert.Equal(1, s.Kills);
            Assert.Equal(100, s.HighScore); // 刷新最高分
            Assert.True(s.IsNewHigh);
            Assert.Equal(1, f.ScoreChangedEvents);
            Assert.Equal(100, f.LastCurrent);
            Assert.Equal(100, f.LastHighScore);
            Assert.Equal(1, f.LastKills);

            f.KillEnemy(1, 100); // ZomBear 普通 100
            s = f.State();
            Assert.Equal(200, s.Current);
            Assert.Equal(2, s.Kills);
            Assert.Equal(200, s.HighScore);
            Assert.Equal(2, f.ScoreChangedEvents);
        }

        [Test]
        public static void KillEnemy_Boss_Score500()
        {
            var f = Fixture.Create();
            f.KillEnemy(6, 500); // Titan 首领 500（契约 enemy §9 ScoreValue=500）
            var s = f.State();
            Assert.Equal(500, s.Current);
            Assert.Equal(500, s.HighScore);
            Assert.Equal(1, s.Kills);
            Assert.Equal(1, f.ScoreChangedEvents);
        }

        [Test]
        public static void KillEnemy_MixedNormalBoss_Sums()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100);
            f.KillEnemy(3, 100);
            f.KillEnemy(6, 500); // 2 普通 + 1 首领 = 700
            var s = f.State();
            Assert.Equal(700, s.Current);
            Assert.Equal(3, s.Kills);
            Assert.Equal(700, s.HighScore);
            Assert.True(s.IsNewHigh);
        }

        [Test]
        public static void KillEnemy_NonPositiveScore_Ignored()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 0);   // 零分
            f.KillEnemy(0, -50); // 负分（防御）
            var s = f.State();
            Assert.Equal(0, s.Current);
            Assert.Equal(0, s.Kills);
            Assert.Equal(0, f.ScoreChangedEvents);
        }

        [Test]
        public static void KillEnemy_ScoreAboveHigh_IsNewHigh_True()
        {
            var f = Fixture.Create(new MemoryScorePersistence(100)); // 既有最高分 100
            f.KillEnemy(0, 100); // 100 == 100 不刷新
            Assert.True(!f.State().IsNewHigh);
            Assert.Equal(1, f.ScoreChangedEvents);
            Assert.Equal(100, f.LastHighScore); // 发布 ScoreChanged(100, 100, 1)
            f.KillEnemy(6, 500); // 600 > 100 刷新
            var s = f.State();
            Assert.True(s.IsNewHigh);
            Assert.Equal(600, s.HighScore);
            Assert.Equal(600, f.LastHighScore); // 第二次发布 ScoreChanged(600, 600, 2)
        }

        // ---- 连击配置（契约 §2：v1 不实现连击逻辑，仅 config 常量 + Combo 字段预埋） ----

        [Test]
        public static void Combo_ConfigMultiplier2_NotAppliedInV1()
        {
            var f = Fixture.Create();
            Assert.Equal(2, ScoreConfig.ComboMultiplier); // 原版 comboMultiplier=2（契约 §8）
            f.KillEnemy(0, 100);
            f.KillEnemy(6, 500);
            // v1 不实现连击（契约 §2：原版仅配置未生效）：Combo 字段预埋保持 0，后续 append-only 扩展
            Assert.Equal(0, f.State().Combo);
        }

        // ---- 最高分持久化（契约 §8） ----

        [Test]
        public static void HighScore_PersistedOnUpdate()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100);
            f.KillEnemy(6, 500); // 600 > 100 刷新
            Assert.Equal(600, ((MemoryScorePersistence)f.Persistence).LoadHighScore());

            // 未刷新最高分路径：预置最高分 600，新局击杀 100 → 100 < 600 不刷新、持久化不变
            var f2 = Fixture.Create(new MemoryScorePersistence(600));
            f2.KillEnemy(0, 100);
            var s2 = f2.State();
            Assert.Equal(100, s2.Current);
            Assert.True(!s2.IsNewHigh);
            Assert.Equal(600, ((MemoryScorePersistence)f2.Persistence).LoadHighScore());
        }

        [Test]
        public static void HighScore_PersistsAcrossReset()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100);
            f.KillEnemy(0, 100); // 本局 200 / 最高 200
            Assert.True(f.CallBool(ScoreMod.ResetCap), "reset 应 ok");

            var s = f.State();
            Assert.Equal(0, s.Current); // 本局清零（契约 §3）
            Assert.Equal(200, s.HighScore); // 最高分跨局保留（对齐原版 ResetScore）

            f.KillEnemy(0, 100); // 新局 100 < 200：不刷新
            s = f.State();
            Assert.Equal(100, s.Current);
            Assert.Equal(200, s.HighScore);
            Assert.True(!s.IsNewHigh);
        }

        // ---- 能力：score:reset（契约 §3，game:start 调用） ----

        [Test]
        public static void Reset_ClearsRun_PublishesScoreChanged()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100);
            Assert.True(f.CallBool(ScoreMod.ResetCap));

            var s = f.State();
            Assert.Equal(0, s.Current);
            Assert.Equal(0, s.Kills);
            Assert.True(!s.IsNewHigh);
            Assert.Equal(0, s.Combo);
            Assert.Equal(100, s.HighScore); // 最高分不清零
            Assert.Equal(2, f.ScoreChangedEvents); // 击杀 1 次 + reset 1 次
            Assert.Equal(0, f.LastCurrent); // reset 发布 ScoreChanged(0, high, 0)
            Assert.Equal(100, f.LastHighScore);
            Assert.Equal(0, f.LastKills);
        }

        [Test]
        public static void Reset_BeforeAnyKill_Ok()
        {
            var f = Fixture.Create();
            Assert.True(f.CallBool(ScoreMod.ResetCap));
            Assert.Equal(1, f.ScoreChangedEvents);
            Assert.Equal(0, f.LastCurrent);
            Assert.Equal(0, f.LastHighScore);
        }

        [Test]
        public static void Reset_ClearsFinalizedRunResult()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100);
            f.PlayerDied(7u); // 结算 → RunResult.Finalized
            Assert.True(Com.Zombtoy.Score.CapabilityShapes.TryReadGetStateRow(f.Call(ScoreMod.GetStateCap), out var score, out _, out var kills, out _));
            Assert.Equal(100, score); // Finalized 时返回 RunResult 快照
            Assert.Equal(1, kills);

            Assert.True(f.CallBool(ScoreMod.ResetCap)); // 新对局：RunResult 清空（契约 §3）
            Assert.True(Com.Zombtoy.Score.CapabilityShapes.TryReadGetStateRow(f.Call(ScoreMod.GetStateCap), out var s2, out _, out var k2, out var isNew2));
            Assert.Equal(0, s2); // 回到 ScoreState 分支
            Assert.Equal(0, k2);
            Assert.True(!isNew2);
        }

        // ---- 能力：score:get_state（契约 §3，Result 初始填充） ----

        [Test]
        public static void GetState_Default_ZeroRow()
        {
            var f = Fixture.Create();
            var row = f.Call(ScoreMod.GetStateCap);
            Assert.True(Com.Zombtoy.Score.CapabilityShapes.TryReadGetStateRow(row, out var score, out var high, out var kills, out var isNew));
            Assert.Equal(0, score);
            Assert.Equal(0, high);
            Assert.Equal(0, kills);
            Assert.True(!isNew);
        }

        [Test]
        public static void GetState_AfterKills_Row()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100);
            f.KillEnemy(0, 100);
            var row = f.Call(ScoreMod.GetStateCap);
            Assert.True(Com.Zombtoy.Score.CapabilityShapes.TryReadGetStateRow(row, out var score, out var high, out var kills, out var isNew));
            Assert.Equal(200, score);
            Assert.Equal(200, high);
            Assert.Equal(2, kills);
            Assert.True(isNew);
        }

        // ---- 结算：player:Died（契约 §2 OnPlayerDied / §4 GameOver / §6 leaderboard:submit） ----

        [Test]
        public static void PlayerDied_Settles_PublishesGameOver()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100);
            f.KillEnemy(6, 500); // 600 / 2 杀
            f.PlayerDied(7u);    // 敌人接触击杀玩家

            Assert.Equal(1, f.GameOverEvents);
            Assert.Equal(600, f.LastFinalScore); // 字段1=finalScore
            Assert.Equal(2, f.LastGameOverKills); // 字段2=kills
            Assert.True(f.LastIsNewHigh); // 字段3=isNewHigh

            // RunResult 快照（契约 §1/§3：get_state 返回结算快照）
            Assert.True(Com.Zombtoy.Score.CapabilityShapes.TryReadGetStateRow(f.Call(ScoreMod.GetStateCap), out var score, out var high, out var kills, out var isNew));
            Assert.Equal(600, score);
            Assert.Equal(600, high);
            Assert.Equal(2, kills);
            Assert.True(isNew);
            Assert.True(f.World.Get<RunResult>(f.ScoreEntity).Finalized);
        }

        [Test]
        public static void PlayerDied_SubmitsLeaderboard_Async()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100);
            f.KillEnemy(0, 100); // 200 / 2 杀
            f.PlayerDied(7u);

            // leaderboard:submit 入队异步落库（契约 leaderboard §0：ModCall 同步入队立即返回）
            Assert.True(SpinUntil(() => f.Transport.SubmitCount == 1), "leaderboard submit 未发生");
            var snap = f.Transport.Snapshot();
            Assert.Equal(1, snap.Length);
            Assert.Equal(200, snap[0].Score);       // FinalScore
            Assert.Equal(ScoreConfig.PlayerName, snap[0].Name); // 默认玩家名 "player"（契约 §6）
            Assert.Equal(1, f.GameOverEvents); // 提交不影响结算发布
        }

        [Test]
        public static void PlayerDied_SubmitFailure_GameOverStillPublished()
        {
            var f = Fixture.Create();
            f.Transport.FailSubmit = true; // 模拟后端失败（leaderboard 契约 §2：失败静默）
            f.KillEnemy(0, 100);
            f.PlayerDied(7u);

            Assert.Equal(1, f.GameOverEvents); // 结算不受提交失败影响
            Assert.Equal(100, f.LastFinalScore);
            Assert.True(SpinUntil(() => f.Transport.SubmitCount == 1), "应尝试提交（即便失败）");
        }

        [Test]
        public static void PlayerDied_ZeroScore_SubmitsRejected_GameOverPublished()
        {
            var f = Fixture.Create();
            f.PlayerDied(0u); // 未击杀直接死亡：FinalScore=0
            Assert.Equal(1, f.GameOverEvents);
            Assert.Equal(0, f.LastFinalScore);
            Assert.True(!f.LastIsNewHigh);
            // leaderboard:submit(0, ...) 被拒绝（leaderboard 契约 §2：score<=0 → [false]，无异步提交）
            Thread.Sleep(50);
            Assert.Equal(0, f.Transport.SubmitCount);
        }

        [Test]
        public static void PlayerDied_Twice_SettlesOnce()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100);
            f.PlayerDied(7u);
            f.PlayerDied(7u); // 重复 Died（防御：已结算忽略）

            Assert.Equal(1, f.GameOverEvents);
            Assert.True(SpinUntil(() => f.Transport.SubmitCount == 1), "只应提交一次");
        }

        // ---- 契约测试向量（§14.11.3）：owner 规范字节 ↔ 样例一致 ----

        [Test]
        public static void TestVectors_DataCodecShapes_RoundTrip()
        {
            foreach (var v in ScoreTestVectors.All())
            {
                if (v.ContractId == ScoreTestVectors.ScoreChangedMsgContract ||
                    v.ContractId == ScoreTestVectors.GameOverMsgContract)
                    continue; // 消息向量不是 DataCodec 形状，走下一测试

                var fields = v.DecodeFields(); // owner 规范字节 → 字段元组
                Assert.Equal(v.Sample.Length, fields.Length, $"{v.ContractId} 字段数漂移");
                for (var i = 0; i < v.Sample.Length; i++)
                    Assert.Equal(v.Sample[i], fields[i], $"{v.ContractId}[{i}] 与样例不一致");
            }
        }

        [Test]
        public static void TestVectors_Messages_ConsumerStyleParsers()
        {
            // 消费方（ui.HudScore / ui.ResultSummary）各自重复定义的解析器（Rule 19）
            // 解析 owner 规范字节，与样例一致 → "可重复定义未漂移"（§14.11.3）
            foreach (var v in ScoreTestVectors.All())
            {
                if (v.ContractId == ScoreTestVectors.ScoreChangedMsgContract)
                {
                    Assert.True(HudScore.TryRead(v.Bytes, out var cur, out var high, out var kills), $"解析失败: {v}");
                    Assert.Equal((int)v.Sample[0]!, cur);
                    Assert.Equal((int)v.Sample[1]!, high);
                    Assert.Equal((int)v.Sample[2]!, kills);
                }
                else if (v.ContractId == ScoreTestVectors.GameOverMsgContract)
                {
                    Assert.True(ResultSummary.TryRead(v.Bytes, out var score, out var kills, out var isNew), $"解析失败: {v}");
                    Assert.Equal((int)v.Sample[0]!, score);
                    Assert.Equal((int)v.Sample[1]!, kills);
                    Assert.Equal((bool)v.Sample[2]!, isNew);
                }
            }
        }

        [Test]
        public static void TestVectors_Drift_NegativeCase()
        {
            // 漂移负向用例（summary §6）：owner 改了布局（GameOver 缺 isNewHigh 字段）应被消费方解析器检出
            var w = new PayloadWriter();
            w.WriteInt32(1, 1200);
            w.WriteInt32(2, 12);
            Assert.True(!ResultSummary.TryRead(w.ToBuffer().ToArray(), out _, out _, out _),
                "漂移载荷（缺字段3 isNewHigh）未被检出");

            // ScoreChanged 缺字段（reset 字节缺 kills）应被检出
            var w2 = new PayloadWriter();
            w2.WriteInt32(1, 0);
            w2.WriteInt32(2, 100);
            Assert.True(!HudScore.TryRead(w2.ToBuffer().ToArray(), out _, out _, out _),
                "漂移载荷（缺字段3 kills）未被检出");
        }

        // ---- 跨 owner 字节消费一致性（Rule 19：score 解析 enemy/player 消息向量） ----

        [Test]
        public static void Consumes_EnemyKilled_OwnerBytes()
        {
            // score 的消费方解析器解析 enemy owner 规范字节 → 与样例一致（契约 enemy §10）
            var parsed = 0;
            foreach (var v in EnemyTestVectors.All())
            {
                if (v.ContractId != EnemyTestVectors.KilledMsgContract) continue;
                Assert.True(ScoreSystem.TryReadEnemyKilled(new PayloadReader(v.Bytes), out var type, out var score),
                    $"enemy_killed_msg 解析失败: {v}");
                Assert.Equal((uint)v.Sample[0]!, type);
                Assert.Equal((int)v.Sample[1]!, score);
                parsed++;
            }
            Assert.True(parsed >= 2, $"应解析到至少 2 条 enemy_killed_msg 向量，实际 {parsed}");
        }

        [Test]
        public static void Consumes_PlayerDied_OwnerBytes()
        {
            var parsed = 0;
            foreach (var v in PlayerTestVectors.All())
            {
                if (v.ContractId != PlayerTestVectors.DiedMsgContract) continue;
                Assert.True(GameOverSystem.TryReadPlayerDied(new PayloadReader(v.Bytes), out var source),
                    $"player_died_msg 解析失败: {v}");
                Assert.Equal((uint)v.Sample[0]!, source);
                parsed++;
            }
            Assert.True(parsed >= 1, "应解析到 player_died_msg 向量");
        }

        // ---- 卸载清理（Rule 20 / §9.2 / §13.5） ----

        [Test]
        public static void Unload_CleansState_NoSubscriptions()
        {
            var f = Fixture.Create();
            f.KillEnemy(0, 100);
            f.PlayerDied(7u);
            Assert.True(f.World.Get<RunResult>(f.ScoreEntity).Finalized);
            var scoreEntityId = ScoreMod.ScoreEntityId;

            f.Host.Manager.Unload(ScoreId);

            Assert.True(!f.Host.Manager.IsLoaded(ScoreId));
            Assert.Equal(0, f.Host.Systems.CountOf(ScoreId));             // 无系统（本来 0）
            Assert.Equal(0, f.Host.Messages.SubscriptionCount(ScoreId));  // 订阅随卸载强制退订（§13.5）
            Assert.True(!f.World.Exists(new Entity(scoreEntityId)));      // 单实体随卸载强制销毁（§9.2）
            Assert.Equal(2, f.World.EntityCount);                         // 仅剩 player + enemy 生成器
            Assert.Equal(0u, ScoreMod.ScoreEntityId);                     // 静态桥清空
            Assert.True(ScoreMod.Context is null);
            Assert.True(ScoreMod.World is null);
        }

        // ---- 消费方风格解析器（Rule 19：结构相同、定义独立，模拟 ui 各自重复定义） ----

        /// <summary>消费方 ui.HudScore：score:ScoreChanged 解析器（[1]=current [2]=highScore [3]=kills，契约 §4）。</summary>
        public sealed class HudScore
        {
            public static bool TryRead(byte[] bytes, out int current, out int highScore, out int kills)
            {
                current = 0; highScore = 0; kills = 0;
                var reader = new PayloadReader(bytes);
                if (!reader.TryReadInt32(1, out current)) return false;
                if (!reader.TryReadInt32(2, out highScore)) return false;
                if (!reader.TryReadInt32(3, out kills)) return false;
                return true;
            }
        }

        /// <summary>消费方 ui.ResultSummary：score:GameOver 解析器（[1]=finalScore [2]=kills [3]=isNewHigh，契约 §4）。</summary>
        public sealed class ResultSummary
        {
            public static bool TryRead(byte[] bytes, out int finalScore, out int kills, out bool isNewHigh)
            {
                finalScore = 0; kills = 0; isNewHigh = false;
                var reader = new PayloadReader(bytes);
                if (!reader.TryReadInt32(1, out finalScore)) return false;
                if (!reader.TryReadInt32(2, out kills)) return false;
                if (!reader.TryReadBool(3, out isNewHigh)) return false;
                return true;
            }
        }
    }
}
