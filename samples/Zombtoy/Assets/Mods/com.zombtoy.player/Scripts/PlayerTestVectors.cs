using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Player
{
    /// <summary>
    /// com.zombtoy.player 的契约测试向量（§14.11.3）：随版本发布，供消费方
    /// （enemy.EnemyAttackSystem / item.ItemPickup / game.GameFlow / ui.HudBinders / score 等）
    /// CI 校验各自重复定义的解析器未与本 Mod 的字节布局漂移。
    ///
    /// 两类向量（契约 §7）：
    /// - DataCodec 形状（ModCall args/rows）：TestVector.Of —— owner 编码器（DataCodec）产出规范字节；
    /// - 消息载荷（字段编号形状）：手工 new TestVector —— owner 消息编码器（PlayerMessagePayload）产出规范字节。
    /// append-only：新向量只增不改。
    /// </summary>
    public static class PlayerTestVectors
    {
        // DataCodec 形状契约名（对应 CONTRACT.md §3 能力 args/返回行）
        public const string DamageArgsContract = "player_damage_args";
        public const string HealArgsContract = "player_heal_args";
        public const string ResetArgsContract = "player_reset_args";
        public const string GetStateRowContract = "player_get_state_row";
        public const string GetPositionRowContract = "player_get_position_row";

        // 消息载荷契约名（对应 CONTRACT.md §4）
        public const string HealthMsgContract = "player_health_msg";
        public const string StaminaMsgContract = "player_stamina_msg";
        public const string DiedMsgContract = "player_died_msg";

        /// <summary>全部测试向量（常规/边界；append-only）。</summary>
        public static TestVector[] All() => new[]
        {
            // ---- ModCall 形状（DataCodec 规范字节） ----
            TestVector.Of(DamageArgsContract, CapabilityShapes.DamageArgs(25, 7u)),
            TestVector.Of(DamageArgsContract, CapabilityShapes.DamageArgs(0, 0u)),
            TestVector.Of(HealArgsContract, CapabilityShapes.HealArgs(20)),
            TestVector.Of(ResetArgsContract, CapabilityShapes.ResetArgs(0f, 1f, 0f)),
            TestVector.Of(GetStateRowContract, CapabilityShapes.StateRow(100, 100, 1f, 1f, true)),
            TestVector.Of(GetStateRowContract, CapabilityShapes.StateRow(45, 100, 0.4f, 1f, true)),
            TestVector.Of(GetPositionRowContract, CapabilityShapes.PositionRow(0f, 1f, 0f, true)),
            TestVector.Of(GetPositionRowContract, CapabilityShapes.PositionRow(12.5f, 0f, -8f, false)),

            // ---- 消息载荷（owner 消息编码器产出规范字节，消费方用 PayloadReader 解析） ----
            new TestVector(HealthMsgContract, new object?[] { 95, 100 },
                PlayerMessagePayload.HealthChanged(95, 100).ToArray()),
            new TestVector(HealthMsgContract, new object?[] { 0, 100 },
                PlayerMessagePayload.HealthChanged(0, 100).ToArray()),
            new TestVector(StaminaMsgContract, new object?[] { 0.4f, 1f },
                PlayerMessagePayload.StaminaChanged(0.4f, 1f).ToArray()),
            new TestVector(DiedMsgContract, new object?[] { 7u },
                PlayerMessagePayload.Died(7u).ToArray()),
        };
    }
}
