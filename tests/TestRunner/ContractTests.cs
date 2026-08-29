using System;
using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Contract.Json;

namespace TestRunner
{
    public static class ContractTests
    {
        [Test]
        public static void ModId_Parse()
        {
            Assert.True(ModId.TryParse("com.foo.bar", out _), "合法 modId 应解析成功");
            Assert.True(!ModId.TryParse("Foo.bar", out _), "大写应失败");
            Assert.True(!ModId.TryParse("com", out _), "单段应失败");
            Assert.True(!ModId.TryParse("com.foo!", out _), "非法字符应失败");
        }

        [Test]
        public static void VersionRange_Semver()
        {
            Assert.True(VersionRange.Parse(">=1.0.0").SatisfiedBy(ModVersion.Parse("1.5.0")), ">= 包含");
            Assert.True(!VersionRange.Parse(">=1.0.0").SatisfiedBy(ModVersion.Parse("0.9.0")), ">= 排除");
            Assert.True(VersionRange.Parse("^1.2.1").SatisfiedBy(ModVersion.Parse("1.9.0")), "^ 包含 minor");
            Assert.True(!VersionRange.Parse("^1.2.1").SatisfiedBy(ModVersion.Parse("2.0.0")), "^ 排除 major");
            Assert.True(VersionRange.Parse("~1.2.1").SatisfiedBy(ModVersion.Parse("1.2.9")), "~ 包含 patch");
            Assert.True(VersionRange.Parse("1.2.1").SatisfiedBy(ModVersion.Parse("1.2.1")), "精确匹配");
        }

        [Test]
        public static void TopoSort_DepsFirst()
        {
            var a = new ModId("com.a.x");
            var b = new ModId("com.b.y");
            var c = new ModId("com.c.z");
            var deps = new Dictionary<ModId, ModId[]>
            {
                [a] = new[] { b },
                [b] = new[] { c },
                [c] = Array.Empty<ModId>(),
            };
            var order = DependencyResolver.TopoSort(new[] { a, b, c }, x => x, x => deps[x]);
            Assert.Equal(c, order[0], "依赖在最前");
            Assert.Equal(b, order[1], "中间");
            Assert.Equal(a, order[2], "最后");
        }

        [Test]
        public static void TopoSort_CycleDetection()
        {
            var a = new ModId("com.a.x");
            var b = new ModId("com.b.y");
            var deps = new Dictionary<ModId, ModId[]>
            {
                [a] = new[] { b },
                [b] = new[] { a },
            };
            var threw = false;
            try { DependencyResolver.TopoSort(new[] { a, b }, x => x, x => deps[x]); }
            catch (ModDependencyException) { threw = true; }
            Assert.True(threw, "循环依赖应抛异常");
        }

        [Test]
        public static void Json_RoundTrip()
        {
            var v = JsonValue.Parse("{\"s\":\"a\\\"b\\\\c\\u0041\\n\",\"n\":123,\"arr\":[1,2,3],\"b\":true,\"nil\":null}");
            Assert.Equal("a\"b\\cA\n", v["s"].AsString, "转义+unicode 解析");
            Assert.Equal(123L, v["n"].AsLong, "整数");
            Assert.True(v["b"].AsBool, "布尔");
            Assert.True(v["nil"].Kind == JsonKind.Null, "null");
            Assert.Equal(3, v["arr"].AsArray.Count, "数组长度");
        }

        [Test]
        public static void Manifest_RoundTrip()
        {
            var m = new ModManifest
            {
                ModId = new ModId("com.test.main"),
                Version = ModVersion.Parse("2.0.0"),
                EntryDll = "com.test.main.dll",
                Dependencies = new Dictionary<ModId, VersionRange>
                {
                    [new ModId("com.test.dep")] = VersionRange.Parse(">=1.0.0"),
                },
                Files = new List<FileEntry>
                {
                    new FileEntry { Path = "a.dll", Type = FileType.Code, Size = 5, Sha256 = "x" },
                },
            };
            var p = ModManifest.Parse(m.ToJson());
            Assert.Equal(m.ModId, p.ModId, "modId 往返");
            Assert.Equal(m.Version, p.Version, "version 往返");
            Assert.Equal(1, p.Dependencies.Count, "依赖数量");
            Assert.Equal(1, p.Files.Count, "文件数量");
        }
    }
}
