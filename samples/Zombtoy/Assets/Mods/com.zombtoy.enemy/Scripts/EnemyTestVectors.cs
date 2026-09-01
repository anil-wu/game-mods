using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// com.zombtoy.enemy 的契约测试向量（§14.11.3）：随版本发布，供消费方
    /// （weapon.FireSystem / weapon.ProjectileSystem / game.GameFlow / ui.HudZombies / score.ScoreSystem 等）
    /// CI 校验各自重复定义的解析器未与本 Mod 的字节布局漂移。
    ///
    /// 两类向量（契约 §10）：
    /// - DataCodec 形状（ModCall args/rows）：TestVector.Of —— owner 编码器（DataCodec）产出规范字节；
    /// - 消息载荷（字段编号形状）：手工 new TestVector —— owner 消息编码器（EnemyMessagePayload）产出规范字节。
    /// append-only：新向量只增不改。
    /// </summary>
    public static class EnemyTestVectors
    {
        // DataCodec 形状契约名（对应 CONTRACT.md §4 能力 args/rows）
        public const string DamageArgsContract = "enemy_damage_args";
        public const string DamageArgsBlastContract = "enemy_damage_args_blast";
        public const string SlowArgsContract = "enemy_slow_args";
        public const string GetAllRowContract = "enemy_get_all_row";
        public const string GetCountRowContract = "enemy_get_count_row";
        public const string SetSpawningArgsContract = "enemy_set_spawning_args";

        // 消息载荷契约名（对应 CONTRACT.md §5）
        public const string KilledMsgContract = "enemy_killed_msg";
        public const string CountMsgContract = "enemy_count_msg";

        /// <summary>全部测试向量（常规/边界；append-only）。</summary>
        public static TestVector[] All() => new[]
        {
            // ---- ModCall 形状（DataCodec 规范字节） ----
            TestVector.Of(DamageArgsContract, CapabilityShapes.DamageArgs(10u, 30, 5u, 0u)),
            TestVector.Of(DamageArgsContract, CapabilityShapes.DamageArgs(10u, 0, 5u, 0u)),
            TestVector.Of(DamageArgsBlastContract, CapabilityShapes.DamageArgs(10u, 80, 5u, 1u)),
            TestVector.Of(SlowArgsContract, CapabilityShapes.SlowArgs(10u, 0.6f, 1.5f)),
            TestVector.Of(GetAllRowContract, CapabilityShapes.GetAllRow(10u, 1f, 0f, 2f, 3u, true, 500, 500)),
            TestVector.Of(GetAllRowContract, CapabilityShapes.GetAllRow(11u, -2f, 0f, 5f, 7u, false, 0, 100)),
            TestVector.Of(GetCountRowContract, CapabilityShapes.GetCountRow(7)),
            TestVector.Of(SetSpawningArgsContract, CapabilityShapes.SetSpawningArgs(true)),
            TestVector.Of(SetSpawningArgsContract, CapabilityShapes.SetSpawningArgs(false)),

            // ---- 消息载荷（owner 消息编码器产出规范字节，消费方用 PayloadReader 解析） ----
            new TestVector(KilledMsgContract, new object?[] { 6u, 500, 1f, 0f, 2f },
                EnemyMessagePayload.EnemyKilled(6, 500, 1f, 0f, 2f).ToArray()),
            new TestVector(KilledMsgContract, new object?[] { 0u, 100, -3f, 0f, 4f },
                EnemyMessagePayload.EnemyKilled(0, 100, -3f, 0f, 4f).ToArray()),
            new TestVector(CountMsgContract, new object?[] { 7 },
                EnemyMessagePayload.ZombieCountChanged(7).ToArray()),
            new TestVector(CountMsgContract, new object?[] { 0 },
                EnemyMessagePayload.ZombieCountChanged(0).ToArray()),
        };
    }
}
