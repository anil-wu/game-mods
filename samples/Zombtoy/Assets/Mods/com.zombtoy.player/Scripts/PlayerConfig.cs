namespace Com.Zombtoy.Player
{
    /// <summary>
    /// 玩家行为参数（契约 §9 配置常量表，来源原版 PlayerMovement.cs / PlayerHealth.cs）。
    /// 结构固定；数值改动需同步升 CONTRACT 版本与测试向量（summary §0.8）。
    /// </summary>
    public static class PlayerConfig
    {
        /// <summary>移动速度（原版 speed = 6f）。</summary>
        public const float MoveSpeed = 6.0f;

        /// <summary>冲刺倍率（原版 sprint_Speed = speed * 1.2）。</summary>
        public const float SprintMultiplier = 1.2f;

        /// <summary>冲刺体力消耗（/秒，原版 Stamina -= dt * 0.5）。</summary>
        public const float StaminaDrainPerSec = 0.5f;

        /// <summary>非冲刺体力恢复（/秒，原版 Stamina += dt * 0.15）。</summary>
        public const float StaminaRegenPerSec = 0.15f;

        /// <summary>最大生命（原版 startingHealth = 100）。</summary>
        public const int MaxHealth = 100;

        /// <summary>最大体力（原版 Stamina = 1f，体力条 [0,1]）。</summary>
        public const float MaxStamina = 1.0f;

        /// <summary>默认出生点（原版 SpawnPoint，game:start 经 player:reset 传入）。</summary>
        public const float SpawnX = 0f;
        public const float SpawnY = 1f;
        public const float SpawnZ = 0f;
    }
}
