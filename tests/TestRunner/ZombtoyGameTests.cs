using System;
using Com.Game.Core;
using Com.Zombtoy.Enemy;
using Com.Zombtoy.Game;
using Com.Zombtoy.Item;
using Com.Zombtoy.Leaderboard;
using Com.Zombtoy.Player;
using Com.Zombtoy.Score;
using Com.Zombtoy.Weapon;
using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// com.zombtoy.game 无头测试（单机 Host 路径，契约 §2：无周期 System——能力实现 = 编排器）：
    /// 相位状态机（Menu → Playing → Paused → GameOver → Menu）、开局编排（game:start 依次调组件 Mod
    /// reset + set_spawning(true)，加载真实 Mod 程序集验证 ModCall 生效）、score:GameOver 订阅收尾、
    /// pause/resume、game:status、StateChanged 发布（二进制字段解析）、契约测试向量自校验（§14.11.3）、
    /// 跨 owner 字节消费一致性（game 解析 score:GameOver 向量）、编排失败防御、卸载清理（Rule 20）。
    /// 消息路径：测试经 Host.MessageBus 以 owner（score）编码器发布真实线上字节（Rule 14）。
    /// 依赖链（契约 §6 mod.json）：core → player/leaderboard → enemy → weapon → item → score → game。
    /// </summary>
    public static class ZombtoyGameTests
    {
        private static readonly ModId GameId = new("com.zombtoy.game");
        private static readonly ModId PlayerId = new("com.zombtoy.player");
        private static readonly ModId EnemyId = new("com.zombtoy.enemy");
        private static readonly ModId WeaponId = new("com.zombtoy.weapon");
        private static readonly ModId ItemId = new("com.zombtoy.item");
        private static readonly ModId ScoreId = new("com.zombtoy.score");
        private static readonly ModId TestSubscriber = new("com.test.runner");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public World World => Host.World;
            public IModContext GameCtx = null!;
            public MemoryTransport Transport = null!;

            // 消费方视角订阅各 Mod 发布消息（PayloadReader 字段解析，Rule 14；观察 game:start 编排的副作用）
            public int StateChangedEvents;
            public uint LastPhase, LastReason;
            public int ScoreChangedEvents;
            public int HealthChangedEvents;
            public int StaminaChangedEvents;
            public int ZombieCountEvents;
            public int AmmoChangedEvents;
            public int WeaponSwitchedEvents;

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };

                // 注入（先于 Mod 装配，同 score/leaderboard 测试先例）：leaderboard 传输 + 最高分持久化
                f.Transport = new MemoryTransport();
                LeaderboardTransport.Set(f.Transport);
                ScorePersistence.Set(new MemoryScorePersistence());

                // 依赖（契约 §6）：core → player/leaderboard → enemy → weapon → item → score → game
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.core")), typeof(CoreMod).Assembly);
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
                f.GameCtx = f.Host.Manager.Get(GameId)!.Context;

                // 消费方订阅（契约 §5：ui 视角解析 StateChanged）
                f.Host.Messages.Subscribe(TestSubscriber, GameMod.StateChangedEvent,
                    (in MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.StateChangedEvents++;
                        if (reader.TryReadUInt32(1, out var p)) f.LastPhase = p;
                        if (reader.TryReadUInt32(2, out var r)) f.LastReason = r;
                    });
                // 观察编排副作用：score/player/enemy/weapon 各 reset 的发布消息（契约各 §）
                f.Host.Messages.Subscribe(TestSubscriber, ScoreMod.ScoreChangedEvent,
                    (in MessageEnvelope _, PayloadReader _) => f.ScoreChangedEvents++);
                f.Host.Messages.Subscribe(TestSubscriber, PlayerMod.HealthChangedEvent,
                    (in MessageEnvelope _, PayloadReader _) => f.HealthChangedEvents++);
                f.Host.Messages.Subscribe(TestSubscriber, PlayerMod.StaminaChangedEvent,
                    (in MessageEnvelope _, PayloadReader _) => f.StaminaChangedEvents++);
                f.Host.Messages.Subscribe(TestSubscriber, EnemyMod.ZombieCountChangedEvent,
                    (in MessageEnvelope _, PayloadReader _) => f.ZombieCountEvents++);
                f.Host.Messages.Subscribe(TestSubscriber, WeaponMod.AmmoChangedEvent,
                    (in MessageEnvelope _, PayloadReader _) => f.AmmoChangedEvents++);
                f.Host.Messages.Subscribe(TestSubscriber, WeaponMod.WeaponSwitchedEvent,
                    (in MessageEnvelope _, PayloadReader _) => f.WeaponSwitchedEvents++);
                return f;
            }

            public object?[] Call(CapabilityId cap, params object?[] args)
                => ModCall.Invoke(GameCtx.Mods, GameId, cap, args);

            public bool CallBool(CapabilityId cap, params object?[] args)
                => ModCall.InvokeBool(GameCtx.Mods, GameId, cap, args);

            public Entity GameEntity => new(GameMod.GameEntityId);
            public GameState State() => World.Get<GameState>(GameEntity);

            /// <summary>以 score owner 编码器发布 score:GameOver（真实线上字节，契约 score §4）。</summary>
            public void PublishScoreGameOver(int finalScore, int kills, bool isNewHigh)
            {
                var buf = ScoreMessagePayload.GameOver(finalScore, kills, isNewHigh);
                Host.Messages.Publish(ScoreId, ScoreMod.GameOverEvent, 1, in buf);
            }

            /// <summary>以 enemy owner 编码器发布 enemy:EnemyKilled（给 score 累计本局分数，契约 enemy §5）。</summary>
            public void KillEnemy(byte kind, int scoreValue)
            {
                var buf = EnemyMessagePayload.EnemyKilled(kind, scoreValue, 0f, 0f, 0f);
                Host.Messages.Publish(EnemyId, EnemyMod.EnemyKilledEvent, 1, in buf);
            }
        }

        // ---- 注册与默认状态 ----

        [Test]
        public static void Register_DefaultState_EntityAndSubscriptions()
        {
            var f = Fixture.Create();
            Assert.True(GameMod.GameEntityId != 0, "相位单实体未创建");
            var s = f.State();
            Assert.Equal((byte)GamePhase.Menu, s.Phase);       // 初始 Menu（契约 §2）
            Assert.Equal((byte)GameReason.Manual, s.Reason);   // 初始 Manual
            Assert.Equal(0, f.Host.Systems.CountOf(GameId));   // 无周期系统（契约 §2）
            Assert.Equal(1, f.Host.Messages.SubscriptionCount(GameId)); // 只订阅 score:GameOver（方向约定，契约头部）
            // 实体：player(1) + enemy 生成器(1) + weapon 域(1) + weapon 槽(4) + item 生成器(1) + score(1) + game(1) = 10
            Assert.Equal(10, f.World.EntityCount);
            Assert.True(!f.CallBool(GameMod.PauseCap), "Menu 相位下 pause 应被拒绝");
            Assert.True(!f.CallBool(GameMod.ResumeCap), "Menu 相位下 resume 应被拒绝");
        }

        // ---- 开局编排（契约 §3：game:start 依次调组件 Mod reset + set_spawning(true)） ----

        [Test]
        public static void Start_OrchestratesAllResets_PhasePlaying()
        {
            var f = Fixture.Create();

            // 扰动各 Mod 状态（验证 reset 真实生效）：
            f.KillEnemy(0, 100);                                     // score 本局 100
            f.KillEnemy(0, 100);                                     // score 本局 200
            var p = new Entity(PlayerMod.LocalPlayerEntityId);
            f.World.Add(p, new PlayerHealth { Current = 10, Max = PlayerConfig.MaxHealth }); // 玩家残血
            var se = new Entity(EnemyMod.SpawnerEntityId);
            var sp = f.World.Get<EnemySpawner>(se);
            sp.ActiveCount = 3; f.World.Add(se, sp);                 // 敌人生成器计数 3
            var ie = new Entity(ItemMod.SpawnerEntityId);
            var ip = f.World.Get<ItemSpawner>(ie);
            ip.ActiveCount = 2; ip.NextSpawnIndex = 2; ip.Timer = 0.5f; f.World.Add(ie, ip); // 道具生成器脏状态
            // 手造 1 个敌人实体（验证 enemy:reset 清场）
            var fakeEnemy = f.World.CreateEntity();
            f.World.Add(fakeEnemy, new EnemyType { Kind = 0 });
            f.World.Add(fakeEnemy, new EnemyHealth { Current = 100, Max = 100, ScoreValue = 100 });

            // 快照消息计数 → game:start
            var scBefore = f.ScoreChangedEvents;
            var hpBefore = f.HealthChangedEvents;
            var stBefore = f.StaminaChangedEvents;
            var zcBefore = f.ZombieCountEvents;
            var amBefore = f.AmmoChangedEvents;
            var wsBefore = f.WeaponSwitchedEvents;
            Assert.True(f.CallBool(GameMod.StartCap), "game:start 应 ok");

            // 相位迁移（契约 §3：写 Phase=Playing → 发 StateChanged(Playing, Started)）
            var s = f.State();
            Assert.Equal((byte)GamePhase.Playing, s.Phase);
            Assert.Equal((byte)GameReason.Started, s.Reason);
            Assert.Equal(1, f.StateChangedEvents);
            Assert.Equal((uint)GamePhase.Playing, f.LastPhase);
            Assert.Equal((uint)GameReason.Started, f.LastReason);

            // score:reset 生效（契约 §3：本局清零）
            Assert.True(f.World.TryGet<ScoreState>(new Entity(ScoreMod.ScoreEntityId), out var score));
            Assert.Equal(0, score.Current);
            Assert.Equal(200, score.HighScore); // 最高分跨局保留（score 契约 §8）
            Assert.True(f.ScoreChangedEvents > scBefore, "score:reset 未发布 ScoreChanged");

            // player:reset 生效（满血 + 发布 HealthChanged/StaminaChanged）
            Assert.True(f.World.TryGet<PlayerHealth>(p, out var hp));
            Assert.Equal(PlayerConfig.MaxHealth, hp.Current);
            Assert.True(f.HealthChangedEvents > hpBefore, "player:reset 未发布 HealthChanged");
            Assert.True(f.StaminaChangedEvents > stBefore, "player:reset 未发布 StaminaChanged");

            // enemy:reset 生效（清场 + 计数归零 + 发布 ZombieCountChanged(0)）
            Assert.True(!f.World.Exists(fakeEnemy), "enemy:reset 未销毁敌人实体");
            Assert.Equal(0, f.World.Get<EnemySpawner>(se).ActiveCount);
            Assert.True(f.ZombieCountEvents > zcBefore, "enemy:reset 未发布 ZombieCountChanged");

            // enemy:set_spawning(true)（契约 §3 相位开关）
            Assert.True(f.World.Get<EnemySpawner>(se).Enabled, "enemy:set_spawning(true) 未生效");

            // item:reset 生效（ActiveCount/NextSpawnIndex/Timer 复位）
            Assert.Equal(0, f.World.Get<ItemSpawner>(ie).ActiveCount);
            Assert.Equal(0, f.World.Get<ItemSpawner>(ie).NextSpawnIndex);
            Assert.Equal(ItemConfig.FirstDelay, f.World.Get<ItemSpawner>(ie).Timer);
            // item:set_spawning(true)
            Assert.True(f.World.Get<ItemSpawner>(ie).Enabled, "item:set_spawning(true) 未生效");

            // weapon:reset 生效（发布 AmmoChanged×4 + WeaponSwitched）
            Assert.True(f.AmmoChangedEvents >= amBefore + WeaponConfig.SlotCount, "weapon:reset 未发布 4 槽 AmmoChanged");
            Assert.True(f.WeaponSwitchedEvents >= wsBefore + 1, "weapon:reset 未发布 WeaponSwitched");
        }

        [Test]
        public static void Start_PlayerResetSpawnPoint_PositionRestored()
        {
            var f = Fixture.Create();
            // 扰动玩家位置（验证 game:start 传出生点给 player:reset）
            var p = new Entity(PlayerMod.LocalPlayerEntityId);
            f.World.Add(p, new Position3 { X = 10f, Y = 5f, Z = -8f });
            Assert.True(f.CallBool(GameMod.StartCap));
            Assert.True(f.World.TryGet<Position3>(p, out var pos));
            Assert.Equal(GameConfig.SpawnX, pos.X); // 契约 §3：player:reset(出生点)
            Assert.Equal(GameConfig.SpawnY, pos.Y);
            Assert.Equal(GameConfig.SpawnZ, pos.Z);
        }

        [Test]
        public static void Start_ComponentResetRejected_ReturnsFalse_PhaseUnchanged()
        {
            var f = Fixture.Create();
            // 组件 Mod 拒绝 reset 的防御（契约 §3：任一编排步骤失败 → [false] 且不改相位）：
            // 玩家实体缺失 → player:reset 返回 [false]（游戏 DAG 保证组件 Mod 无法在 game 存活时卸载，
            // 故用能力侧拒绝模拟依赖未就绪的失败路径）
            f.World.Destroy(new Entity(PlayerMod.LocalPlayerEntityId));

            Assert.True(!f.CallBool(GameMod.StartCap), "组件 reset 被拒时 game:start 应 [false]");
            var s = f.State();
            Assert.Equal((byte)GamePhase.Menu, s.Phase); // 不改相位（契约 §3 防御）
            Assert.Equal(0, f.StateChangedEvents);       // 不发 StateChanged
        }

        [Test]
        public static void Start_StateEntityMissing_ReturnsFalse()
        {
            var f = Fixture.Create();
            f.World.Destroy(new Entity(GameMod.GameEntityId)); // 相位单实体缺失（防御分支）
            Assert.True(!f.CallBool(GameMod.StartCap), "状态实体缺失时 game:start 应 [false]");
            Assert.Equal(0, f.StateChangedEvents);
        }

        // ---- 相位迁移：Playing → GameOver（score:GameOver 订阅收尾）→ Menu ----

        [Test]
        public static void GameOver_Subscribed_StopsSpawning_PhaseGameOver()
        {
            var f = Fixture.Create();
            Assert.True(f.CallBool(GameMod.StartCap));
            f.StateChangedEvents = 0; // 只观察 GameOver 收尾的发布

            f.PublishScoreGameOver(1200, 12, true); // score owner 编码器（契约 score §4）

            var s = f.State();
            Assert.Equal((byte)GamePhase.GameOver, s.Phase);
            Assert.Equal((byte)GameReason.PlayerDied, s.Reason);
            Assert.Equal(1, f.StateChangedEvents);
            Assert.Equal((uint)GamePhase.GameOver, f.LastPhase);
            Assert.Equal((uint)GameReason.PlayerDied, f.LastReason);

            // 收尾：enemy/item 生成关闭（契约 §3 OnScoreGameOver）
            var se = new Entity(EnemyMod.SpawnerEntityId);
            Assert.True(!f.World.Get<EnemySpawner>(se).Enabled, "GameOver 后 enemy 生成应关闭");
            var ie = new Entity(ItemMod.SpawnerEntityId);
            Assert.True(!f.World.Get<ItemSpawner>(ie).Enabled, "GameOver 后 item 生成应关闭");
        }

        [Test]
        public static void GameOver_Twice_HandledOnce()
        {
            var f = Fixture.Create();
            Assert.True(f.CallBool(GameMod.StartCap));
            f.PublishScoreGameOver(100, 1, false);
            f.StateChangedEvents = 0;
            f.PublishScoreGameOver(100, 1, false); // 重复 GameOver（防重放）
            Assert.Equal(0, f.StateChangedEvents); // 已 GameOver → 不再收尾/发布
            Assert.Equal((byte)GamePhase.GameOver, f.State().Phase);
        }

        [Test]
        public static void GameOver_BadPayload_Ignored()
        {
            var f = Fixture.Create();
            Assert.True(f.CallBool(GameMod.StartCap));
            f.StateChangedEvents = 0; // 只观察坏载荷是否触发收尾
            // 漂移载荷（缺字段 3 isNewHigh）：game 解析应拒绝（§14.11.3 漂移检出）
            var w = new PayloadWriter();
            w.WriteInt32(1, 1200);
            w.WriteInt32(2, 12);
            var buf = w.ToBuffer();
            f.Host.Messages.Publish(ScoreId, ScoreMod.GameOverEvent, 1, in buf);
            Assert.Equal((byte)GamePhase.Playing, f.State().Phase); // 相位不变
            Assert.Equal(0, f.StateChangedEvents);
        }

        [Test]
        public static void GameOver_ThenToMenu_PhaseMenu()
        {
            var f = Fixture.Create();
            Assert.True(f.CallBool(GameMod.StartCap));
            f.PublishScoreGameOver(600, 6, true);
            Assert.Equal((byte)GamePhase.GameOver, f.State().Phase);

            Assert.True(f.CallBool(GameMod.ToMenuCap), "GameOver 后 to_menu 应 ok");
            var s = f.State();
            Assert.Equal((byte)GamePhase.Menu, s.Phase);
            Assert.Equal((byte)GameReason.ToMenu, s.Reason);
            Assert.Equal((uint)GamePhase.Menu, f.LastPhase); // 最近一次 StateChanged(Menu, ToMenu)
            Assert.Equal((uint)GameReason.ToMenu, f.LastReason);
        }

        [Test]
        public static void ToMenu_FromPlaying_Direct()
        {
            var f = Fixture.Create();
            Assert.True(f.CallBool(GameMod.StartCap));
            Assert.True(f.CallBool(GameMod.ToMenuCap), "Playing 中 to_menu 应 ok（ResultView 退出按钮回调侧）");
            Assert.Equal((byte)GamePhase.Menu, f.State().Phase);
            Assert.Equal((uint)GameReason.ToMenu, f.LastReason);
        }

        // ---- pause / resume（契约 §3：生成开关 + 相位迁移 + StateChanged） ----

        [Test]
        public static void Pause_StopsSpawning_PhasePaused()
        {
            var f = Fixture.Create();
            Assert.True(f.CallBool(GameMod.StartCap));
            Assert.True(f.CallBool(GameMod.PauseCap), "Playing 中 pause 应 ok");

            var s = f.State();
            Assert.Equal((byte)GamePhase.Paused, s.Phase);
            Assert.Equal((byte)GameReason.Paused, s.Reason);
            Assert.Equal((uint)GamePhase.Paused, f.LastPhase);
            Assert.Equal((uint)GameReason.Paused, f.LastReason);

            var se = new Entity(EnemyMod.SpawnerEntityId);
            Assert.True(!f.World.Get<EnemySpawner>(se).Enabled, "pause 后 enemy 生成应关闭");
            Assert.Equal(0f, f.World.Get<EnemySpawner>(se).Timer); // Timer 清空（enemy 契约 §4 暂停语义）
            var ie = new Entity(ItemMod.SpawnerEntityId);
            Assert.True(!f.World.Get<ItemSpawner>(ie).Enabled, "pause 后 item 生成应关闭");
        }

        [Test]
        public static void Resume_StartsSpawning_PhasePlaying()
        {
            var f = Fixture.Create();
            Assert.True(f.CallBool(GameMod.StartCap));
            Assert.True(f.CallBool(GameMod.PauseCap));
            Assert.True(f.CallBool(GameMod.ResumeCap), "Paused 中 resume 应 ok");

            var s = f.State();
            Assert.Equal((byte)GamePhase.Playing, s.Phase);
            Assert.Equal((byte)GameReason.Resumed, s.Reason);
            Assert.Equal((uint)GamePhase.Playing, f.LastPhase);
            Assert.Equal((uint)GameReason.Resumed, f.LastReason);

            var se = new Entity(EnemyMod.SpawnerEntityId);
            Assert.True(f.World.Get<EnemySpawner>(se).Enabled, "resume 后 enemy 生成应恢复");
            var ie = new Entity(ItemMod.SpawnerEntityId);
            Assert.True(f.World.Get<ItemSpawner>(ie).Enabled, "resume 后 item 生成应恢复");
        }

        [Test]
        public static void PauseResume_NoStateChanged_WhenRejected()
        {
            var f = Fixture.Create();
            Assert.True(!f.CallBool(GameMod.PauseCap));  // Menu 下 pause 拒绝
            Assert.True(!f.CallBool(GameMod.ResumeCap)); // Menu 下 resume 拒绝
            Assert.Equal(0, f.StateChangedEvents);

            Assert.True(f.CallBool(GameMod.StartCap));
            Assert.True(!f.CallBool(GameMod.ResumeCap)); // Playing 下 resume 拒绝（未暂停）
            Assert.True(f.StateChangedEvents >= 1);      // 只有 start 的发布
            Assert.Equal((uint)GamePhase.Playing, f.LastPhase);
        }

        // ---- game:status（契约 §4：QA/调试） ----

        [Test]
        public static void Status_ReportsCurrentPhase()
        {
            var f = Fixture.Create();
            Assert.True(Com.Zombtoy.Game.CapabilityShapes.TryReadStatusRow(f.Call(GameMod.StatusCap), out var p0));
            Assert.Equal((uint)GamePhase.Menu, p0);

            Assert.True(f.CallBool(GameMod.StartCap));
            Assert.True(Com.Zombtoy.Game.CapabilityShapes.TryReadStatusRow(f.Call(GameMod.StatusCap), out var p1));
            Assert.Equal((uint)GamePhase.Playing, p1);

            Assert.True(f.CallBool(GameMod.PauseCap));
            Assert.True(Com.Zombtoy.Game.CapabilityShapes.TryReadStatusRow(f.Call(GameMod.StatusCap), out var p2));
            Assert.Equal((uint)GamePhase.Paused, p2);
        }

        // ---- 契约测试向量（§14.11.3）：owner 规范字节 ↔ 样例一致 ----

        [Test]
        public static void TestVectors_DataCodecShapes_RoundTrip()
        {
            foreach (var v in GameTestVectors.All())
            {
                if (v.ContractId == GameTestVectors.StateChangedMsgContract)
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
            // 消费方（ui 各 View 窗口路由 / 调试）各自重复定义的解析器（Rule 19）
            // 解析 owner 规范字节，与样例一致 → "可重复定义未漂移"（§14.11.3）
            foreach (var v in GameTestVectors.All())
            {
                if (v.ContractId != GameTestVectors.StateChangedMsgContract) continue;
                Assert.True(StateChangedParser.TryRead(v.Bytes, out var phase, out var reason), $"解析失败: {v}");
                Assert.Equal((uint)v.Sample[0]!, phase);
                Assert.Equal((uint)v.Sample[1]!, reason);
            }
        }

        [Test]
        public static void TestVectors_Drift_NegativeCase()
        {
            // 漂移负向用例（summary §6）：owner 改了布局（StateChanged 缺 reason 字段）应被消费方解析器检出
            var w = new PayloadWriter();
            w.WriteUInt32(1, 1);
            Assert.True(!StateChangedParser.TryRead(w.ToBuffer().ToArray(), out _, out _),
                "漂移载荷（缺字段2 reason）未被检出");
        }

        // ---- 跨 owner 字节消费一致性（Rule 19：game 解析 score:GameOver 消息向量） ----

        [Test]
        public static void Consumes_ScoreGameOver_OwnerBytes()
        {
            var parsed = 0;
            foreach (var v in ScoreTestVectors.All())
            {
                if (v.ContractId != ScoreTestVectors.GameOverMsgContract) continue;
                Assert.True(GameFlowSystem.TryReadScoreGameOver(new PayloadReader(v.Bytes)),
                    $"score_gameover_msg 解析失败: {v}");
                parsed++;
            }
            Assert.True(parsed >= 2, $"应解析到至少 2 条 score_gameover_msg 向量，实际 {parsed}");
        }

        // ---- 卸载清理（Rule 20 / §9.2 / §13.5） ----

        [Test]
        public static void Unload_CleansState_NoSubscriptions()
        {
            var f = Fixture.Create();
            Assert.True(f.CallBool(GameMod.StartCap));
            var gameEntityId = GameMod.GameEntityId;

            f.Host.Manager.Unload(GameId);

            Assert.True(!f.Host.Manager.IsLoaded(GameId));
            Assert.Equal(0, f.Host.Systems.CountOf(GameId));             // 无系统（本来 0）
            Assert.Equal(0, f.Host.Messages.SubscriptionCount(GameId));  // 订阅随卸载强制退订（§13.5）
            Assert.True(!f.World.Exists(new Entity(gameEntityId)));      // 单实体随卸载强制销毁（§9.2）
            Assert.Equal(9, f.World.EntityCount);                        // 仅剩其余组件 Mod 实体（10 - game 1）
            Assert.Equal(0u, GameMod.GameEntityId);                      // 静态桥清空
            Assert.True(GameMod.Context is null);
            Assert.True(GameMod.World is null);
        }

        // ---- 消费方风格解析器（Rule 19：结构相同、定义独立，模拟 ui 各自重复定义） ----

        /// <summary>消费方 ui（窗口路由）：game:StateChanged 解析器（[1]=phase [2]=reason，契约 §5）。</summary>
        public sealed class StateChangedParser
        {
            public static bool TryRead(byte[] bytes, out uint phase, out uint reason)
            {
                phase = 0; reason = 0;
                var reader = new PayloadReader(bytes);
                if (!reader.TryReadUInt32(1, out phase)) return false;
                if (!reader.TryReadUInt32(2, out reason)) return false;
                return true;
            }
        }
    }
}
