namespace Com.Zombtoy.Score
{
    /// <summary>
    /// 分数行为参数（CONTRACT.md §8 配置常量表；结构固定、数值可调，改动需升 CONTRACT 版本与测试向量，summary §0.8）。
    /// 数值来源（只读机制数值，不搬代码）：原版 ScoreManager.cs（enemyKillScore=100 / bossKillScore=500 / comboMultiplier=2）。
    /// </summary>
    public static class ScoreConfig
    {
        /// <summary>普通击杀分（100，原版 enemyKillScore=100）。契约 §8：实际分值由 enemy:EnemyKilled.scoreValue
        /// 携带（enemy 侧配置），本常量仅作文档化默认值与测试断言参考。</summary>
        public const int EnemyKillScore = 100;

        /// <summary>首领击杀分（500，原版 bossKillScore=500）。同上，由 enemy:EnemyKilled.scoreValue 携带。</summary>
        public const int BossKillScore = 500;

        /// <summary>连击倍率（原版 comboMultiplier=2）。契约 §2：原版仅配置未生效、v1 不实现连击逻辑；
        /// ScoreState.Combo 字段预埋，后续 append-only 扩展（score:ScoreChanged 加字段或新消息）。</summary>
        public const int ComboMultiplier = 2;

        /// <summary>最高分持久化 key（原版 PlayerPrefs "HighScore"，契约 §8；Unity 宿主 PlayerPrefs 实现使用）。</summary>
        public const string HighScoreKey = "HighScore";

        /// <summary>提交 leaderboard 的默认玩家名（v1 无玩家名输入；对齐 leaderboard CONTRACT §7 PlayerName，
        /// Rule 19：消费方各自重复定义）。</summary>
        public const string PlayerName = "player";
    }
}
