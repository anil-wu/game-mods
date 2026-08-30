using Game.Mod.Contract.Wire;

namespace Com.Fps.Npc
{
    /// <summary>
    /// com.fps.npc 的契约测试向量（§14.11.3）：随版本发布，供消费方（weapon）CI 校验解析器。
    /// </summary>
    public static class NpcTestVectors
    {
        /// <summary>NpcRow 行契约：[0]=EntityId(u32) [1]=X(f32) [2]=Y(f32) [3]=Z(f32) [4]=Alive(bool)。</summary>
        public const string NpcRowContract = "npc_row";

        public static TestVector[] All() => new[]
        {
            TestVector.Of(NpcRowContract, new NpcSnapshot(11u, -2f, 0f, 6f, true).Row()),
            TestVector.Of(NpcRowContract, new NpcSnapshot(12u, 5f, 0.5f, -4f, false).Row()),
        };
    }
}
