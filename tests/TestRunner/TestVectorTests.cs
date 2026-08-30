using Com.Fps.Npc;
using Com.Fps.Player;
using Game.Mod.Contract.Wire;
using WeaponPlayerSnapshot = Com.Fps.Weapon.PlayerSnapshot;
using WeaponNpcSnapshot = Com.Fps.Weapon.NpcSnapshot;
using NpcPlayerSnap = Com.Fps.Npc.NpcAiSystem.PlayerSnap;

namespace TestRunner
{
    /// <summary>
    /// 契约测试向量机制（§14.11.3）：owner 发布"样例 + 规范字节"，各消费方用自己重复定义的解析器
    /// 解析同一字节，应解出一致结果——验证"可重复定义"（Rule 19）两端未漂移，以及漂移能被检出。
    /// </summary>
    public static class TestVectorTests
    {
        /// <summary>player 的 position_row：weapon 与 npc 两个独立消费方对同一字节解出一致结果。</summary>
        [Test]
        public static void PlayerRow_AllConsumers_ParseConsistently()
        {
            var vectors = PlayerTestVectors.All();
            Assert.True(vectors.Length >= 2, "player 测试向量不足");
            foreach (var v in vectors)
            {
                Assert.Equal(PlayerTestVectors.PositionRowContract, v.ContractId);
                var row = v.DecodeFields(); // owner 规范字节 → 字段元组（消费方解析器输入）

                // 消费方 1：weapon.PlayerSnapshot（跳读 [4]=Yaw）；消费方 2：npc.PlayerSnap（独立重复定义）
                Assert.True(WeaponPlayerSnapshot.TryRead(row, out var w), $"weapon 解析失败: {v}");
                Assert.True(NpcPlayerSnap.TryRead(row, out var n), $"npc 解析失败: {v}");

                // 两端一致 = 无漂移
                Assert.Equal(w.EntityId, n.EntityId);
                Assert.Equal(w.X, n.X);
                Assert.Equal(w.Y, n.Y);
                Assert.Equal(w.Z, n.Z);
                Assert.Equal(w.Alive, n.Alive);

                // 与 owner 样例一致（[0]=id [1..3]=xyz [5]=alive；[4]=yaw 被两消费方跳过）
                Assert.Equal((uint)v.Sample[0]!, w.EntityId);
                Assert.Equal((float)v.Sample[1]!, w.X);
                Assert.Equal((float)v.Sample[2]!, w.Y);
                Assert.Equal((float)v.Sample[3]!, w.Z);
                Assert.Equal((bool)v.Sample[5]!, w.Alive);
            }
        }

        /// <summary>npc 的 npc_row：weapon 消费方解析 owner 字节，解出样例字段。</summary>
        [Test]
        public static void NpcRow_WeaponConsumer_ParsesOwnerBytes()
        {
            var vectors = NpcTestVectors.All();
            Assert.True(vectors.Length >= 1, "npc 测试向量不足");
            foreach (var v in vectors)
            {
                Assert.Equal(NpcTestVectors.NpcRowContract, v.ContractId);
                var row = v.DecodeFields();
                Assert.True(WeaponNpcSnapshot.TryRead(row, out var w), $"weapon 解析 npc 行失败: {v}");
                Assert.Equal((uint)v.Sample[0]!, w.EntityId);
                Assert.Equal((float)v.Sample[1]!, w.X);
                Assert.Equal((float)v.Sample[2]!, w.Y);
                Assert.Equal((float)v.Sample[3]!, w.Z);
                Assert.Equal((bool)v.Sample[4]!, w.Alive); // npc 行 [4]=Alive
            }
        }

        /// <summary>漂移检测（负向）：owner 违反 append-only 改布局，消费方旧解析器应检出（解析失败/取错）。</summary>
        [Test]
        public static void Drift_OwnerBreaksLayout_ConsumerDetectsIt()
        {
            // 模拟 owner 把 bool 放到 [4]、float 放到 [5]（违反 append-only 的布局漂移）
            var drifted = TestVector.Of(PlayerTestVectors.PositionRowContract,
                new object?[] { 42u, 1f, 2f, 3f, true, 90f });
            var row = drifted.DecodeFields();
            // weapon 旧解析器读 [5] 当 bool——[5] 现在是 float → 类型不匹配 → TryRead 返回 false → 抓住漂移
            Assert.True(!WeaponPlayerSnapshot.TryRead(row, out _),
                "漂移未被检出：布局破坏后解析仍通过");
        }
    }
}
