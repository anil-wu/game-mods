namespace Com.Zombtoy.Game
{
    /// <summary>
    /// 相位/对局行为参数（CONTRACT.md §8 行为参数；结构固定、数值可调，改动需升 CONTRACT 版本与测试向量，summary §0.8）。
    /// 出生点对齐 player CONTRACT §9（SpawnPoint=(0,1,0)，game:start 调 player:reset 传入）。
    /// </summary>
    public static class GameConfig
    {
        /// <summary>初始相位（Menu，契约 §2：单实体 Register 创建初始 Menu）。</summary>
        public const byte InitialPhase = (byte)GamePhase.Menu;

        /// <summary>初始原因（Manual=0，契约 §1）。</summary>
        public const byte InitialReason = (byte)GameReason.Manual;

        /// <summary>玩家出生点 X（对齐 player CONTRACT §9 SpawnPoint=(0,1,0)）。</summary>
        public const float SpawnX = 0f;

        /// <summary>玩家出生点 Y（对齐 player CONTRACT §9 SpawnPoint=(0,1,0)）。</summary>
        public const float SpawnY = 1f;

        /// <summary>玩家出生点 Z（对齐 player CONTRACT §9 SpawnPoint=(0,1,0)）。</summary>
        public const float SpawnZ = 0f;
    }
}
