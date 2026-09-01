using System;
using System.Threading;
using Com.Game.Core;
using Com.Zombtoy.Enemy;
using Com.Zombtoy.Game;
using Com.Zombtoy.Item;
using Com.Zombtoy.Leaderboard;
using Com.Zombtoy.Player;
using Com.Zombtoy.Score;
using Com.Zombtoy.Ui;
using Com.Zombtoy.Weapon;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;
using UiFrameworkMod = Com.Game.Ui.UiMod;
using UiGamePhase = Com.Zombtoy.Ui.GamePhase;
using UiGameReason = Com.Zombtoy.Ui.GameReason;

namespace TestRunner
{
    /// <summary>
    /// com.zombtoy.ui 无头测试（纯消费方，契约 §0：无 ECS 逻辑——订阅各 Mod 消息 + 调 game/leaderboard 能力）：
    /// 窗口声明（5 窗口经 UI.Mod WindowManager 注册，Rule 9）、消息订阅（9 条，§13.5 卸载强制退订）、
    /// 消费方解析器 vs owner 测试向量（字节→结构一致性，§14.11.3：player/score/enemy/weapon/game/leaderboard 全矩阵）、
    /// 漂移负向用例、UiTestVectors 黄金副本自校验、订阅 handler 更新 UiBindings 快照（真实线上字节）、
    /// game:StateChanged 窗口路由、真实 ModCall 链路（UiCommands 调 game:start/pause/resume/to_menu/status）、
    /// score:GameOver → 开 Result + leaderboard:refresh（异步 TopChanged 刷新榜单）、卸载清理（Rule 20/§10.8）。
    /// 消息路径：测试经 Host.MessageBus 以 owner（player/score/enemy/weapon/game）编码器发布真实线上字节（Rule 14）。
    /// 依赖链（ui mod.json）：core → com.game.ui → player → leaderboard → enemy → weapon → item → score → game → ui。
    /// </summary>
    public static class ZombtoyUiTests
    {
        private static readonly ModId UiId = new("com.zombtoy.ui");
        private static readonly ModId GameId = new("com.zombtoy.game");
        private static readonly ModId ScoreId = new("com.zombtoy.score");
        private static readonly ModId PlayerId = new("com.zombtoy.player");
        private static readonly ModId EnemyId = new("com.zombtoy.enemy");
        private static readonly ModId WeaponId = new("com.zombtoy.weapon");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public MemoryTransport Transport = null!;
            public IModContext UiCtx = null!;

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };

                // 注入（先于 Mod 装配，同 score/leaderboard 测试先例）：leaderboard 传输 + 最高分持久化
                f.Transport = new MemoryTransport();
                LeaderboardTransport.Set(f.Transport);
                ScorePersistence.Set(new MemoryScorePersistence());

