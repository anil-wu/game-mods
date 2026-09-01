using Com.Game.Core;
using Com.Zombtoy.Player;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// com.zombtoy.player 无头测试（单机 Host 路径，契约 §2：SystemSide.Server + UpdateAll）：
    /// 能力（damage/heal/reset/get_*）、系统（输入合成速度/位移积分/体力冲刺消耗恢复）、
    /// 消息（HealthChanged/StaminaChanged/Died 二进制字段）、契约测试向量自校验（§14.11.3）、卸载清理（Rule 20）。
    /// </summary>
    public static class ZombtoyPlayerTests
    {
        private static readonly ModId PlayerId = new("com.zombtoy.player");
        private static readonly ModId TestSubscriber = new("com.test.runner");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public World World => Host.World;
            public IModContext PlayerCtx = null!;

            // 消费方视角订阅（PayloadReader 字段解析，Rule 14）
            public int HealthChangedEvents;
            public int LastHealth;
            public int LastMax;
            public int StaminaChangedEvents;
            public float LastStamina;
            public float LastStaminaMax;
            public int DiedEvents;
            public uint LastDiedSource;

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };

                // 依赖（契约 §5）：com.game.core 在前
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.core")), typeof(CoreMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.player")),
                    typeof(PlayerMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.player"));
                f.PlayerCtx = f.Host.Manager.Get(PlayerId)!.Context;

                f.Host.Messages.Subscribe(TestSubscriber, PlayerMod.HealthChangedEvent,
                    (in Game.Messaging.MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.HealthChangedEvents++;
                        if (reader.TryReadInt32(1, out var c)) f.LastHealth = c;
                        if (reader.TryReadInt32(2, out var m)) f.LastMax = m;
                    });
                f.Host.Messages.Subscribe(TestSubscriber, PlayerMod.StaminaChangedEvent,
                    (in Game.Messaging.MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.StaminaChangedEvents++;
                        if (reader.TryReadFloat(1, out var c)) f.LastStamina = c;
                        if (reader.TryReadFloat(2, out var m)) f.LastStaminaMax = m;
                    });
                f.Host.Messages.Subscribe(TestSubscriber, PlayerMod.DiedEvent,
                    (in Game.Messaging.MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.DiedEvents++;
                        if (reader.TryReadUInt32(1, out var s)) f.LastDiedSource = s;
                    });
                return f;
            }

            public void Tick(int frames, float dt = 0.05f)
            {
                for (var i = 0; i < frames; i++) Host.Tick(dt);
            }

            public Entity Player => new(PlayerMod.LocalPlayerEntityId);

            /// <summary>视图同路径：直接写 PlayerInput（无头测试替代 MonoBehaviour 输入）。</summary>
            public void SetInput(float moveX, float moveZ, bool sprint)
            {
                var e = Player;
                var input = World.TryGet<PlayerInput>(e, out var old) ? old : default;
                input.MoveX = moveX;
                input.MoveZ = moveZ;
                input.Sprint = sprint;
                World.Add(e, input);
            }

            public object?[] Call(CapabilityId cap, params object?[] args)
                => ModCall.Invoke(PlayerCtx.Mods, PlayerId, cap, args);

            public bool CallBool(CapabilityId cap, params object?[] args)
                => ModCall.InvokeBool(PlayerCtx.Mods, PlayerId, cap, args);
        }

        // ---- 生成与默认值 ----

        [Test]
        public static void Spawn_Player_WithDefaults()
        {
            var f = Fixture.Create();
            var e = f.Player;
            Assert.True(e.Id != 0, "玩家实体未生成");

            var pos = f.World.Get<Position3>(e);
            Assert.Equal(PlayerConfig.SpawnX, pos.X);
            Assert.Equal(PlayerConfig.SpawnY, pos.Y);
            Assert.Equal(PlayerConfig.SpawnZ, pos.Z);

            var hp = f.World.Get<PlayerHealth>(e);
            Assert.Equal(PlayerConfig.MaxHealth, hp.Current);
            Assert.Equal(PlayerConfig.MaxHealth, hp.Max);

            var st = f.World.Get<Stamina>(e);
            Assert.Equal(PlayerConfig.MaxStamina, st.Current);
            Assert.Equal(PlayerConfig.MaxStamina, st.Max);

            Assert.True(!f.World.Get<PlayerFlags>(e).IsDead);
        }

        // ---- 能力：damage / heal / reset / get_* ----

        [Test]
        public static void Damage_ReducesHealth_PublishesHealthChanged()
        {
            var f = Fixture.Create();
            var killed = f.CallBool(PlayerMod.DamageCap, 25, 7u);
            Assert.True(!killed);

            var hp = f.World.Get<PlayerHealth>(f.Player);
            Assert.Equal(75, hp.Current);
            Assert.Equal(1, f.HealthChangedEvents);
            Assert.Equal(75, f.LastHealth);
            Assert.Equal(100, f.LastMax);
            Assert.Equal(0, f.DiedEvents);
        }

        [Test]
        public static void Damage_Lethal_SetsDead_PublishesDied()
        {
            var f = Fixture.Create();
            var killed = f.CallBool(PlayerMod.DamageCap, 150, 9u);
            Assert.True(killed);

            var hp = f.World.Get<PlayerHealth>(f.Player);
            Assert.Equal(0, hp.Current);
            Assert.True(f.World.Get<PlayerFlags>(f.Player).IsDead);
            Assert.Equal(1, f.HealthChangedEvents);
            Assert.Equal(0, f.LastHealth);
            Assert.Equal(1, f.DiedEvents);
            Assert.Equal(9u, f.LastDiedSource);

            // 死亡后继续扣血：拒绝（[false]），不重复发 Died
            var again = f.CallBool(PlayerMod.DamageCap, 10, 0u);
            Assert.True(!again);
            Assert.Equal(1, f.DiedEvents);
            Assert.Equal(0, hp.Current);
        }

        [Test]
        public static void Damage_NonPositive_NoOp()
        {
            var f = Fixture.Create();
            var killed = f.CallBool(PlayerMod.DamageCap, 0, 0u);
            Assert.True(!killed);
            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current);
            Assert.Equal(0, f.HealthChangedEvents);
            Assert.Equal(0, f.DiedEvents);
        }

        [Test]
        public static void Heal_RestoresUpToMax()
        {
            var f = Fixture.Create();
            f.CallBool(PlayerMod.DamageCap, 30, 0u);
            var eventsBefore = f.HealthChangedEvents;

            var applied = f.CallBool(PlayerMod.HealCap, 20);
            Assert.True(applied);
            Assert.Equal(90, f.World.Get<PlayerHealth>(f.Player).Current);
            Assert.True(f.HealthChangedEvents > eventsBefore);

            // 治疗溢出：封顶 Max，不越界
            applied = f.CallBool(PlayerMod.HealCap, 1000);
            Assert.True(applied);
            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current);
            Assert.Equal(100, f.LastMax);
        }

        [Test]
        public static void Heal_AtFullHealth_ReturnsTrue_NoChange()
        {
            var f = Fixture.Create();
            var applied = f.CallBool(PlayerMod.HealCap, 10);
            Assert.True(applied); // 满血：血瓶照常消耗，治疗量溢出丢弃（契约 §3）
            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current);
            Assert.Equal(0, f.HealthChangedEvents); // 值未变化 → 不发布
        }

        [Test]
        public static void Heal_OnDead_Rejected()
        {
            var f = Fixture.Create();
            f.CallBool(PlayerMod.DamageCap, 999, 0u);
            var applied = f.CallBool(PlayerMod.HealCap, 50);
            Assert.True(!applied);
            Assert.Equal(0, f.World.Get<PlayerHealth>(f.Player).Current);
        }

        // ---- 系统：移动 / 冲刺 / 体力 ----

        [Test]
        public static void Movement_InputApply_Integrates()
        {
            var f = Fixture.Create();
            f.Tick(5);

            // 前进（W → +Z）：速度 6，0.5s 位移 3
            f.SetInput(0f, 1f, sprint: false);
            f.Tick(10);
            var pos = f.World.Get<Position3>(f.Player);
            Assert.True(System.Math.Abs(pos.Z - (PlayerConfig.SpawnZ + PlayerConfig.MoveSpeed * 0.5f)) < 0.01f,
                $"未按速度位移: Z={pos.Z}");

            // 斜向（A+D 同按）→ 归一化，合位移量 = 单轴 1
            f.SetInput(1f, 1f, sprint: false);
            var before = f.World.Get<Position3>(f.Player);
            f.Tick(10);
            var after = f.World.Get<Position3>(f.Player);
            var dx = after.X - before.X;
            var dz = after.Z - before.Z;
            var moved = System.Math.Sqrt(dx * dx + dz * dz);
            Assert.True(System.Math.Abs(moved - PlayerConfig.MoveSpeed * 0.5f) < 0.01f,
                $"斜向未归一化: moved={moved}");

            // 停止输入 → 停住
            f.SetInput(0f, 0f, sprint: false);
            f.Tick(1);
            var p1 = f.World.Get<Position3>(f.Player);
            f.Tick(10);
            var p2 = f.World.Get<Position3>(f.Player);
            Assert.Equal(p1.X, p2.X);
            Assert.Equal(p1.Z, p2.Z);
        }

        [Test]
        public static void Sprint_DrainsStamina_ForcesCancel_Regens()
        {
            var f = Fixture.Create();
            f.SetInput(0f, 0f, sprint: true);
            f.Tick(40); // 2s：体力 1 - 0.5*2 = 0
            var st = f.World.Get<Stamina>(f.Player);
            Assert.True(st.Current <= 0.01f, $"冲刺未耗尽体力: {st.Current}");
            Assert.True(!f.World.Get<PlayerInput>(f.Player).Sprint, "体力耗尽未强制取消冲刺");
            Assert.True(f.StaminaChangedEvents > 0, "冲刺未发布 StaminaChanged");

            // 非冲刺恢复：2s → +0.3（封顶 Max）
            f.Tick(40);
            var st2 = f.World.Get<Stamina>(f.Player);
            Assert.True(st2.Current >= PlayerConfig.StaminaRegenPerSec * 2f - 0.01f, $"未恢复: {st2.Current}");
            Assert.True(st2.Current <= PlayerConfig.MaxStamina + 0.01f, $"恢复超 Max: {st2.Current}");
        }

        [Test]
        public static void Sprint_Multiplier_FasterMovement()
        {
            var f = Fixture.Create();
            // 冲刺：速度 = 6 * 1.2 = 7.2
            f.SetInput(0f, 1f, sprint: true);
            f.Tick(10);
            var sprintZ = f.World.Get<Position3>(f.Player).Z;
            var expected = PlayerConfig.SpawnZ + PlayerConfig.MoveSpeed * PlayerConfig.SprintMultiplier * 0.5f;
            Assert.True(System.Math.Abs(sprintZ - expected) < 0.02f,
                $"冲刺速度不对: Z={sprintZ}（期望 {expected}）");

            // 体力耗尽（2s）后冲刺失效 → 速度回落 6
            f.SetInput(0f, 1f, sprint: true);
            f.Tick(200); // 10s：耗尽并恢复
            var v = f.World.Get<Velocity3>(f.Player);
            Assert.True(System.Math.Abs(v.Z - PlayerConfig.MoveSpeed) < 0.01f,
                $"体力耗尽后仍加速: vZ={v.Z}");
        }

        [Test]
        public static void DeadPlayer_DoesNotMove()
        {
            var f = Fixture.Create();
            f.SetInput(0f, 1f, sprint: false);
            f.Tick(5);
            var posBefore = f.World.Get<Position3>(f.Player);

            f.CallBool(PlayerMod.DamageCap, 999, 0u);
            f.Tick(10);

            var posAfter = f.World.Get<Position3>(f.Player);
            Assert.Equal(posBefore.X, posAfter.X);
            Assert.Equal(posBefore.Z, posAfter.Z);
        }

        // ---- 能力：reset / get_* ----

        [Test]
        public static void Reset_RestoresAll_PublishesBothMessages()
        {
            var f = Fixture.Create();
            // 造成状态污染：扣血、冲刺耗尽体力、移动、转身
            f.CallBool(PlayerMod.DamageCap, 70, 0u);
            f.SetInput(0f, 1f, sprint: true);
            f.Tick(40);
            f.World.Add(f.Player, new Facing { Yaw = 90f });

            var eventsBefore = f.HealthChangedEvents + f.StaminaChangedEvents;
            var ok = f.CallBool(PlayerMod.ResetCap, 5f, 2f, -3f);
            Assert.True(ok);

            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current);
            Assert.Equal(1f, f.World.Get<Stamina>(f.Player).Current);
            Assert.True(!f.World.Get<PlayerFlags>(f.Player).IsDead);

            var pos = f.World.Get<Position3>(f.Player);
            Assert.Equal(5f, pos.X);
            Assert.Equal(2f, pos.Y);
            Assert.Equal(-3f, pos.Z);
            Assert.Equal(0f, f.World.Get<Facing>(f.Player).Yaw);
            var vel = f.World.Get<Velocity3>(f.Player);
            Assert.Equal(0f, vel.X);
            Assert.Equal(0f, vel.Z);

            Assert.True(f.HealthChangedEvents + f.StaminaChangedEvents > eventsBefore,
                "reset 未发布 Health/Stamina 消息");
        }

        [Test]
        public static void Reset_RevivesDeadPlayer()
        {
            var f = Fixture.Create();
            f.CallBool(PlayerMod.DamageCap, 999, 3u);
            Assert.True(f.World.Get<PlayerFlags>(f.Player).IsDead);
            Assert.Equal(1, f.DiedEvents);

            var ok = f.CallBool(PlayerMod.ResetCap, 0f, 1f, 0f);
            Assert.True(ok);
            Assert.True(!f.World.Get<PlayerFlags>(f.Player).IsDead);
            Assert.Equal(100, f.World.Get<PlayerHealth>(f.Player).Current);
            // 复活后可再次受击
            var killed = f.CallBool(PlayerMod.DamageCap, 50, 0u);
            Assert.True(!killed);
            Assert.Equal(50, f.World.Get<PlayerHealth>(f.Player).Current);
        }

        [Test]
        public static void GetEntity_ReturnsLocalPlayer()
        {
            var f = Fixture.Create();
            var row = f.Call(PlayerMod.GetEntityCap);
            Assert.True(row.Length > 0 && row[0] is uint id && id != 0 && id == PlayerMod.LocalPlayerEntityId,
                $"get_entity 返回异常: {row.Length}");
        }

        [Test]
        public static void GetState_And_GetPosition_Rows()
        {
            var f = Fixture.Create();
            var state = f.Call(PlayerMod.GetStateCap);
            Assert.True(CapabilityShapes.TryReadStateRow(state,
                out var hp, out var max, out var st, out var maxSt, out var alive));
            Assert.Equal(100, hp);
            Assert.Equal(100, max);
            Assert.Equal(1f, st);
            Assert.Equal(1f, maxSt);
            Assert.True(alive);

            var pos = f.Call(PlayerMod.GetPositionCap);
            Assert.True(CapabilityShapes.TryReadPositionRow(pos, out var x, out var y, out var z, out var alive2));
            Assert.Equal(PlayerConfig.SpawnX, x);
            Assert.Equal(PlayerConfig.SpawnY, y);
            Assert.Equal(PlayerConfig.SpawnZ, z);
            Assert.True(alive2);

            // 受伤后：get_state 反映当前值；致死 → alive=false
            f.CallBool(PlayerMod.DamageCap, 25, 0u);
            state = f.Call(PlayerMod.GetStateCap);
            Assert.True(CapabilityShapes.TryReadStateRow(state, out hp, out max, out _, out _, out alive));
            Assert.Equal(75, hp);

            f.CallBool(PlayerMod.DamageCap, 999, 0u);
            pos = f.Call(PlayerMod.GetPositionCap);
            Assert.True(CapabilityShapes.TryReadPositionRow(pos, out _, out _, out _, out alive2));
            Assert.True(!alive2);
        }

        // ---- 消息 ----

        [Test]
        public static void StaminaChanged_Message_BinaryFields()
        {
            var f = Fixture.Create();
            f.SetInput(0f, 0f, sprint: true);
            f.Tick(10); // 0.5s → 体力 0.75
            Assert.True(f.StaminaChangedEvents >= 1, "未发布 StaminaChanged");
            Assert.True(f.LastStamina > 0.7f && f.LastStamina < 0.8f, $"字段1 解析异常: {f.LastStamina}");
            Assert.Equal(1f, f.LastStaminaMax);
        }

        // ---- 契约测试向量（§14.11.3）：owner 规范字节 ↔ 样例一致 ----

        [Test]
        public static void TestVectors_DataCodecShapes_RoundTrip()
        {
            foreach (var v in PlayerTestVectors.All())
            {
                if (v.ContractId is PlayerTestVectors.HealthMsgContract
                    or PlayerTestVectors.StaminaMsgContract
                    or PlayerTestVectors.DiedMsgContract)
                    continue; // 消息向量不是 DataCodec 形状，走下一测试

                var fields = v.DecodeFields(); // owner 规范字节 → 字段元组
                Assert.Equal(v.Sample.Length, fields.Length, $"{v.ContractId} 字段数漂移");
                for (var i = 0; i < v.Sample.Length; i++)
                    Assert.Equal(v.Sample[i], fields[i], $"{v.ContractId}[{i}] 与样例不一致");
            }
        }

        [Test]
        public static void TestVectors_Messages_ConsumerStyleParsers()
        {
            // 消费方（ui.HudHealth / ui.HudStamina / score.ScoreSystem）各自重复定义的解析器（Rule 19）
            // 解析 owner 规范字节，与样例一致 → "可重复定义未漂移"（§14.11.3）
            foreach (var v in PlayerTestVectors.All())
            {
                switch (v.ContractId)
                {
                    case PlayerTestVectors.HealthMsgContract:
                        Assert.True(HudHealth.TryRead(v.Bytes, out var c, out var m), $"解析失败: {v}");
                        Assert.Equal((int)v.Sample[0]!, c);
                        Assert.Equal((int)v.Sample[1]!, m);
                        break;
                    case PlayerTestVectors.StaminaMsgContract:
                        Assert.True(HudStamina.TryRead(v.Bytes, out var sc, out var sm), $"解析失败: {v}");
                        Assert.Equal((float)v.Sample[0]!, sc);
                        Assert.Equal((float)v.Sample[1]!, sm);
                        break;
                    case PlayerTestVectors.DiedMsgContract:
                        Assert.True(ScoreSummary.TryRead(v.Bytes, out var src), $"解析失败: {v}");
                        Assert.Equal((uint)v.Sample[0]!, src);
                        break;
                }
            }
        }

        // ---- 卸载清理（Rule 20 / §9.2 / §13.5） ----

        [Test]
        public static void Unload_CleansSystemsEntitiesCapabilities()
        {
            var f = Fixture.Create();
            f.Tick(5);
            Assert.True(f.World.EntityCount > 0);
            Assert.Equal(3, f.Host.Systems.CountOf(PlayerId)); // InputApply/Movement/Stamina

            f.Host.Manager.Unload(PlayerId);

            Assert.True(!f.Host.Manager.IsLoaded(PlayerId));
            Assert.Equal(0, f.Host.Systems.CountOf(PlayerId)); // 系统移除（§9.2）
            Assert.Equal(0, f.World.EntityCount);              // 玩家实体随卸载强制销毁（§9.2）
            Assert.Equal(0, f.Host.Messages.SubscriptionCount(PlayerId)); // 本 Mod 无订阅残留（只发布）
        }

        /// <summary>声明依赖 player 的探针 Mod（验证卸载后能力调用 → NoModException，§12.11 规则 2）。</summary>
        public sealed class PlayerProbeMod : IMod
        {
            public static readonly ModId Id = new("com.test.playerprobe");
            public static System.Exception? CallError;

            public void Register(IModContext context) { }
            public void Unregister(IModContext context) { }

            public static void TryCall(IModContext ctx)
            {
                try { ModCall.Invoke(ctx.Mods, PlayerId, PlayerMod.GetStateCap); }
                catch (System.Exception e) { CallError = e; }
            }
        }

        [Test]
        public static void Unload_CapabilityCall_ThrowsNoMod()
        {
            var f = Fixture.Create();
            f.Host.Manager.Unload(PlayerId);

            // 卸载后加载探针（声明对 player 的依赖）→ 能力调用得到 NoModException
            f.Host.Load(new ModManifest
            {
                ModId = PlayerProbeMod.Id,
                Version = new ModVersion(1, 0, 0),
                Entry = typeof(PlayerProbeMod).FullName!,
                SharedModule = "test.dll",
                Dependencies = { new ModDependency { Id = PlayerId, Version = VersionRange.Any, Optional = false } },
            }, typeof(PlayerProbeMod).Assembly);
            var probeCtx = f.Host.Manager.Get(PlayerProbeMod.Id)!.Context;

            PlayerProbeMod.CallError = null;
            PlayerProbeMod.TryCall(probeCtx);
            Assert.True(PlayerProbeMod.CallError is NoModException,
                $"期望 NoModException，实际 {PlayerProbeMod.CallError}");
        }

        // ---- 消费方风格解析器（Rule 19：结构相同、定义独立，模拟 ui/score 各自重复定义） ----

        /// <summary>消费方 ui.HudHealth：player:HealthChanged 解析器（[1]=current [2]=max）。</summary>
        public sealed class HudHealth
        {
            public static bool TryRead(byte[] bytes, out int current, out int max)
            {
                current = 0; max = 0;
                var reader = new PayloadReader(bytes);
                if (!reader.TryReadInt32(1, out current)) return false;
                if (!reader.TryReadInt32(2, out max)) return false;
                return true;
            }
        }

        /// <summary>消费方 ui.HudStamina：player:StaminaChanged 解析器（[1]=current [2]=max）。</summary>
        public sealed class HudStamina
        {
            public static bool TryRead(byte[] bytes, out float current, out float max)
            {
                current = 0; max = 0;
                var reader = new PayloadReader(bytes);
                if (!reader.TryReadFloat(1, out current)) return false;
                if (!reader.TryReadFloat(2, out max)) return false;
                return true;
            }
        }

        /// <summary>消费方 score.ScoreSystem：player:Died 解析器（[1]=source，结算触发）。</summary>
        public sealed class ScoreSummary
        {
            public static bool TryRead(byte[] bytes, out uint source)
            {
                source = 0;
                var reader = new PayloadReader(bytes);
                return reader.TryReadUInt32(1, out source);
            }
        }
    }
}
