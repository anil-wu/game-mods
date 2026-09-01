using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Item
{
    /// <summary>
    /// com.zombtoy.item 的契约测试向量（§14.11.3）：随版本发布，供消费方
    /// （game.GameFlow / ui.HudPickupHint 等）CI 校验各自重复定义的解析器
    /// 未与本 Mod 的字节布局漂移。
    ///
    /// 两类向量（契约 §10）：
    /// - DataCodec 形状（ModCall args/rows）：TestVector.Of —— owner 编码器（DataCodec）产出规范字节；
    /// - 消息载荷（字段编号形状）：手工 new TestVector —— owner 消息编码器（ItemMessagePayload）产出规范字节。
    /// append-only：新向量只增不改。
    /// </summary>
    public static class ItemTestVectors
    {
        // DataCodec 形状契约名（对应 CONTRACT.md §4 能力 args / §10）
        public const string PickupArgsContract = "item_pickup_args";
        public const string SetSpawningArgsContract = "item_set_spawning_args";

        // 消息载荷契约名（对应 CONTRACT.md §5 / §10）
        public const string PickedUpMsgContract = "item_pickedup_msg";

        /// <summary>全部测试向量（常规/边界；append-only）。</summary>
        public static TestVector[] All() => new[]
        {
            // ---- ModCall 形状（DataCodec 规范字节） ----
            TestVector.Of(PickupArgsContract, CapabilityShapes.PickupArgs(10u)), // 契约 §10 样例
            TestVector.Of(PickupArgsContract, CapabilityShapes.PickupArgs(0u)), // 无效实体边界（0 = 未绑定）
            TestVector.Of(SetSpawningArgsContract, CapabilityShapes.SetSpawningArgs(true)), // 契约 §10 样例
            TestVector.Of(SetSpawningArgsContract, CapabilityShapes.SetSpawningArgs(false)),

            // ---- 消息载荷（owner 消息编码器产出规范字节，消费方用 PayloadReader 解析） ----
            new TestVector(PickedUpMsgContract, new object?[] { 0u, 30 },
                ItemMessagePayload.PickedUp(0, 30).ToArray()), // 契约 §10 样例（血瓶 30）
            new TestVector(PickedUpMsgContract, new object?[] { 1u, 1 },
                ItemMessagePayload.PickedUp(1, 1).ToArray()),  // 弹药箱 1
        };
    }
}
