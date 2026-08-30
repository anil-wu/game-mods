using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// Mod 运行时测试：生命周期 / 热卸载管线 / ModCall / 资源域 / 池域 / 端侧裁剪。
    /// 测试 Mod 直接编译进测试程序集，经 manifest.Entry 精确解析入口。
    /// ModCall 一律二进制（Rule 14）：DataCodec 编解码纯数据。
    /// </summary>
    public static class ModRuntimeTests
    {
        // ---- 测试 Mod ----

        /// <summary>测试辅助：二进制 ModCall（编码入参 → Call → 解码返回）。</summary>
        private static object?[] InvokeCap(IModManager mods, ModId target, CapabilityId cap, params object?[] args)
        {
            var buf = mods.Call(target, cap, DataCodec.Write(args));
            return DataCodec.Read(new PayloadReader(buf));
        }

        public sealed class NoopSystem : ISystem
        {
            public void Update(SystemContext context) { }
        }

        public sealed class ItemFactory : IPoolFactory
        {
            public int Created;
            public object Create() { Created++; return new object(); }
            public void Reset(object instance) { }
        }

        public sealed class InventoryMod : IMod
        {
            public static readonly ModId Id = new("com.test.inventory");
            public static readonly CapabilityId HasItemCap = new(Id, "has_item");
            public static readonly ServiceId StoreService = new(Id, "store");
            public static readonly PoolId ItemPool = new(Id, "items");
            public static readonly MessageId ItemAddedMsg = new(Id, "item_added");

            public static IModContext? Ctx;
            public static ItemFactory? Factory;

            public void Register(IModContext context)
            {
                Ctx = context;
                Factory = new ItemFactory();
                context.Mods.Export(HasItemCap, reader =>
                {
                    var a = DataCodec.Read(reader);
                    return DataCodec.Write(new object?[] { a.Length > 0 && a[0] is int n && n > 0 });
                });
                context.Services.Register(StoreService, new object());
                context.Pool.RegisterPool(ItemPool, Factory, prewarm: 2);
                context.Ecs.RegisterSystem(new NoopSystem(), SystemSide.Shared);
            }

            public void Unregister(IModContext context) { Ctx = null; }
        }

        /// <summary>声明依赖 inventory 的 Mod。</summary>
        public sealed class WeaponMod : IMod
        {
            public static readonly ModId Id = new("com.test.weapon");
            public static bool? CallResult;
            public static bool Subscribed;

            public void Register(IModContext context)
            {
                var r = InvokeCap(context.Mods, InventoryMod.Id, InventoryMod.HasItemCap, 5);
                CallResult = r.Length > 0 && r[0] is bool b && b;
                context.Messages.Subscribe(InventoryMod.ItemAddedMsg, static (in Game.Messaging.MessageEnvelope _, Game.Mod.Contract.Wire.PayloadReader _) => { });
                Subscribed = true;
            }

            public void Unregister(IModContext context) { }
        }

        /// <summary>未声明依赖却尝试 Call 的 Mod（应被 Core 拒绝，§12.11 规则 1）。</summary>
        public sealed class RogueMod : IMod
        {
            public static readonly ModId Id = new("com.test.rogue");
            public static Exception? CallError;

            public void Register(IModContext context)
            {
                try { InvokeCap(context.Mods, InventoryMod.Id, InventoryMod.HasItemCap, 1); }
                catch (Exception e) { CallError = e; }
            }

            public void Unregister(IModContext context) { }
        }

        // ---- 辅助 ----

        private static ModManifest Manifest(string id, string entry, params ModDependency[] deps) => new()
        {
            ModId = new ModId(id),
            Version = new ModVersion(1, 0, 0),
            Entry = entry,
            SharedModule = "test.dll",
            Dependencies = deps.ToList(),
        };

        private static ModDependency Dep(string id, bool optional = false, ModScope scope = ModScope.Shared) =>
            new() { Id = new ModId(id), Version = VersionRange.Any, Optional = optional, Scope = scope };

        private static ModRuntimeHost Host(RuntimeRole role = RuntimeRole.Host) => new(role);

        private static ModManifest InventoryManifest() => Manifest("com.test.inventory", typeof(InventoryMod).FullName!);

        private static ModManifest WeaponManifest() =>
            Manifest("com.test.weapon", typeof(WeaponMod).FullName!, Dep("com.test.inventory"));

        // ---- 生命周期 ----

        [Test]
        public static void Load_DependenciesFirst_And_ModCall()
        {
            var host = Host();
            WeaponMod.CallResult = null;
            // 只显式加载 weapon，inventory 应由依赖递归先加载（§12.3 依赖在前）
            host.Manager.RegisterManifest(InventoryManifest(), new[] { typeof(InventoryMod).Assembly });
            host.Manager.RegisterManifest(WeaponManifest(), new[] { typeof(WeaponMod).Assembly });
            host.Manager.Load(WeaponMod.Id);

            Assert.True(host.Manager.IsLoaded(InventoryMod.Id));
            Assert.True(host.Manager.IsLoaded(WeaponMod.Id));
            Assert.Equal(true, WeaponMod.CallResult); // ModCall 成功（§12.11）
            Assert.True(host.Manager.CallTrace().Count >= 1); // 调用计入 Trace（§12.11 规则 5）
        }

        [Test]
        public static void Load_RoleCapabilities()
        {
            var host = Host(RuntimeRole.Server);
            host.Load(InventoryManifest(), typeof(InventoryMod).Assembly);
            var ctx = InventoryMod.Ctx!;
            Assert.True(!ctx.HasClient && ctx.HasServer); // Rule 2：端侧是 Context 能力
            Assert.True(ctx.Client is null && ctx.Server is not null);
        }

        [Test]
        public static void ModCall_Errors()
        {
            var host = Host();
            // 声明可选依赖 ghost（已声明但未加载 → NoModException 路径）
            var m = Manifest("com.test.inventory", typeof(InventoryMod).FullName!, Dep("com.test.ghost", optional: true));
            host.Load(m, typeof(InventoryMod).Assembly);
            var ctx = InventoryMod.Ctx!;

            // 目标未加载 → NoModException
            Assert.Throws<NoModException>(() =>
                ctx.Mods.Call(new ModId("com.test.ghost"), new CapabilityId(new ModId("com.test.ghost"), "x"), DataCodec.Write(null)));
            // 能力未导出 → NoCapabilityException
            Assert.Throws<NoCapabilityException>(() =>
                ctx.Mods.Call(InventoryMod.Id, new CapabilityId(InventoryMod.Id, "nonexistent"), DataCodec.Write(null)));
        }

        [Test]
        public static void ModCall_UndeclaredDependency_Rejected()
        {
            var host = Host();
            RogueMod.CallError = null;
            host.Load(InventoryManifest(), typeof(InventoryMod).Assembly);
            host.Load(Manifest("com.test.rogue", typeof(RogueMod).FullName!), typeof(RogueMod).Assembly);
            Assert.True(RogueMod.CallError is ModDependencyException,
                $"期望 ModDependencyException，实际 {RogueMod.CallError}");
        }

        [Test]
        public static void OptionalDependency_Missing_StillLoads()
        {
            var host = Host();
            var m = Manifest("com.test.rogue", typeof(RogueMod).FullName!, Dep("com.test.ghost", optional: true));
            host.Load(m, typeof(RogueMod).Assembly); // Optional 缺失仍可运行（§12.4）
            Assert.True(host.Manager.IsLoaded(RogueMod.Id));
        }

        [Test]
        public static void ScopeDependency_Filtered_ByRole()
        {
            // Client Scope 依赖在 Server 环境不生效（§12.6，Rule 8）
            var host = Host(RuntimeRole.Server);
            var m = Manifest("com.test.rogue", typeof(RogueMod).FullName!, Dep("com.test.ghost", scope: ModScope.Client));
            host.Load(m, typeof(RogueMod).Assembly);
            Assert.True(host.Manager.IsLoaded(RogueMod.Id));
        }

        // ---- 热卸载管线（§7） ----

        [Test]
        public static void Unload_DependentRejected()
        {
            var host = Host();
            host.Load(InventoryManifest(), typeof(InventoryMod).Assembly);
            host.Load(WeaponManifest(), typeof(WeaponMod).Assembly);
            Assert.Throws<ModStateException>(() => host.Manager.Unload(InventoryMod.Id)); // 仍被依赖
            host.Manager.Unload(WeaponMod.Id);   // 先卸依赖方（注销顺序与注册相反，§12.3）
            host.Manager.Unload(InventoryMod.Id);
            Assert.True(!host.Manager.IsLoaded(InventoryMod.Id));
        }

        [Test]
        public static void Unload_CleansEverything()
        {
            var host = Host();
            host.Load(InventoryManifest(), typeof(InventoryMod).Assembly);
            var ctx = InventoryMod.Ctx!;
            var systemsCount = host.Systems.Count;
            Assert.True(systemsCount > 0);

            host.Manager.Unload(InventoryMod.Id);

            // 服务撤销（经宿主注册表查验）
            Assert.True(!host.Services.TryGet(InventoryMod.StoreService, out _));
            // 系统移除
            Assert.Equal(0, host.Systems.CountOf(InventoryMod.Id));
            // ModObject 移除（能力随之撤销，再调用得到 NoModException 而非僵尸代码，§13.5）
            Assert.True(host.Manager.Get(InventoryMod.Id) is null);
            // Context 失效：残留引用立即报错而非野指针（§7）
            Assert.Throws<ModStateException>(() => ctx.Services.TryGet(InventoryMod.StoreService, out _));
        }

        [Test]
        public static void Unload_ForcesUnsubscribe()
        {
            var host = Host();
            host.Load(InventoryManifest(), typeof(InventoryMod).Assembly);
            host.Load(WeaponManifest(), typeof(WeaponMod).Assembly);
            Assert.Equal(1, host.Messages.SubscriberCount(InventoryMod.ItemAddedMsg));

            host.Manager.Unload(WeaponMod.Id);
            Assert.Equal(0, host.Messages.SubscriberCount(InventoryMod.ItemAddedMsg)); // Core 强制退订（§13.5）
        }

        // ---- 资源域（§8） ----

        [Test]
        public static void Resources_Scope_And_Isolation()
        {
            var dir = Path.Combine(Path.GetTempPath(), "modrt_res_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "data"));
            File.WriteAllText(Path.Combine(dir, "data", "config.json"), "{ \"hp\": 100 }");

            try
            {
                var host = Host();
                var m = InventoryManifest();
                host.Load(m, typeof(InventoryMod).Assembly, contentRoot: dir);
                var res = host.Manager.Get(InventoryMod.Id)!.Resources;

                // 本命名空间加载 OK
                var text = (string)res.Load(AssetId.Parse("com.test.inventory:data/config.json"));
                Assert.True(text.Contains("hp"));

                // 跨 Mod 直接 Load 被拒（§8.4）
                Assert.Throws<ResourceAccessException>(() =>
                    res.Load(AssetId.Parse("com.test.other:secret.json")));

                // 路径逃逸被拒
                Assert.Throws<ResourceAccessException>(() =>
                    res.Load(AssetId.Parse("com.test.inventory:../outside.txt")));

                // 子域释放（§8.8）
                var scope = res.CreateScope();
                scope.Load(AssetId.Parse("com.test.inventory:data/config.json"));
                scope.Dispose();

                // 卸载后资源域失效
                host.Manager.Unload(InventoryMod.Id);
                Assert.Throws<ModStateException>(() => res.Load(AssetId.Parse("com.test.inventory:data/config.json")));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ---- 池域（§9） ----

        [Test]
        public static void Pool_RentReturnClear()
        {
            var host = Host();
            host.Load(InventoryManifest(), typeof(InventoryMod).Assembly);
            var pool = host.Manager.Get(InventoryMod.Id)!.Pool;

            var a = pool.Rent(InventoryMod.ItemPool);
            var b = pool.Rent(InventoryMod.ItemPool);
            Assert.True(!ReferenceEquals(a, b));
            pool.Return(InventoryMod.ItemPool, a);
            var c = pool.Rent(InventoryMod.ItemPool);
            Assert.True(ReferenceEquals(a, c)); // 归还复用

            // 未注册池报错；外部命名空间注册被拒
            Assert.Throws<ModStateException>(() => pool.Rent(PoolId.Parse("com.test.inventory:ghost")));
            Assert.Throws<ModStateException>(() =>
                pool.RegisterPool(PoolId.Parse("com.test.other:x"), new ItemFactory()));

            host.Manager.Unload(InventoryMod.Id); // Clear() 由 Core 强制调用
        }

        // ---- 退出游戏：镜像销毁（UnloadAll） ----

        [Test]
        public static void UnloadAll_MirrorOfDependencyOrder()
        {
            var host = Host();
            host.Load(InventoryManifest(), typeof(InventoryMod).Assembly);
            host.Load(WeaponManifest(), typeof(WeaponMod).Assembly);
            host.Load(Manifest("com.test.rogue", typeof(RogueMod).FullName!), typeof(RogueMod).Assembly);

            var unloaded = new List<ModId>();
            host.Manager.ModUnloaded += id => unloaded.Add(id);

            var errors = host.Manager.UnloadAll();

            Assert.Equal(0, errors.Count);
            Assert.Equal(3, unloaded.Count);
            // 依赖方先卸：weapon（依赖 inventory）必须先于 inventory（§12.3 镜像）
            Assert.True(unloaded.IndexOf(WeaponMod.Id) < unloaded.IndexOf(InventoryMod.Id),
                $"卸载顺序错误: [{string.Join(", ", unloaded)}]");
            Assert.Equal(0, host.Manager.Loaded.Count);
            // 重复调用安全（空跑）
            Assert.Equal(0, host.Manager.UnloadAll().Count);
        }

        /// <summary>在系统 Update 中请求卸载自身的 Mod（P0：相位内卸载延迟到帧末）。</summary>
        public sealed class SelfUnloadInUpdateMod : IMod
        {
            public static readonly ModId Id = new("com.test.suicidal");

            public sealed class SuicideSystem : ISystem
            {
                public IModContext? Ctx;
                public bool Done;
                public void Update(SystemContext context)
                {
                    if (Done) return;
                    Done = true;
                    Ctx!.Mods.Unload(Id); // 迭代途中请求卸载——应入队而非崩溃
                }
            }

            public void Register(IModContext context)
                => context.Ecs.RegisterSystem(new SuicideSystem { Ctx = context }, SystemSide.Shared);

            public void Unregister(IModContext context) { }
        }

        [Test]
        public static void Unload_DuringSystemUpdate_DeferredToFrameEnd()
        {
            var host = Host();
            host.Load(Manifest("com.test.suicidal", typeof(SelfUnloadInUpdateMod).FullName!),
                typeof(SelfUnloadInUpdateMod).Assembly);
            Assert.True(host.Manager.IsLoaded(SelfUnloadInUpdateMod.Id));

            // Tick 中系统请求卸载自身：不得崩溃，帧末生效
            host.Tick(0.016f);
            Assert.True(!host.Manager.IsLoaded(SelfUnloadInUpdateMod.Id));
            Assert.Equal(0, host.Systems.CountOf(SelfUnloadInUpdateMod.Id));

            // 后续 Tick 正常
            host.Tick(0.016f);
        }

        [Test]
        public static void PinnedMod_CannotBeDestroyed()
        {
            var host = Host();
            var pinned = InventoryManifest();
            pinned.Pinned = true; // 主 Mod 标记（§10.4）
            host.Load(pinned, typeof(InventoryMod).Assembly);
            host.Load(WeaponManifest(), typeof(WeaponMod).Assembly);

            // 单个卸载主 Mod → 拒绝
            Assert.Throws<ModStateException>(() => host.Manager.Unload(InventoryMod.Id));

            // 退出游戏：主 Mod 跳过销毁，其余按镜像卸载
            host.Manager.UnloadAll();
            Assert.True(host.Manager.IsLoaded(InventoryMod.Id));
            Assert.True(!host.Manager.IsLoaded(WeaponMod.Id));
        }

        [Test]
        public static void AssemblyLoader_Dedup_ReusesLoadedCopy()
        {
            // Unity 字节加载不可卸载：同名程序集必须复用首份副本，
            // 否则卸载（静态桥清空）后重装会绑到静态桥已死的旧副本（NRE）
            var dllPath = Path.Combine(RepoPaths.Root, "tests", "TestPlugin", "bin", "Release", "netstandard2.1", "com.test.plugin.dll");
            Assert.True(File.Exists(dllPath), $"插件 DLL 不存在: {dllPath}");

            var loader = new ByteArrayAssemblyLoader();
            var first = loader.Load(new[] { dllPath });
            var second = loader.Load(new[] { dllPath });
            Assert.True(ReferenceEquals(first.Assemblies[0], second.Assemblies[0]),
                "同名程序集未复用——会产生多份静态状态分歧的副本");
        }

        // ---- ECS 归属 ----

        [Test]
        public static void Ecs_OwnerTracking()
        {
            var host = Host();
            host.Load(InventoryManifest(), typeof(InventoryMod).Assembly);
            Assert.Equal(1, host.Systems.CountOf(InventoryMod.Id));
            host.Manager.Unload(InventoryMod.Id);
            Assert.Equal(0, host.Systems.CountOf(InventoryMod.Id)); // 卸载移除系统（§9.2）
        }
    }
}
