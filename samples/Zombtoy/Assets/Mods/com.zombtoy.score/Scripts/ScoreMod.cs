using System;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Score
{
    /// <summary>
    /// 分数 Mod（com.zombtoy.score）：击杀计分 / 击杀数 / 最高分 / 结算。单机 Host（summary §0.1）。
    /// 本 Mod 是消息消费方（订阅 enemy:EnemyKilled、player:Died，Rule 11 谁定义谁发布）+ 能力提供方
    /// （score:reset / score:get_state）；无周期系统（契约 §2：逻辑在消息 handler + 能力实现内，全部可无头测试）。
    /// 结算链路（summary §5.2）：player:Died → 结算（写 RunResult、发 score:GameOver、调 leaderboard:submit）→
    /// game 订阅 GameOver 收尾。
    /// 跨 Mod 一律二进制（Rule 14）：订阅消息用 PayloadReader 字段解析；调 leaderboard:submit 用 DataCodec。
    /// 不引用 enemy/player/leaderboard 程序集：能力/消息 ID 与行形状各自重复定义（Rule 12/19）。
    /// 最高分持久化（契约 §8）：经 ScorePersistence 静态桥注入——无头测试用 MemoryScorePersistence，
    /// Unity 宿主可注入 PlayerPrefs 实现（Key = ScoreConfig.HighScoreKey = "HighScore"，对齐原版 ScoreManager）。
    /// </summary>
    public sealed class ScoreMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.zombtoy.score");

        // 发布消息（谁定义谁发布，Rule 11；载荷字段布局见 CONTRACT.md §4）
        public static readonly MessageId ScoreChangedEvent = new(ModIdValue, "ScoreChanged");
        public static readonly MessageId GameOverEvent = new(ModIdValue, "GameOver");

        // 消费的对端消息（契约 §6；不引用对端程序集，Rule 12/19：MessageId 各自重复定义）
        public static readonly MessageId EnemyKilledEvent = new(new ModId("com.zombtoy.enemy"), "EnemyKilled");
        public static readonly MessageId PlayerDiedEvent = new(new ModId("com.zombtoy.player"), "Died");

        // 导出能力（契约 §3；owner = com.zombtoy.score）
        public static readonly CapabilityId ResetCap = new(ModIdValue, "reset");
        public static readonly CapabilityId GetStateCap = new(ModIdValue, "get_state");

        // 消费的对端能力（契约 §6：leaderboard:submit 提交最高分）
        public static readonly ModId LeaderboardModId = new("com.zombtoy.leaderboard");
        public static readonly CapabilityId LeaderboardSubmitCap = new(LeaderboardModId, "submit");

        // 静态桥（测试/宿主访问，Mod 内部状态，FPS 先例；Rule 12 不跨 Mod）
        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;
        public static uint ScoreEntityId { get; private set; }

        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;
            ScoreEntityId = 0;

            // 1. 组件注册（归属本 ModObject，卸载强制回收 §9.2；契约 §1）
            context.Ecs.RegisterComponent(typeof(ScoreState));
            context.Ecs.RegisterComponent(typeof(RunResult));

            // 2. 能力导出（§12.11；二进制 ABI Rule 14：DataCodec 编解码纯数据，bytes 不藏引用）
            context.Mods.Export(ResetCap, _ => DataCodec.Write(Reset()));
            context.Mods.Export(GetStateCap, _ => DataCodec.Write(GetState()));

            if (context.HasServer)
            {
                // 3. 消息订阅（归属本 ModObject，卸载强制退订 §13.5；契约 §2）
                context.Messages.Subscribe(EnemyKilledEvent, ScoreSystem.OnEnemyKilled);
                context.Messages.Subscribe(PlayerDiedEvent, GameOverSystem.OnPlayerDied);

                // 4. 单实体状态（Register 创建常驻；reset 只清字段不销毁，契约 §1 单实体）
                var e = context.Ecs.CreateEntity();
                context.Ecs.World.Add(e, new ScoreState
                {
                    Current = 0,
                    HighScore = ScorePersistence.Current.LoadHighScore(), // 最高分持久化加载（契约 §8）
                    Kills = 0,
                    IsNewHigh = false,
                    Combo = 0,
                });
                ScoreEntityId = e.Id;
            }

            context.Log.Info($"分数 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            // 对称撤销（Rule 20）：系统/实体/能力/订阅由 Core 按 ModObject 归属强制回收（§9.2/§13.5），
            // 本 Mod 只清静态桥与本地状态。最高分已随更新持久化（契约 §8），不在此额外保存。
            World = null!;
            Context = null!;
            ScoreEntityId = 0;
            context.Log.Info($"分数 Mod '{context.Info.Id}' 已注销");
        }

        // ---- 能力实现（契约 §3；同步执行，可无头测试） ----

        /// <summary>score:reset：args=[] → [ok bool]（game:start 调用，契约 §3）。
        /// Current=0、Kills=0、IsNewHigh=false、RunResult 清空、Combo=0；发 score:ScoreChanged(0, high, 0)。
        /// 最高分跨局保留（对齐原版 ResetScore：重置本局不重置 HighScore，契约 §8 持久化）。</summary>
        private static object?[] Reset()
        {
            var mod = Context;
            var world = World;
            if (mod is null || world is null || ScoreEntityId == 0) return new object?[] { false };
            var e = new Entity(ScoreEntityId);
            if (!world.TryGet<ScoreState>(e, out var state)) return new object?[] { false };

            state.Current = 0;
            state.Kills = 0;
            state.IsNewHigh = false;
            state.Combo = 0;
            world.Add(e, state);

            // RunResult 清空（Finalized=false：get_state 回到 ScoreState 分支，契约 §3）
            if (world.TryGet<RunResult>(e, out var rr))
            {
                rr.FinalScore = 0;
                rr.Kills = 0;
                rr.IsNewHigh = false;
                rr.Finalized = false;
                world.Add(e, rr);
            }

            PublishScoreChanged(mod, state); // score:ScoreChanged(0, high, 0)（对齐原版 ResetScore 发 ScoreChanged）
            return new object?[] { true };
        }

        /// <summary>score:get_state：args=[] → [score, highScore, kills, isNewHigh]（Result 初始填充，契约 §3）。
        /// RunResult.Finalized 时返回结算快照，否则返回 ScoreState。</summary>
        private static object?[] GetState()
        {
            var world = World;
            if (world is null || ScoreEntityId == 0)
                return CapabilityShapes.GetStateRow(0, 0, 0, false); // 未就绪（防御）
            var e = new Entity(ScoreEntityId);
            if (!world.TryGet<ScoreState>(e, out var state))
                return CapabilityShapes.GetStateRow(0, 0, 0, false);
            if (world.TryGet<RunResult>(e, out var rr) && rr.Finalized)
                return CapabilityShapes.GetStateRow(rr.FinalScore, state.HighScore, rr.Kills, rr.IsNewHigh);
            return CapabilityShapes.GetStateRow(state.Current, state.HighScore, state.Kills, state.IsNewHigh);
        }

        // ---- 消息发布（字段编号线格式，CONTRACT.md §4；owner 编码器唯一事实来源） ----

        /// <summary>发布 score:ScoreChanged（current/highScore/kills，契约 §4；ScoreSystem.OnEnemyKilled 与 score:reset 共用）。</summary>
        internal static void PublishScoreChanged(IModContext mod, in ScoreState state)
        {
            var buffer = ScoreMessagePayload.ScoreChanged(state.Current, state.HighScore, state.Kills);
            mod.Messages.Publish(ScoreChangedEvent, 1, in buffer);
        }
    }

    /// <summary>最高分持久化抽象（契约 §8：PlayerPrefs key "HighScore" 本端本地持久化，对齐原版 Load/SaveHighScore）。</summary>
    public interface IScorePersistence
    {
        int LoadHighScore();
        void SaveHighScore(int highScore);
    }

    /// <summary>内存持久化（无头测试默认实现；Unity 宿主可经 ScorePersistence.Set 注入 PlayerPrefs 实现，Key=ScoreConfig.HighScoreKey）。</summary>
    public sealed class MemoryScorePersistence : IScorePersistence
    {
        private int _value;

        public MemoryScorePersistence() { }
        public MemoryScorePersistence(int seedHighScore) => _value = seedHighScore;

        public int LoadHighScore() => _value;
        public void SaveHighScore(int highScore) => _value = highScore;
    }

    /// <summary>最高分持久化静态桥（Mod 内部状态，同 leaderboard LeaderboardTransport.Set 先例；测试注入，Rule 12 不跨 Mod）。</summary>
    public static class ScorePersistence
    {
        private static IScorePersistence _impl = new MemoryScorePersistence();

        public static IScorePersistence Current => _impl;

        /// <summary>注入持久化实现（Unity 宿主装配 PlayerPrefs 实现；null 回退内存实现）。</summary>
        public static void Set(IScorePersistence? impl) => _impl = impl ?? new MemoryScorePersistence();
    }
}
