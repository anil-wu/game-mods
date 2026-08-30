using System;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;

namespace TestRunner
{
    /// <summary>契约层测试：ID / 稳定哈希 / 线格式 / Manifest v2 / 版本 / 拓扑。</summary>
    public static class ContractTests
    {
        [Test]
        public static void ModId_Parse()
        {
            Assert.True(ModId.TryParse("com.game.weapon", out var id));
            Assert.Equal("com.game.weapon", id.Value);
            Assert.True(!ModId.TryParse("Com.Game", out _));   // 大写非法
            Assert.True(!ModId.TryParse("single", out _));     // 单段非法
        }

        [Test]
        public static void NamespacedIds_RoundTrip()
        {
            var asset = AssetId.Parse("com.game.weapon:sword");
            Assert.Equal("com.game.weapon", asset.Mod.Value);
            Assert.Equal("sword", asset.Path);
            Assert.Equal("com.game.weapon:sword", asset.ToString());

            var msg = MessageId.Parse("com.sample.pushbox:game_won");
            Assert.Equal("game_won", msg.Path);

            Assert.True(NamespacedId.TryParse("a.b:x/y/z", out var n));
            Assert.Equal("x/y/z", n.LocalId);
        }

        [Test]
        public static void XxHash32_KnownVectors()
        {
            // 参考向量（xxHash32, seed=0）
            Assert.Equal(0x02CC5D05u, XxHash32.Compute(""));
            Assert.Equal(0x32D153FFu, XxHash32.Compute("abc"));
            Assert.Equal(XxHash32.Compute("com.game.weapon:fire"), XxHash32.Compute("com.game.weapon:fire"));
            Assert.True(XxHash32.Compute("a:b") != XxHash32.Compute("a:c"));
        }

        [Test]
        public static void ProtocolId_StableHash()
        {
            // Rule 16：两端各自对同一字符串计算，结果天然一致
            var a = ProtocolId.Of("com.game.weapon:fire");
            var b = ProtocolId.Of(new ModId("com.game.weapon"), "fire");
            Assert.Equal(a, b);
            Assert.True(a.Value != 0u);
        }

        [Test]
        public static void Wire_RoundTrip_AllTypes()
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, -12345);          // zigzag 负数
            w.WriteUInt32(2, 4000000000u);
            w.WriteInt64(3, -9_000_000_000L);
            w.WriteUInt64(4, 18_000_000_000UL);
            w.WriteFloat(5, 3.5f);
            w.WriteBool(6, true);
            w.WriteByte(7, 200);
            w.WriteString(8, "你好, world");
            w.WriteBytes(9, new byte[] { 1, 2, 3, 255 });

