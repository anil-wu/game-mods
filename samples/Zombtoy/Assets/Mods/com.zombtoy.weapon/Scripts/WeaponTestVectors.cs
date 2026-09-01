using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Weapon
{
    /// <summary>
    /// com.zombtoy.weapon 的契约测试向量（§14.11.3）：随版本发布，供消费方
    /// （item.ItemPickup / game.GameFlow / ui.HudBinders 等）CI 校验各自重复定义的解析器
    /// 未与本 Mod 的字节布局漂移。
    ///
    /// 两类向量（契约 §10）：
    /// - DataCodec 形状（ModCall args/rows）：TestVector.Of —— owner 编码器（DataCodec）产出规范字节；
    /// - 消息载荷（字段编号形状）：手工 new TestVector —— owner 消息编码器（WeaponMessagePayload）产出规范字节。
    /// append-only：新向量只增不改。
    /// </summary>
    public static class WeaponTestVectors
    {
        // DataCodec 形状契约名（对应 CONTRACT.md §4 能力 args/rows / §10）
        public const string AddAmmoArgsContract = "weapon_add_ammo_args";
        public const string ResetArgsContract = "weapon_reset_args";
        public const string GetStateRowContract = "weapon_get_state_row";
        public const string ShotRequestContract = "weapon_shot_request";

        // 消息载荷契约名（对应 CONTRACT.md §5）
        public const string AmmoMsgContract = "weapon_ammo_msg";
        public const string SwitchedMsgContract = "weapon_switched_msg";

        /// <summary>全部测试向量（常规/边界；append-only）。</summary>
        public static TestVector[] All() => new[]
        {
            // ---- ModCall 形状（DataCodec 规范字节） ----
            TestVector.Of(AddAmmoArgsContract, CapabilityShapes.AddAmmoArgs(1)),
            TestVector.Of(AddAmmoArgsContract, CapabilityShapes.AddAmmoArgs(2)),
            TestVector.Of(ResetArgsContract, System.Array.Empty<object?>()),
            TestVector.Of(GetStateRowContract, CapabilityShapes.GetStateRow(0u, 4u, 25, 50, 25)), // 契约 §10 样例
            TestVector.Of(GetStateRowContract, CapabilityShapes.GetStateRow(1u, 4u, 8, 24, 8)),   // 霰弹行
            TestVector.Of(ShotRequestContract, CapabilityShapes.ShotRequestArgs(1u, 10u, true, 0f, 0f, 2f, 0u)), // 契约 §10 样例
            TestVector.Of(ShotRequestContract, CapabilityShapes.ShotRequestArgs(1u, 0u, false, 0f, 0f, 100f, 2u)), // 火箭筒未命中瞄准

            // ---- 消息载荷（owner 消息编码器产出规范字节，消费方用 PayloadReader 解析） ----
            new TestVector(AmmoMsgContract, new object?[] { 0u, 25, 50, 25 },
                WeaponMessagePayload.AmmoChanged(0u, 25, 50, 25).ToArray()), // 契约 §10 样例
            new TestVector(AmmoMsgContract, new object?[] { 2u, 0, 5, 1 },
                WeaponMessagePayload.AmmoChanged(2u, 0, 5, 1).ToArray()),    // 火箭筒打空边界
            new TestVector(SwitchedMsgContract, new object?[] { 1u },
                WeaponMessagePayload.WeaponSwitched(1u).ToArray()),          // 契约 §10 样例
            new TestVector(SwitchedMsgContract, new object?[] { 0u },
                WeaponMessagePayload.WeaponSwitched(0u).ToArray()),
        };
    }
}
