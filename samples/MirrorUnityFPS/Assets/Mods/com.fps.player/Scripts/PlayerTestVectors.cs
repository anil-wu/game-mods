using Game.Mod.Contract.Wire;

namespace Com.Fps.Player
{
    /// <summary>
    /// com.fps.player 的契约测试向量（§14.11.3）：随版本发布，供消费方（weapon/npc/inventory）
    /// CI 校验各自重复定义的解析器未与本 Mod 的字节布局漂移。
    /// </summary>
    public static class PlayerTestVectors
    {
        /// <summary>PositionRow 行契约：[0]=EntityId(u32) [1]=X(f32) [2]=Y(f32) [3]=Z(f32) [4]=Yaw(f32) [5]=Alive(bool)。</summary>
        public const string PositionRowContract = "position_row";

        /// <summary>全部测试向量（覆盖常规/零值/边界；append-only：新向量只增不改）。</summary>
        public static TestVector[] All() => new[]
        {
            TestVector.Of(PositionRowContract, CapabilityShapes.PositionRow(42u, 1.5f, 2.5f, 3.5f, 90f, true)),
            TestVector.Of(PositionRowContract, CapabilityShapes.PositionRow(7u, -3f, 0f, 8f, 0f, false)),
            TestVector.Of(PositionRowContract, CapabilityShapes.PositionRow(1000u, 0f, 1.0f, 0f, 270.5f, true)),
        };
    }
}
