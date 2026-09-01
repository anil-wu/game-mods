using Game.ECS;

namespace Com.Zombtoy.Score
{
    /// <summary>
    /// 分数 ECS 组件（CONTRACT.md §1，仅本 Mod 读写，其他 Mod 禁止触碰 World）。
    /// 单机 Host：无 [NetworkReplicated] 标签（summary §0.1）。
    /// 单实体（ScoreMod.ScoreEntityId，Register 创建常驻；score:reset 只清字段不销毁，对齐原版 ResetScore）。
    /// 组件类型在 Register 里经 context.Ecs.RegisterComponent 注册，卸载由 Core 强制回收（§9.2）。
    /// </summary>

    /// <summary>分数状态（契约 §1）：Current 本局分数 / HighScore 最高分（跨局持久化，契约 §8）/
    /// Kills 击杀数 / IsNewHigh 是否刷新最高分 / Combo 连击计数（v1 预埋：契约 §2 连击原版仅配置未生效、
    /// v1 不实现，后续 append-only 扩展 score:ScoreChanged 加字段或新消息；score:reset 清零）。</summary>
    public struct ScoreState : IComponent
    {
        public int Current;
        public int HighScore;
        public int Kills;
        public bool IsNewHigh;
        public int Combo;
    }

    /// <summary>结算快照（契约 §1，供 Result 展示）：FinalScore 终局分数 / Kills 击杀数 /
    /// IsNewHigh 是否新纪录 / Finalized 是否已结算（score:get_state 以此决定返回 RunResult 或 ScoreState，契约 §3）。</summary>
    public struct RunResult : IComponent
    {
        public int FinalScore;
        public int Kills;
        public bool IsNewHigh;
        public bool Finalized;
    }
}
