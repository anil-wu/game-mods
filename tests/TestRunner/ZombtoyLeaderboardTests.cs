using System;
using System.Threading;
using Com.Game.Core;
using Com.Zombtoy.Leaderboard;
using Game.Messaging;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// com.zombtoy.leaderboard 无头测试（纯服务 Mod，契约 §0）：
    /// 能力（submit 立即 accepted + 异步落库 / get_top 缓存行集解析 / refresh 异步拉取并发布 TopChanged）、
    /// 传输抽象（MemoryTransport 注入：往返 / 降序 / 失败模拟）、契约测试向量自校验（§14.11.3）、
    /// 失败路径（submit 静默 / refresh 空表）、卸载清理（Rule 20）。
    /// 异步编排（§12.11）：refresh/submit 只入队立即返回，测试经 SpinUntil 等待后台任务完成（无真实后端）。
    /// </summary>
    public static class ZombtoyLeaderboardTests
    {
        private static readonly ModId TestSubscriber = new("com.test.runner");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public IModContext Ctx = null!;
            public MemoryTransport Transport = null!;

            // 消费方视角订阅 leaderboard:TopChanged（PayloadReader 字段解析，Rule 14；同 ui.ResultView 解析器形状）
            public int TopChangedEvents;
            public uint LastCount;
            public object?[] LastRows = Array.Empty<object?[]>();

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };

                // 传输注入（契约 §1）：先于 Mod 装配，无头测试不依赖真实后端
                f.Transport = new MemoryTransport();
                LeaderboardTransport.Set(f.Transport);

                // 依赖（契约 §4）：com.game.core 在前
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.core")), typeof(CoreMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.leaderboard")),
                    typeof(LeaderboardMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.leaderboard"));
                f.Ctx = f.Host.Manager.Get(LeaderboardMod.ModIdValue)!.Context;

                f.Host.Messages.Subscribe(TestSubscriber, LeaderboardMod.TopChangedEvent,
                    (in MessageEnvelope _, PayloadReader reader) =>
                    {
                        // 注意：handler 在线程池异步续体上执行，测试线程 SpinUntil 轮询 TopChangedEvents。
                        // 必须先写数据字段、最后写计数（flag-last，x86 存储序保证计数可见时数据已可见），
                        // 否则负载下测试线程可能看到 events=1 而 LastCount/LastRows 尚未写入（陈旧读竞态）。
                        if (reader.TryReadUInt32(1, out var c)) f.LastCount = c;
                        if (reader.TryReadView(2, out var view))
                        {
                            var decoded = DataCodec.Read(new PayloadReader(in view));
                            f.LastRows = decoded as object?[] ?? Array.Empty<object?[]>();
                        }
                        f.TopChangedEvents++;
                    });
                return f;
            }

            public object?[] Call(CapabilityId cap, params object?[] args)
                => ModCall.Invoke(Ctx.Mods, LeaderboardMod.ModIdValue, cap, args);

            public bool CallBool(CapabilityId cap, params object?[] args)
                => ModCall.InvokeBool(Ctx.Mods, LeaderboardMod.ModIdValue, cap, args);
        }

        /// <summary>等待后台异步任务完成（refresh/submit 只入队立即返回，契约 §0）。</summary>
        private static bool SpinUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return condition();
        }

        // ---- 能力：submit ----

        [Test]
        public static void Submit_Accepted_Immediate_AsyncStored()
        {
            var f = Fixture.Create();
            var accepted = f.CallBool(LeaderboardMod.SubmitCap, 1200, "player1");
            Assert.True(accepted, "submit 应立即返回 [true]");

            // 入队异步提交实际发生（后台任务落库到 MemoryTransport）
            Assert.True(SpinUntil(() => f.Transport.SubmitCount == 1), "异步提交未完成");
            var snap = f.Transport.Snapshot();
            Assert.Equal(1, snap.Length);
            Assert.Equal(1200, snap[0].Score);
            Assert.Equal("player1", snap[0].Name);
            Assert.Equal(0, f.TopChangedEvents); // submit 不发布消息（只有 refresh 发布，契约 §3）
        }

        [Test]
        public static void Submit_NonPositive_Rejected_NoAsyncWork()
        {
            var f = Fixture.Create();
            Assert.True(!f.CallBool(LeaderboardMod.SubmitCap, 0, "p"), "score=0 应拒绝");
            Assert.True(!f.CallBool(LeaderboardMod.SubmitCap, -5, "p"), "score<0 应拒绝");
            Thread.Sleep(50); // 给异步机会（不应发生）
            Assert.Equal(0, f.Transport.SubmitCount);
            Assert.Equal(0, f.TopChangedEvents);
        }

        [Test]
        public static void Submit_BadArgs_Rejected()
        {
            var f = Fixture.Create();
            var row = f.Call(LeaderboardMod.SubmitCap, "not-a-score"); // 类型不符
            Assert.True(row.Length > 0 && row[0] is bool b && !b, "非法入参应返回 [false]");
            Assert.Equal(0, f.Transport.SubmitCount);
        }

        [Test]
        public static void Submit_Failure_Silent_NoTopChanged()
        {
            var f = Fixture.Create();
            f.Transport.FailSubmit = true;
            Assert.True(f.CallBool(LeaderboardMod.SubmitCap, 100, "p"), "入队成功应立即 [true]");
            Assert.True(SpinUntil(() => f.Transport.SubmitCount == 1), "异步提交未完成");
            Thread.Sleep(50);
            Assert.Equal(0, f.TopChangedEvents); // 失败静默记日志，不影响流程、不发布（契约 §2）
        }

        // ---- 能力：get_top（最近一次缓存，契约 §0/§2） ----

        [Test]
        public static void GetTop_NoCache_ReturnsEmpty()
        {
            var f = Fixture.Create();
            var rows = f.Call(LeaderboardMod.GetTopCap, 10);
            Assert.Equal(0, rows.Length, "无缓存应返回 []");
        }

        [Test]
        public static void GetTop_BadArgs_ReturnsEmpty()
        {
            var f = Fixture.Create();
            var rows = f.Call(LeaderboardMod.GetTopCap, "x");
            Assert.Equal(0, rows.Length);
        }

        [Test]
        public static void GetTop_AfterRefresh_ParsedRowsWithRanks()
        {
            var f = Fixture.Create();
            f.Transport.Seed(5000, "alice");
            f.Transport.Seed(3000, "bob");
            f.Transport.Seed(1200, "player1");

            Assert.True(f.CallBool(LeaderboardMod.RefreshCap), "refresh 应立即 [true]");
            Assert.True(SpinUntil(() => f.TopChangedEvents >= 1), "refresh 未发布 TopChanged");

            var rows = f.Call(LeaderboardMod.GetTopCap, 10);
            Assert.Equal(3, rows.Length, "缓存应有 3 行");
            Assert.True(CapabilityShapes.TryReadRow(rows[0], out var r1, out var s1, out var n1));
            Assert.Equal(1u, r1); Assert.Equal(5000, s1); Assert.Equal("alice", n1);
            Assert.True(CapabilityShapes.TryReadRow(rows[1], out var r2, out var s2, out var n2));
            Assert.Equal(2u, r2); Assert.Equal(3000, s2); Assert.Equal("bob", n2);
            Assert.True(CapabilityShapes.TryReadRow(rows[2], out var r3, out var s3, out var n3));
            Assert.Equal(3u, r3); Assert.Equal(1200, s3); Assert.Equal("player1", n3);
        }

        [Test]
        public static void GetTop_ClampsN_ToRange()
        {
            var f = Fixture.Create();
            f.Transport.Seed(5000, "alice");
            f.Transport.Seed(3000, "bob");
            Assert.True(f.CallBool(LeaderboardMod.RefreshCap));
            Assert.True(SpinUntil(() => f.TopChangedEvents >= 1));

            // n=0 → 钳到 1（返回 1 行）
            Assert.Equal(1, f.Call(LeaderboardMod.GetTopCap, 0).Length);
            // 负 n → 钳到 1
            Assert.Equal(1, f.Call(LeaderboardMod.GetTopCap, -3).Length);
            // n 超大 → 钳到 50，但缓存只有 2 行
            Assert.Equal(2, f.Call(LeaderboardMod.GetTopCap, 999).Length);
        }

        // ---- 能力：refresh + 消息 TopChanged（契约 §3） ----

        [Test]
        public static void Refresh_PublishesTopChanged_BinaryFields()
        {
            var f = Fixture.Create();
            f.Transport.Seed(5000, "alice");
            f.Transport.Seed(3000, "bob");

            Assert.True(f.CallBool(LeaderboardMod.RefreshCap), "refresh 应立即 [true]");
            Assert.True(SpinUntil(() => f.TopChangedEvents >= 1), "refresh 未发布 TopChanged");

            // field1=count
            Assert.Equal(2u, f.LastCount);
            // field2=rows 嵌套数组（消费方自解析，Rule 19）
            Assert.Equal(2, f.LastRows.Length);
            Assert.True(CapabilityShapes.TryReadRow(f.LastRows[0], out var r0, out var s0, out var name0));
            Assert.Equal(1u, r0); Assert.Equal(5000, s0); Assert.Equal("alice", name0);
            Assert.True(CapabilityShapes.TryReadRow(f.LastRows[1], out var r1, out var s1, out var name1));
            Assert.Equal(2u, r1); Assert.Equal(3000, s1); Assert.Equal("bob", name1);

            // 缓存同步更新：get_top 立即读到同一行集
            var rows = f.Call(LeaderboardMod.GetTopCap, 10);
            Assert.Equal(2, rows.Length);
        }

        [Test]
        public static void Refresh_Failure_PublishesEmptyTable()
        {
            var f = Fixture.Create();
            f.Transport.Seed(5000, "alice"); // 后端有数据但拉取失败
            f.Transport.FailGetTop = true;

            Assert.True(f.CallBool(LeaderboardMod.RefreshCap));
            Assert.True(SpinUntil(() => f.TopChangedEvents >= 1), "refresh 失败也应发布（空表）");

            Assert.Equal(0u, f.LastCount, "失败应发布空表（UI 显示加载失败）");
            Assert.Equal(0, f.LastRows.Length);
            Assert.Equal(0, f.Call(LeaderboardMod.GetTopCap, 10).Length, "失败后缓存应为空");
        }

        // ---- 传输抽象：MemoryTransport 往返（契约 §1） ----

        [Test]
        public static void MemoryTransport_RoundTrip_SortedDesc()
        {
            var t = new MemoryTransport();
            Assert.True(t.Submit(100, "a"));
            Assert.True(t.Submit(300, "b"));
            Assert.True(t.Submit(200, "c"));

            var top1 = t.GetTop(1);
            Assert.Equal(1, top1.Length);
            Assert.Equal(300, top1[0].Score);
            Assert.Equal("b", top1[0].Name);

            var all = t.GetTop(10);
            Assert.Equal(3, all.Length);
            Assert.Equal(300, all[0].Score);
            Assert.Equal(200, all[1].Score);
            Assert.Equal(100, all[2].Score);

            Assert.Equal(0, t.GetTop(0).Length);
            Assert.Equal(0, t.GetTop(-1).Length);

            // 空传输 → 空
            var empty = new MemoryTransport();
            Assert.Equal(0, empty.GetTop(10).Length);
        }

        [Test]
        public static void MemoryTransport_FailModes_AndDefaults()
        {
            var t = new MemoryTransport { FailSubmit = true, FailGetTop = true };
            Assert.True(!t.Submit(10, "x"), "FailSubmit 应返回 false");
            Assert.Equal(0, t.GetTop(5).Length, "FailGetTop 应返回空表");
            Assert.Equal(0, t.EntryCount, "失败不应落库");

            // 缺省名
            var t2 = new MemoryTransport();
            t2.Submit(50, "");
            var snap = t2.Snapshot();
            Assert.Equal(LeaderboardConfig.PlayerName, snap[0].Name);
        }

        // ---- 契约测试向量（§14.11.3）：owner 规范字节 ↔ 样例一致 ----

        [Test]
        public static void TestVectors_DataCodecShapes_RoundTrip()
        {
            foreach (var v in LeaderboardTestVectors.All())
            {
                if (v.ContractId is LeaderboardTestVectors.TopMsgContract)
                    continue; // 消息向量不是 DataCodec 形状，走下一测试

                var fields = v.DecodeFields(); // owner 规范字节 → 字段元组
                Assert.Equal(v.Sample.Length, fields.Length, $"{v.ContractId} 字段数漂移");
                for (var i = 0; i < v.Sample.Length; i++)
                    Assert.Equal(v.Sample[i], fields[i], $"{v.ContractId}[{i}] 与样例不一致");
            }
        }

        [Test]
        public static void TestVectors_TopChangedMessage_ConsumerParser()
        {
            // 消费方（ui.ResultView / LeaderboardRows）各自重复定义的解析器（Rule 19）
            // 解析 owner 规范字节，与样例一致 → "可重复定义未漂移"（§14.11.3）
            foreach (var v in LeaderboardTestVectors.All())
            {
                if (v.ContractId != LeaderboardTestVectors.TopMsgContract) continue;
                Assert.True(LeaderboardRows.TryRead(v.Bytes, out var count, out var rows), $"解析失败: {v}");

                var sample = v.Sample;
                Assert.Equal((uint)sample[0]!, count, $"field1 count 漂移: {v}");
                var sampleRows = (object?[])sample[1]!;
                Assert.Equal(sampleRows.Length, rows.Length, $"行数漂移: {v}");
                for (var i = 0; i < sampleRows.Length; i++)
                {
                    Assert.True(CapabilityShapes.TryReadRow(rows[i], out var r, out var s, out var n), $"行 {i} 解析失败: {v}");
                    var sr = (object?[])sampleRows[i]!;
                    Assert.Equal((uint)sr[0]!, r, $"[{i}].rank 漂移");
                    Assert.Equal((int)sr[1]!, s, $"[{i}].score 漂移");
                    Assert.Equal((string)sr[2]!, n, $"[{i}].name 漂移");
                }
            }
        }

        [Test]
        public static void TestVectors_Drift_NegativeCase()
        {
            // 漂移负向用例（summary §6）：owner 改了布局（行缺 rank 字段）应被消费方解析器检出
            var wrongRows = DataCodec.Write(new object?[] { new object?[] { 5000, "alice" } }); // [score, name] 缺 rank
            var w = new PayloadWriter();
            w.WriteUInt32(1, 1u);
            w.WriteBytes(2, wrongRows.ToArray());
            Assert.True(LeaderboardRows.TryRead(w.ToBuffer().ToArray(), out var count, out var rows));
            Assert.Equal(1u, count);
            Assert.Equal(1, rows.Length);
            Assert.True(!CapabilityShapes.TryReadRow(rows[0], out _, out _, out _),
                "漂移载荷（缺 rank）未被检出");
        }

        // ---- 卸载清理（Rule 20 / §9.2 / §13.5） ----

        [Test]
        public static void Unload_ResetsStaticState_NoSubscriptions()
        {
            var f = Fixture.Create();
            f.Transport.Seed(100, "x");
            Assert.True(f.CallBool(LeaderboardMod.RefreshCap));
            Assert.True(SpinUntil(() => f.TopChangedEvents >= 1));

            f.Host.Manager.Unload(LeaderboardMod.ModIdValue);

            Assert.True(!f.Host.Manager.IsLoaded(LeaderboardMod.ModIdValue));
            Assert.Equal(0, f.Host.Systems.CountOf(LeaderboardMod.ModIdValue)); // 纯服务 Mod 无系统
            Assert.Equal(0, f.Host.Messages.SubscriptionCount(LeaderboardMod.ModIdValue)); // 无订阅残留（只发布）
            Assert.True(LeaderboardMod.Context is null, "静态桥未清空");
            Assert.Equal(0, f.Host.World.EntityCount); // 无实体创建
        }

        /// <summary>声明依赖 leaderboard 的探针 Mod（验证卸载后能力调用 → NoModException，§12.11 规则 2）。</summary>
        public sealed class LeaderboardProbeMod : IMod
        {
            public static readonly ModId Id = new("com.test.leaderboardprobe");
            public static Exception? CallError;

            public void Register(IModContext context) { }
            public void Unregister(IModContext context) { }

            public static void TryCall(IModContext ctx)
            {
                try { ModCall.Invoke(ctx.Mods, LeaderboardMod.ModIdValue, LeaderboardMod.SubmitCap, 100, "p"); }
                catch (Exception e) { CallError = e; }
            }
        }

        [Test]
        public static void Unload_CapabilityCall_ThrowsNoMod()
        {
            var f = Fixture.Create();
            f.Host.Manager.Unload(LeaderboardMod.ModIdValue);

            // 卸载后加载探针（声明对 leaderboard 的依赖）→ 能力调用得到 NoModException
            f.Host.Load(new ModManifest
            {
                ModId = LeaderboardProbeMod.Id,
                Version = new ModVersion(1, 0, 0),
                Entry = typeof(LeaderboardProbeMod).FullName!,
                SharedModule = "test.dll",
                Dependencies = { new ModDependency { Id = LeaderboardMod.ModIdValue, Version = VersionRange.Any, Optional = false } },
            }, typeof(LeaderboardProbeMod).Assembly);
            var probeCtx = f.Host.Manager.Get(LeaderboardProbeMod.Id)!.Context;

            LeaderboardProbeMod.CallError = null;
            LeaderboardProbeMod.TryCall(probeCtx);
            Assert.True(LeaderboardProbeMod.CallError is NoModException,
                $"期望 NoModException，实际 {LeaderboardProbeMod.CallError}");
        }

        // ---- 消费方风格解析器（Rule 19：结构相同、定义独立，模拟 ui.ResultView） ----

        /// <summary>消费方 ui.LeaderboardRows：leaderboard:TopChanged 解析器（1=count(u32) 2=rows(bytes 嵌套)）。</summary>
        public sealed class LeaderboardRows
        {
            public static bool TryRead(byte[] bytes, out uint count, out object?[] rows)
            {
                count = 0;
                rows = Array.Empty<object?[]>();
                var reader = new PayloadReader(bytes);
                if (!reader.TryReadUInt32(1, out count)) return false;
                if (!reader.TryReadView(2, out var view)) return false;
                rows = DataCodec.Read(new PayloadReader(in view));
                return true;
            }
        }
    }
}