                // 依赖链（ui mod.json）：core → com.game.ui → player → leaderboard → enemy → weapon → item → score → game → ui
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.core")), typeof(CoreMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.ui")), typeof(UiFrameworkMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.player")),
                    typeof(PlayerMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.player"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.leaderboard")),
                    typeof(LeaderboardMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.leaderboard"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.enemy")),
                    typeof(EnemyMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.enemy"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.weapon")),
                    typeof(WeaponMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.weapon"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.item")),
                    typeof(ItemMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.item"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.score")),
                    typeof(ScoreMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.score"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.game")),
                    typeof(GameMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.game"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.ui")),
                    typeof(UiMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.ui"));
                f.UiCtx = f.Host.Manager.Get(UiId)!.Context;
                return f;
            }

            public object?[] Call(CapabilityId cap, params object?[] args)
                => ModCall.Invoke(UiCtx.Mods, UiId, cap, args);

            public bool CallBool(CapabilityId cap, params object?[] args)
                => ModCall.InvokeBool(UiCtx.Mods, UiId, cap, args);

            // ---- 以 owner 编码器发布真实线上字节（Rule 14；消费方解析器/订阅 handler 的输入） ----
            public void PublishHealth(int current, int max)
            {
                var buf = PlayerMessagePayload.HealthChanged(current, max);
                Host.Messages.Publish(PlayerId, PlayerMod.HealthChangedEvent, 1, in buf);
            }

            public void PublishStamina(float current, float max)
            {
                var buf = PlayerMessagePayload.StaminaChanged(current, max);
                Host.Messages.Publish(PlayerId, PlayerMod.StaminaChangedEvent, 1, in buf);
            }

            public void PublishScoreChanged(int current, int high, int kills)
            {
                var buf = ScoreMessagePayload.ScoreChanged(current, high, kills);
                Host.Messages.Publish(ScoreId, ScoreMod.ScoreChangedEvent, 1, in buf);
            }

            public void PublishScoreGameOver(int finalScore, int kills, bool isNewHigh)
            {
                var buf = ScoreMessagePayload.GameOver(finalScore, kills, isNewHigh);
                Host.Messages.Publish(ScoreId, ScoreMod.GameOverEvent, 1, in buf);
            }

            public void PublishZombieCount(int count)
            {
                var buf = EnemyMessagePayload.ZombieCountChanged(count);
                Host.Messages.Publish(EnemyId, EnemyMod.ZombieCountChangedEvent, 1, in buf);
            }

            public void PublishAmmo(uint slot, int inMag, int reserve, int maxMag)
            {
                var buf = WeaponMessagePayload.AmmoChanged(slot, inMag, reserve, maxMag);
                Host.Messages.Publish(WeaponId, WeaponMod.AmmoChangedEvent, 1, in buf);
            }

            public void PublishWeaponSwitched(uint slot)
            {
                var buf = WeaponMessagePayload.WeaponSwitched(slot);
                Host.Messages.Publish(WeaponId, WeaponMod.WeaponSwitchedEvent, 1, in buf);
            }

            public void PublishStateChanged(byte phase, byte reason)
            {
                var buf = GameMessagePayload.StateChanged(phase, reason);
                Host.Messages.Publish(GameId, GameMod.StateChangedEvent, 1, in buf);
            }
        }

        /// <summary>等待 leaderboard 后台异步刷新完成（leaderboard 契约 §0：入队立即返回，异步拉取后发布 TopChanged）。</summary>
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

        // ---- 注册：窗口声明 + 消息订阅（契约 §1/§2；Rule 9/§13.5） ----

        [Test]
        public static void Register_DeclaresFiveWindows_SubscribesNineMessages()
        {
            var f = Fixture.Create();

            // 5 窗口经 UI.Mod WindowManager 注册（Rule 9；契约 §1：menu/hud/result/settings/leaderboard）
            foreach (var w in new[]
            {
                UiMod.MenuWindow, UiMod.HudWindow, UiMod.ResultWindow,
                UiMod.SettingsWindow, UiMod.LeaderboardWindow,
            })
            {
                Assert.True(!f.UiCtx.Ui.IsOpen(w), $"{w} 初始应关闭");
                f.UiCtx.Ui.Open(w); // 未注册会抛 ModStateException（§10.9）→ 能打开即已注册
                Assert.True(f.UiCtx.Ui.IsOpen(w), $"{w} 打开失败");
                f.UiCtx.Ui.Close(w);
            }

            // 消息订阅（契约 §2 订阅表）：player×2 + enemy + score×2 + weapon×2 + game + leaderboard = 9
            Assert.Equal(9, f.Host.Messages.SubscriptionCount(UiId));
            Assert.Equal(0, f.Host.Systems.CountOf(UiId)); // 无周期系统（契约 §4：ui 无系统）
            Assert.True(UiMod.Registered);
        }

        // ---- 消费方解析器 vs owner 测试向量（§14.11.3 消费矩阵；summary §6） ----

        [Test]
        public static void Parsers_Consume_PlayerOwnerVectors()
        {
            foreach (var v in PlayerTestVectors.All())
            {
                if (v.ContractId == UiTestVectors.PlayerHealthMsgContract)
                {
                    Assert.True(HudHealth.TryRead(new PayloadReader(v.Bytes), out var cur, out var max),
                        $"player_health_msg 解析失败: {v}");
                    Assert.Equal((int)v.Sample[0]!, cur);
                    Assert.Equal((int)v.Sample[1]!, max);
                }
                else if (v.ContractId == UiTestVectors.PlayerStaminaMsgContract)
                {
                    Assert.True(HudStamina.TryRead(new PayloadReader(v.Bytes), out var cur, out var max),
                        $"player_stamina_msg 解析失败: {v}");
                    Assert.Equal((float)v.Sample[0]!, cur);
                    Assert.Equal((float)v.Sample[1]!, max);
                }
                else if (v.ContractId == UiTestVectors.PlayerGetStateRowContract)
                {
                    var row = v.DecodeFields();
                    Assert.True(UiCapabilityShapes.TryReadPlayerState(row, out var hp, out var max, out var st, out var maxSt, out var alive),
                        $"player_get_state_row 解析失败: {v}");
                    Assert.Equal((int)v.Sample[0]!, hp);
                    Assert.Equal((int)v.Sample[1]!, max);
                    Assert.Equal((float)v.Sample[2]!, st);
                    Assert.Equal((float)v.Sample[3]!, maxSt);
                    Assert.Equal((bool)v.Sample[4]!, alive);
                }
            }
        }

        [Test]
        public static void Parsers_Consume_ScoreEnemyWeaponOwnerVectors()
        {
            foreach (var v in ScoreTestVectors.All())
            {
                if (v.ContractId == UiTestVectors.ScoreChangedMsgContract)
                {
                    Assert.True(HudScore.TryRead(new PayloadReader(v.Bytes), out var cur, out var high, out var kills),
                        $"score_changed_msg 解析失败: {v}");
                    Assert.Equal((int)v.Sample[0]!, cur);
                    Assert.Equal((int)v.Sample[1]!, high);
                    Assert.Equal((int)v.Sample[2]!, kills);
                }
                else if (v.ContractId == UiTestVectors.ScoreGameOverMsgContract)
                {
                    Assert.True(ResultSummary.TryRead(new PayloadReader(v.Bytes), out var score, out var kills, out var isNew),
                        $"score_gameover_msg 解析失败: {v}");
                    Assert.Equal((int)v.Sample[0]!, score);
                    Assert.Equal((int)v.Sample[1]!, kills);
                    Assert.Equal((bool)v.Sample[2]!, isNew);
                }
                else if (v.ContractId == UiTestVectors.ScoreGetStateRowContract)
                {
                    var row = v.DecodeFields();
                    Assert.True(UiCapabilityShapes.TryReadScoreState(row, out var sc, out var hi, out var k, out var nh),
                        $"score_get_state_row 解析失败: {v}");
                    Assert.Equal((int)v.Sample[0]!, sc);
                    Assert.Equal((int)v.Sample[1]!, hi);
                    Assert.Equal((int)v.Sample[2]!, k);
                    Assert.Equal((bool)v.Sample[3]!, nh);
                }
            }

            foreach (var v in EnemyTestVectors.All())
            {
                if (v.ContractId == UiTestVectors.EnemyCountMsgContract)
                {
                    Assert.True(HudZombies.TryRead(new PayloadReader(v.Bytes), out var count),
                        $"enemy_count_msg 解析失败: {v}");
                    Assert.Equal((int)v.Sample[0]!, count);
                }
                else if (v.ContractId == UiTestVectors.EnemyGetCountRowContract)
                {
                    var row = v.DecodeFields();
                    Assert.True(UiCapabilityShapes.TryReadEnemyCount(row, out var count),
                        $"enemy_get_count_row 解析失败: {v}");
                    Assert.Equal((int)v.Sample[0]!, count);
                }
            }

            foreach (var v in WeaponTestVectors.All())
            {
                if (v.ContractId == UiTestVectors.WeaponAmmoMsgContract)
                {
                    Assert.True(HudAmmo.TryRead(new PayloadReader(v.Bytes), out var slot, out var inMag, out var reserve, out var maxMag),
                        $"weapon_ammo_msg 解析失败: {v}");
                    Assert.Equal((uint)v.Sample[0]!, slot);
                    Assert.Equal((int)v.Sample[1]!, inMag);
                    Assert.Equal((int)v.Sample[2]!, reserve);
                    Assert.Equal((int)v.Sample[3]!, maxMag);
                }
                else if (v.ContractId == UiTestVectors.WeaponSwitchedMsgContract)
                {
                    Assert.True(HudWeapon.TryRead(new PayloadReader(v.Bytes), out var slot),
                        $"weapon_switched_msg 解析失败: {v}");
                    Assert.Equal((uint)v.Sample[0]!, slot);
                }
                else if (v.ContractId == UiTestVectors.WeaponGetStateRowContract)
                {
                    var row = v.DecodeFields();
                    Assert.True(UiCapabilityShapes.TryReadWeaponState(row, out var slot, out var slotCount, out var inMag, out var reserve, out var maxMag),
                        $"weapon_get_state_row 解析失败: {v}");
                    Assert.Equal((uint)v.Sample[0]!, slot);
                    Assert.Equal((uint)v.Sample[1]!, slotCount);
                    Assert.Equal((int)v.Sample[2]!, inMag);
                    Assert.Equal((int)v.Sample[3]!, reserve);
                    Assert.Equal((int)v.Sample[4]!, maxMag);
                }
            }
        }

        [Test]
        public static void Parsers_Consume_GameOwnerVectors_StateChanged()
        {
            foreach (var v in GameTestVectors.All())
            {
                if (v.ContractId == UiTestVectors.GameStateMsgContract)
                {
                    Assert.True(GameStateChanged.TryRead(new PayloadReader(v.Bytes), out var phase, out var reason),
                        $"game_state_msg 解析失败: {v}");
                    Assert.Equal((uint)v.Sample[0]!, phase);
                    Assert.Equal((uint)v.Sample[1]!, reason);
                }
                else if (v.ContractId == UiTestVectors.GameStatusRowContract)
                {
                    var row = v.DecodeFields();
                    Assert.True(UiCapabilityShapes.TryReadStatusRow(row, out var phase),
                        $"game_status_row 解析失败: {v}");
                    Assert.Equal((uint)v.Sample[0]!, phase);
                }
                else if (v.ContractId == UiTestVectors.GameStartArgsContract ||
                         v.ContractId == UiTestVectors.GameToMenuArgsContract ||
                         v.ContractId == UiTestVectors.GamePauseArgsContract ||
                         v.ContractId == UiTestVectors.GameResumeArgsContract)
                {
                    Assert.Equal(0, v.DecodeFields().Length); // 无参能力（契约 §4：args=[]）
                }
            }
        }

        [Test]
        public static void Parsers_Consume_LeaderboardOwnerVectors()
        {
            foreach (var v in LeaderboardTestVectors.All())
            {
                if (v.ContractId == UiTestVectors.LeaderboardTopMsgContract)
                {
                    Assert.True(LeaderboardRows.TryRead(new PayloadReader(v.Bytes), out var count, out var rows),
                        $"leaderboard_top_msg 解析失败: {v}");
                    Assert.Equal((uint)v.Sample[0]!, count);
                    Assert.Equal(((object?[])v.Sample[1]!).Length, rows.Length, "行数不一致");
                    var sampleRows = (object?[])v.Sample[1]!;
                    for (var i = 0; i < sampleRows.Length; i++)
                    {
                        Assert.True(LeaderboardRows.TryReadRow(rows[i], out var rank, out var score, out var name),
                            $"第 {i} 行解析失败: {v}");
                        var sampleRow = (object?[])sampleRows[i]!;
                        Assert.Equal((uint)sampleRow[0]!, rank);
                        Assert.Equal((int)sampleRow[1]!, score);
                        Assert.Equal((string)sampleRow[2]!, name);
                    }
                }
                else if (v.ContractId == UiTestVectors.LeaderboardRowContract)
                {
                    var row = v.DecodeFields();
                    Assert.True(UiCapabilityShapes.TryReadGetTopRow(row, out var rank, out var score, out var name),
                        $"leaderboard_row 解析失败: {v}");
                    Assert.Equal((uint)v.Sample[0]!, rank);
                    Assert.Equal((int)v.Sample[1]!, score);
                    Assert.Equal((string)v.Sample[2]!, name);
                }
                else if (v.ContractId == UiTestVectors.LeaderboardGetTopArgsContract)
                {
                    var fields = v.DecodeFields();
                    Assert.Equal(1, fields.Length);
                    Assert.Equal((int)v.Sample[0]!, fields[0]);
                }
            }
        }

        [Test]
        public static void Parsers_Drift_NegativeCases()
        {
            // 漂移负向用例（summary §6）：owner 改了布局（缺字段）应被消费方解析器检出
            var w1 = new PayloadWriter();
            w1.WriteInt32(1, 95); // player_health_msg 缺字段2 max
            Assert.True(!HudHealth.TryRead(new PayloadReader(w1.ToBuffer()), out _, out _));

            var w2 = new PayloadWriter();
            w2.WriteUInt32(1, 0); w2.WriteInt32(2, 25); w2.WriteInt32(3, 50); // weapon_ammo_msg 缺字段4 maxMag
            Assert.True(!HudAmmo.TryRead(new PayloadReader(w2.ToBuffer()), out _, out _, out _, out _));

            var w3 = new PayloadWriter();
            w3.WriteInt32(1, 1200); w3.WriteInt32(2, 12); // score_gameover_msg 缺字段3 isNewHigh
            Assert.True(!ResultSummary.TryRead(new PayloadReader(w3.ToBuffer()), out _, out _, out _));

            var w4 = new PayloadWriter();
            w4.WriteUInt32(1, 0); // leaderboard_top_msg 缺字段2 rows
            Assert.True(!LeaderboardRows.TryRead(new PayloadReader(w4.ToBuffer()), out _, out _));

            var w5 = new PayloadWriter();
            w5.WriteUInt32(1, 1); // game_state_msg 缺字段2 reason
            Assert.True(!GameStateChanged.TryRead(new PayloadReader(w5.ToBuffer()), out _, out _));
        }

        // ---- UiTestVectors 黄金副本（ui CONTRACT.md §5 向量回灌） ----

        [Test]
        public static void UiTestVectors_GoldenArchive_ParsesConsistently()
        {
            foreach (var v in UiTestVectors.All())
            {
                if (v.ContractId == UiTestVectors.GameStateMsgContract)
                {
                    Assert.True(GameStateChanged.TryRead(new PayloadReader(v.Bytes), out var phase, out var reason), $"{v}");
                    Assert.Equal((uint)v.Sample[0]!, phase);
                    Assert.Equal((uint)v.Sample[1]!, reason);
                }
                else if (v.ContractId == UiTestVectors.LeaderboardTopMsgContract)
                {
                    Assert.True(LeaderboardRows.TryRead(new PayloadReader(v.Bytes), out var count, out var rows), $"{v}");
                    Assert.Equal((uint)v.Sample[0]!, count);
                    Assert.Equal(((object?[])v.Sample[1]!).Length, rows.Length, "行数不一致");
                }
                else if (v.ContractId == UiTestVectors.GameStatusRowContract)
                {
                    Assert.True(UiCapabilityShapes.TryReadStatusRow(v.DecodeFields(), out var phase), $"{v}");
                    Assert.Equal((uint)v.Sample[0]!, phase);
                }
                else if (v.ContractId == UiTestVectors.PlayerGetStateRowContract)
                {
                    Assert.True(UiCapabilityShapes.TryReadPlayerState(v.DecodeFields(), out _, out _, out _, out _, out _), $"{v}");
                }
                else if (v.ContractId == UiTestVectors.EnemyGetCountRowContract)
                {
                    Assert.True(UiCapabilityShapes.TryReadEnemyCount(v.DecodeFields(), out _), $"{v}");
                }
                else if (v.ContractId == UiTestVectors.WeaponGetStateRowContract)
                {
                    Assert.True(UiCapabilityShapes.TryReadWeaponState(v.DecodeFields(), out _, out _, out _, out _, out _), $"{v}");
                }
                else if (v.ContractId == UiTestVectors.ScoreGetStateRowContract)
                {
                    Assert.True(UiCapabilityShapes.TryReadScoreState(v.DecodeFields(), out _, out _, out _, out _), $"{v}");
                }
                else if (v.ContractId == UiTestVectors.LeaderboardRowContract)
                {
                    Assert.True(UiCapabilityShapes.TryReadGetTopRow(v.DecodeFields(), out _, out _, out _), $"{v}");
                }
                else if (v.ContractId == UiTestVectors.GameStartArgsContract ||
                         v.ContractId == UiTestVectors.GameToMenuArgsContract ||
                         v.ContractId == UiTestVectors.GamePauseArgsContract ||
                         v.ContractId == UiTestVectors.GameResumeArgsContract ||
                         v.ContractId == UiTestVectors.LeaderboardGetTopArgsContract)
                {
                    v.DecodeFields(); // 只验证可解码（无参/参数形状）
                }
                else
                {
                    // 消息载荷向量：owner 编码器字段布局的黄金副本 → 解析器应可读
                    Assert.True(ParseMessageVector(v), $"黄金副本解析失败: {v}");
                }
            }
        }

        /// <summary>消息载荷向量解析分发（owner 实时向量与黄金副本共用）。</summary>
        private static bool ParseMessageVector(TestVector v)
        {
            if (v.ContractId == UiTestVectors.PlayerHealthMsgContract)
                return HudHealth.TryRead(new PayloadReader(v.Bytes), out _, out _);
            if (v.ContractId == UiTestVectors.PlayerStaminaMsgContract)
                return HudStamina.TryRead(new PayloadReader(v.Bytes), out _, out _);
            if (v.ContractId == UiTestVectors.EnemyCountMsgContract)
                return HudZombies.TryRead(new PayloadReader(v.Bytes), out _);
            if (v.ContractId == UiTestVectors.ScoreChangedMsgContract)
                return HudScore.TryRead(new PayloadReader(v.Bytes), out _, out _, out _);
            if (v.ContractId == UiTestVectors.ScoreGameOverMsgContract)
                return ResultSummary.TryRead(new PayloadReader(v.Bytes), out _, out _, out _);
            if (v.ContractId == UiTestVectors.WeaponAmmoMsgContract)
                return HudAmmo.TryRead(new PayloadReader(v.Bytes), out _, out _, out _, out _);
            if (v.ContractId == UiTestVectors.WeaponSwitchedMsgContract)
                return HudWeapon.TryRead(new PayloadReader(v.Bytes), out _);
            return false;
        }

        // ---- 订阅 handler：真实线上字节 → UiBindings 快照（契约 §2 视图数据绑定） ----

        [Test]
        public static void Subscriptions_UpdateBindings_OnLiveOwnerBytes()
        {
            var f = Fixture.Create();
            f.PublishHealth(95, 100);
            Assert.Equal(95, UiBindings.Health);
            Assert.Equal(100, UiBindings.MaxHealth);

            f.PublishStamina(0.4f, 1f);
            Assert.Equal(0.4f, UiBindings.Stamina);
            Assert.Equal(1f, UiBindings.MaxStamina);

            f.PublishZombieCount(7);
            Assert.Equal(7, UiBindings.ZombieCount);

            f.PublishScoreChanged(1200, 5000, 12);
            Assert.Equal(1200, UiBindings.Score);
            Assert.Equal(5000, UiBindings.HighScore);
            Assert.Equal(12, UiBindings.Kills);

            f.PublishAmmo(2u, 0, 5, 1);
            Assert.Equal(2u, UiBindings.AmmoSlot);
            Assert.Equal(0, UiBindings.InMag);
            Assert.Equal(5, UiBindings.Reserve);
            Assert.Equal(1, UiBindings.MaxMag);

            f.PublishWeaponSwitched(1u);
            Assert.Equal(1u, UiBindings.WeaponSlot);
        }

        [Test]
        public static void Subscriptions_MalformedPayloads_Ignored()
        {
            var f = Fixture.Create();
            f.PublishHealth(95, 100);
            Assert.Equal(95, UiBindings.Health);

            // 漂移载荷（缺字段2 max）：handler 防御忽略，快照不变
            var w = new PayloadWriter();
            w.WriteInt32(1, 10);
            var bad = w.ToBuffer();
            f.Host.Messages.Publish(PlayerId, PlayerMod.HealthChangedEvent, 1, in bad);
            Assert.Equal(95, UiBindings.Health);
            Assert.Equal(100, UiBindings.MaxHealth);
        }

        // ---- 窗口路由（契约 §1 打开时机：game:StateChanged 驱动） ----

        [Test]
        public static void StateChanged_RoutesWindows_ByPhase()
        {
            var f = Fixture.Create();
            var wm = UiFrameworkMod.Current!;
            Assert.True(!wm.IsOpen(UiMod.MenuWindow)); // 初始（注册未打开，契约 §1 由 StateChanged 驱动）

            f.PublishStateChanged((byte)UiGamePhase.Menu, (byte)UiGameReason.ToMenu);
            Assert.True(wm.IsOpen(UiMod.MenuWindow));
            Assert.True(!wm.IsOpen(UiMod.HudWindow));
            Assert.True(!wm.IsOpen(UiMod.ResultWindow));
            Assert.Equal((byte)UiGamePhase.Menu, UiBindings.Phase);

            f.PublishStateChanged((byte)UiGamePhase.Playing, (byte)UiGameReason.Started);
            Assert.True(wm.IsOpen(UiMod.HudWindow));
            Assert.True(!wm.IsOpen(UiMod.MenuWindow));
            Assert.True(!wm.IsOpen(UiMod.ResultWindow));
            Assert.Equal((byte)UiGamePhase.Playing, UiBindings.Phase);
            Assert.Equal((byte)UiGameReason.Started, UiBindings.Reason);

            f.PublishStateChanged((byte)UiGamePhase.GameOver, (byte)UiGameReason.PlayerDied);
            Assert.True(!wm.IsOpen(UiMod.HudWindow)); // GameOver：关 HUD（Result 由 score:GameOver 打开）
        }

        // ---- 真实 ModCall 链路（契约 §3：ui → game 单向调用，二进制 Rule 14） ----

        [Test]
        public static void StartGame_RealModCall_OpensHud()
        {
            var f = Fixture.Create();
            var wm = UiFrameworkMod.Current!;

            Assert.True(UiCommands.StartGame(), "game:start 应 ok（开局编排由 game 能力实现）");
            Assert.True(wm.IsOpen(UiMod.HudWindow), "StateChanged(Playing) 路由应打开 HUD");
            Assert.True(!wm.IsOpen(UiMod.MenuWindow));
            Assert.True(UiCommands.TryReadStatus(out var phase), "game:status 行解析");
            Assert.Equal((uint)UiGamePhase.Playing, phase);
        }

        [Test]
        public static void PauseResumeToMenu_RealModCalls_RouteWindows()
        {
            var f = Fixture.Create();
            var wm = UiFrameworkMod.Current!;

            Assert.True(!UiCommands.Pause(), "Menu 相位下 pause 应被 game 拒绝");
            Assert.True(!UiCommands.Resume(), "Menu 相位下 resume 应被 game 拒绝");
            Assert.True(UiCommands.ToMenu(), "Menu 相位下 to_menu 应 ok（幂等回菜单）");

            Assert.True(UiCommands.StartGame());
            Assert.True(UiCommands.Pause(), "Playing 下 pause 应 ok");
            Assert.True(UiCommands.TryReadStatus(out var p1));
            Assert.Equal((uint)UiGamePhase.Paused, p1);
            Assert.True(wm.IsOpen(UiMod.HudWindow), "Paused 相位保持 HUD 打开（暂停覆盖层由视图层处理）");

            Assert.True(UiCommands.Resume(), "Paused 下 resume 应 ok");
            Assert.True(UiCommands.TryReadStatus(out var p2));
            Assert.Equal((uint)UiGamePhase.Playing, p2);

            Assert.True(UiCommands.ToMenu(), "Playing 下 to_menu 应 ok");
            Assert.True(UiCommands.TryReadStatus(out var p3));
            Assert.Equal((uint)UiGamePhase.Menu, p3);
            Assert.True(wm.IsOpen(UiMod.MenuWindow), "StateChanged(Menu) 路由应开主菜单");
            Assert.True(!wm.IsOpen(UiMod.HudWindow));
        }

        // ---- score:GameOver → 开 Result + leaderboard:refresh 异步刷新榜单（summary §5.2 链路） ----

        [Test]
        public static void GameOver_OpensResult_RefreshesLeaderboard()
        {
            var f = Fixture.Create();
            var wm = UiFrameworkMod.Current!;
            f.Transport.Seed(9000, "alice"); // 后端已有分数（leaderboard 契约 §1 种子）

            Assert.True(UiCommands.StartGame());
            f.PublishScoreGameOver(1200, 12, true); // score owner 编码器（契约 score §4）

            // 结算快照（契约 §2：ResultSummary 解析器）
            Assert.Equal(1200, UiBindings.FinalScore);
            Assert.Equal(12, UiBindings.GameOverKills);
            Assert.True(UiBindings.IsNewHigh);
            Assert.True(UiBindings.HasGameOver);

            // Result 窗口打开 + HUD 关闭（契约 §1）
            Assert.True(wm.IsOpen(UiMod.ResultWindow), "score:GameOver 应开 Result");
            Assert.True(!wm.IsOpen(UiMod.HudWindow), "StateChanged(GameOver) 应关 HUD");

            // leaderboard:refresh 异步拉取 → TopChanged → 榜单快照（契约 §3）
            Assert.True(SpinUntil(() => UiBindings.Rows.Length > 0), "refresh 未发布 TopChanged");
            Assert.True(LeaderboardRows.TryReadRow(UiBindings.Rows[0], out var rank, out var score, out var name));
            Assert.Equal(1u, rank);
            Assert.Equal(9000, score);
            Assert.Equal("alice", name);
        }

        [Test]
        public static void GameOver_ThenPlayAgain_Restarts()
        {
            var f = Fixture.Create();
            f.Transport.Seed(9000, "alice");
            f.PublishScoreGameOver(500, 5, false);
            Assert.True(wmIsOpen(UiMod.ResultWindow), "score:GameOver 应开 Result");

            // 等本次 score:GameOver 触发的异步榜单刷新完成（leaderboard 契约 §0：入队异步）——
            // 避免后台任务在测试结束后才完成、污染后续测试共享的静态缓存（跨 fixture 竞态防御）
            Assert.True(SpinUntil(() => UiBindings.Rows.Length >= 1), "refresh 未发布 TopChanged");

            Assert.True(UiCommands.StartGame(), "Result 再玩一次 → game:start 应 ok");
            Assert.True(wmIsOpen(UiMod.HudWindow), "新对局应开 HUD");
            Assert.True(!wmIsOpen(UiMod.ResultWindow));
            Assert.Equal(0, UiBindings.Score); // score:reset 清零（契约 score §3）
        }

        private static bool wmIsOpen(WindowId w) => UiFrameworkMod.Current!.IsOpen(w);

        // ---- 卸载清理（Rule 20 / §10.8 / §13.5） ----

        [Test]
        public static void Unload_CleansSubscriptions_WindowsClosed_BindingsReset()
        {
            var f = Fixture.Create();
            var wm = UiFrameworkMod.Current!;
            f.PublishStateChanged((byte)UiGamePhase.Menu, (byte)UiGameReason.Manual); // 打开主菜单窗口
            f.PublishHealth(95, 100);
            Assert.True(wm.IsOpen(UiMod.MenuWindow));
            Assert.Equal(95, UiBindings.Health);

            f.Host.Manager.Unload(UiId);

            Assert.True(!f.Host.Manager.IsLoaded(UiId));
            Assert.Equal(0, f.Host.Messages.SubscriptionCount(UiId)); // 订阅随卸载强制退订（§13.5）
            Assert.Equal(0, f.Host.Systems.CountOf(UiId));             // 无系统（本来 0）
            Assert.True(!wm.IsOpen(UiMod.MenuWindow));                 // 窗口随卸载强制关闭（§10.8 CloseAll）
            Assert.Equal(0, wm.OpenCountOf(UiId));
            Assert.True(UiMod.Context is null);                        // 静态桥清空
            Assert.True(!UiMod.Registered);
            Assert.Equal(0, UiBindings.Health);                        // 快照清空
            Assert.Equal(0, UiBindings.ZombieCount);
            Assert.True(!UiBindings.HasGameOver);
        }
    }
}