            var r = new PayloadReader(w.ToBuffer());
            Assert.True(r.TryReadInt32(1, out var i32) && i32 == -12345);
            Assert.True(r.TryReadUInt32(2, out var u32) && u32 == 4000000000u);
            Assert.True(r.TryReadInt64(3, out var i64) && i64 == -9_000_000_000L);
            Assert.True(r.TryReadUInt64(4, out var u64) && u64 == 18_000_000_000UL);
            Assert.True(r.TryReadFloat(5, out var f) && Math.Abs(f - 3.5f) < 1e-6);
            Assert.True(r.TryReadBool(6, out var b) && b);
            Assert.True(r.TryReadByte(7, out var by) && by == 200);
            Assert.True(r.TryReadString(8, out var s) && s == "你好, world");
            Assert.True(r.TryReadBytes(9, out var bytes) && bytes.Length == 4 && bytes[3] == 255);
        }

        [Test]
        public static void Wire_AppendOnly_UnknownFieldsSkipped()
        {
            // v2 载荷（多一个 field 3）→ v1 读取器只读 field 1/2 照常工作（§14.11.1）
            var w = new PayloadWriter();
            w.WriteInt32(1, 42);
            w.WriteString(3, "new field");
            w.WriteBool(2, true);

            var r = new PayloadReader(w.ToBuffer());
            Assert.True(r.TryReadInt32(1, out var v1) && v1 == 42);
            Assert.True(r.TryReadBool(2, out var v2) && v2);
            Assert.True(!r.TryReadInt32(99, out _)); // 缺失字段 → false，不报错
        }

        [Test]
        public static void Wire_ConsumerReadsSubset()
        {
            // 消费方只解析自己关心的字段（§14.12.2 懒解码）
            var w = new PayloadWriter();
            w.WriteInt32(1, 7);
            w.WriteString(2, new string('x', 500));
            w.WriteInt32(3, 8);

            var r = new PayloadReader(w.ToBuffer());
            Assert.True(r.TryReadInt32(3, out var v) && v == 8); // 跳过大字段直接读 field 3
        }

        [Test]
        public static void Wire_Malformed_Throws()
        {
            Assert.Throws<PayloadFormatException>(() =>
                new PayloadReader(new byte[] { 1, 0 }).TryReadInt32(1, out _)); // 截断
            var w = new PayloadWriter();
            w.WriteInt32(1, 5);
            Assert.Throws<PayloadFormatException>(() =>
                new PayloadReader(w.ToBuffer()).TryReadString(1, out _)); // 类型不匹配
        }

        [Test]
        public static void ManifestV2_RoundTrip()
        {
            var json = @"{
  ""id"": ""com.game.weapon"",
  ""version"": ""1.2.0"",
  ""entry"": ""Com.Game.Weapon.WeaponMod"",
  ""modules"": { ""shared"": ""Weapon.Shared.dll"", ""client"": ""Weapon.Client.dll"", ""server"": ""Weapon.Server.dll"" },
  ""dependencies"": [
    { ""id"": ""com.game.common"", ""version"": "">=1.0.0"" },
    { ""id"": ""com.game.ui"", ""version"": ""^1.0.0"", ""scope"": ""client"", ""optional"": true, ""features"": [""hud""] }
  ],
  ""network"": { ""protocols"": [
    { ""id"": ""fire"", ""version"": 1, ""direction"": ""ClientToServer"" },
    { ""id"": ""state"", ""version"": 2, ""direction"": ""ServerToClient"", ""delivery"": ""Unreliable"", ""frequency"": ""High"" }
  ] }
}";
            var m = ModManifest.Parse(json);
            Assert.Equal("com.game.weapon", m.ModId.Value);
            Assert.Equal("1.2.0", m.Version.ToString());
            Assert.Equal("Com.Game.Weapon.WeaponMod", m.Entry);
            Assert.Equal("Weapon.Shared.dll", m.SharedModule);
            Assert.Equal("Weapon.Client.dll", m.ClientModule);
            Assert.Equal("Weapon.Server.dll", m.ServerModule);
            Assert.Equal(2, m.Dependencies.Count);
            Assert.Equal(ModScope.Client, m.Dependencies[1].Scope);
            Assert.True(m.Dependencies[1].Optional);
            Assert.Equal("hud", m.Dependencies[1].Features[0]);
            Assert.Equal(2, m.Protocols.Count);
            Assert.Equal(NetworkDirection.ServerToClient, m.Protocols[1].Direction);
            Assert.Equal(Delivery.Unreliable, m.Protocols[1].Delivery);
            Assert.Equal(Frequency.High, m.Protocols[1].Frequency);
            Assert.Equal(ProtocolId.Of(m.ModId, "fire"), m.Protocols[0].GlobalId(m.ModId));

            // 序列化 → 再解析保持一致
            var m2 = ModManifest.Parse(m.ToJson());
            Assert.Equal(m.ModId, m2.ModId);
            Assert.Equal(m.Version, m2.Version);
            Assert.Equal(m.SharedModule, m2.SharedModule);
            Assert.Equal(2, m2.Dependencies.Count);
            Assert.Equal(2, m2.Protocols.Count);

            // 环境裁剪（§15.4）
            var hostMods = string.Join(",", m.ModulesFor(true, true));
            Assert.True(hostMods.Contains("Client") && hostMods.Contains("Server"));
            var serverMods = string.Join(",", m.ModulesFor(false, true));
            Assert.True(!serverMods.Contains("Client") && serverMods.Contains("Server"));
        }

        [Test]
        public static void Manifest_Pinned_RoundTrip()
        {
            // 主 Mod 标记（§10.4）：默认 false，序列化往返保持
            var m = ModManifest.Parse(@"{ ""id"": ""com.game.core"", ""version"": ""1.0.0"",
  ""modules"": { ""shared"": ""x.dll"" }, ""pinned"": true }");
            Assert.True(m.Pinned);
            var m2 = ModManifest.Parse(m.ToJson());
            Assert.True(m2.Pinned);
            Assert.True(m.ToJson().Contains("pinned"));

            var unpinned = ModManifest.Parse(@"{ ""id"": ""com.game.core"", ""version"": ""1.0.0"",
  ""modules"": { ""shared"": ""x.dll"" } }");
            Assert.True(!unpinned.Pinned);
            Assert.True(!unpinned.ToJson().Contains("pinned"));
        }

        [Test]
        public static void ManifestV1_CompatParse()
        {
            // v1 清单仍可解析（modId / entryDll / dependencies 对象形式）
            var v1 = @"{ ""modId"": ""com.sample.hello"", ""version"": ""0.1.0"",
  ""entryDll"": ""com.sample.hello.dll"",
  ""dependencies"": { ""com.game.core"": "">=1.0.0"" } }";
            var m = ModManifest.Parse(v1);
            Assert.Equal("com.sample.hello", m.ModId.Value);
            Assert.Equal("com.sample.hello.dll", m.SharedModule);
            Assert.Equal(1, m.Dependencies.Count);
            Assert.Equal("com.game.core", m.Dependencies[0].Id.Value);
        }

        [Test]
        public static void Manifest_RealModJson_Parse()
        {
            // 仓库内真实 mod.json 全部可解析且 entry 指向有效程序集名
            foreach (var modId in new[] { "com.game.core", "com.game.network", "com.game.ui", "com.sample.hello", "com.sample.pushbox" })
            {
                var m = ModManifest.Parse(RepoPaths.ModJson(modId));
                Assert.Equal(modId, m.ModId.Value);
                Assert.True(m.SharedModule.Length > 0, $"{modId} 缺少 modules.shared");
                Assert.True(m.Entry.Length > 0, $"{modId} 缺少 entry");
            }
        }

        [Test]
        public static void VersionRange_Semver()
        {
            Assert.True(VersionRange.Parse(">=1.0.0").SatisfiedBy(ModVersion.Parse("1.2.3")));
            Assert.True(!VersionRange.Parse(">=1.0.0").SatisfiedBy(ModVersion.Parse("0.9.9")));
            Assert.True(VersionRange.Parse("^1.2.0").SatisfiedBy(ModVersion.Parse("1.9.0")));
            Assert.True(!VersionRange.Parse("^1.2.0").SatisfiedBy(ModVersion.Parse("2.0.0")));
            Assert.True(VersionRange.Parse("~1.2.0").SatisfiedBy(ModVersion.Parse("1.2.9")));
            Assert.True(!VersionRange.Parse("~1.2.0").SatisfiedBy(ModVersion.Parse("1.3.0")));
            Assert.True(VersionRange.Any.SatisfiedBy(ModVersion.Parse("0.0.1")));
        }

        [Test]
        public static void TopoSort_DepsFirst()
        {
            var items = new[]
            {
                (new ModId("com.c"), new[] { new ModId("com.a"), new ModId("com.b") }),
                (new ModId("com.a"), Array.Empty<ModId>()),
                (new ModId("com.b"), new[] { new ModId("com.a") }),
            };
            var order = DependencyResolver.TopoSort(items, x => x.Item1, x => x.Item2);
            Assert.Equal("com.a", order[0].Item1.Value);
            Assert.Equal("com.b", order[1].Item1.Value);
            Assert.Equal("com.c", order[2].Item1.Value);
        }

        [Test]
        public static void TopoSort_CycleDetection()
        {
            var items = new[]
            {
                (new ModId("com.a"), new[] { new ModId("com.b") }),
                (new ModId("com.b"), new[] { new ModId("com.a") }),
            };
            Assert.Throws<ModDependencyException>(() =>
                DependencyResolver.TopoSort(items, x => x.Item1, x => x.Item2));
        }
    }
}
