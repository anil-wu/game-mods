using System;
using Com.Game.Ui;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>UI.Mod 测试（§10）：窗口注册 / 开关 / 层级 / CloseAll(modId) 热卸载。</summary>
    public static class UiTests
    {
        private static readonly ModId Weapon = new("com.game.weapon");
        private static readonly ModId Inventory = new("com.game.inventory");

        /// <summary>注册窗口的业务 Mod。</summary>
        public sealed class WindowedMod : IMod
        {
            public static readonly ModId Id = new("com.test.windowed");
            public static readonly WindowId Settings = new(Id, "settings");

            public void Register(IModContext context)
            {
                context.Ui.RegisterWindow(new WindowDescriptor
                {
                    Id = Settings,
                    Layer = UiLayer.Popup,
                    Prefab = "settings_panel",
                    Poolable = true,
                });
                context.Ui.Open(Settings);
            }

            public void Unregister(IModContext context) { }
        }

        [Test]
        public static void WindowManager_RegisterOpenClose()
        {
            var wm = new WindowManager();
            wm.RegisterWindow(Weapon, new WindowDescriptor { Id = new WindowId(Weapon, "hud"), Layer = UiLayer.Normal });
            wm.RegisterWindow(Weapon, new WindowDescriptor { Id = new WindowId(Weapon, "settings"), Layer = UiLayer.Popup });

            Assert.True(!wm.IsOpen(new WindowId(Weapon, "hud")));
            var handle = wm.Open(Weapon, new WindowId(Weapon, "hud"), WindowOptions.Default);
            Assert.True(wm.IsOpen(new WindowId(Weapon, "hud")));
            Assert.True(handle.IsValid);

            wm.Open(Inventory, new WindowId(Weapon, "settings"), WindowOptions.Default);
            var byLayer = wm.OpenWindowsByLayer;
            Assert.Equal("com.game.weapon:hud", byLayer[0].ToString());      // Normal 在 Popup 之前
            Assert.Equal("com.game.weapon:settings", byLayer[1].ToString());

            wm.Close(Weapon, new WindowId(Weapon, "hud"));
            Assert.True(!handle.IsValid);
        }

        [Test]
        public static void WindowManager_ForeignNamespace_Rejected()
        {
            var wm = new WindowManager();
            Assert.Throws<ModStateException>(() =>
                wm.RegisterWindow(Weapon, new WindowDescriptor { Id = new WindowId(Inventory, "bag") }));
        }

        [Test]
        public static void WindowManager_CloseAll_ByOwner()
        {
            var wm = new WindowManager();
            wm.RegisterWindow(Weapon, new WindowDescriptor { Id = new WindowId(Weapon, "hud") });
            wm.RegisterWindow(Inventory, new WindowDescriptor { Id = new WindowId(Inventory, "bag") });
            wm.Open(Weapon, new WindowId(Weapon, "hud"), WindowOptions.Default);
            wm.Open(Inventory, new WindowId(Inventory, "bag"), WindowOptions.Default);

            wm.CloseAll(Weapon); // §10.8：热卸载强制关闭，不依赖业务 Mod 自觉
            Assert.True(!wm.IsOpen(new WindowId(Weapon, "hud")));
            Assert.True(wm.IsOpen(new WindowId(Inventory, "bag")));
            Assert.Equal(0, wm.OpenCountOf(Weapon)); // 卸载完成校验引用清零（§10.8）
        }

        [Test]
        public static void Unload_Mod_ClosesWindows_ViaManager()
        {
            // 端到端：UI.Mod + 业务 Mod → 卸载业务 Mod → ModManager 强制 CloseAll（§10.8）
            var host = new ModRuntimeHost(RuntimeRole.Host);
            host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.ui")), typeof(UiMod).Assembly);
            host.Load(new ModManifest
            {
                ModId = WindowedMod.Id,
                Version = new ModVersion(1, 0, 0),
                Entry = typeof(WindowedMod).FullName!,
                SharedModule = "test.dll",
                Dependencies =
                {
                    new ModDependency { Id = new ModId("com.game.ui"), Version = VersionRange.Any },
                },
            }, typeof(WindowedMod).Assembly);

            Assert.True(UiMod.Current!.IsOpen(WindowedMod.Settings));

            host.Manager.Unload(WindowedMod.Id);
            Assert.True(!UiMod.Current!.IsOpen(WindowedMod.Settings)); // 强制关闭，无残留引用
        }

        /// <summary>记录 UI 能力不可用异常的 Mod。</summary>
        public sealed class UiProbeMod : IMod
        {
            public static readonly ModId Id = new("com.test.uiprobe");
            public static Exception? UiError;

            public void Register(IModContext context)
            {
                try { context.Ui.RegisterWindow(new WindowDescriptor { Id = new WindowId(Id, "w") }); }
                catch (Exception e) { UiError = e; }
            }

            public void Unregister(IModContext context) { }
        }

        [Test]
        public static void UiContext_WithoutUiMod_Throws_NoService()
        {
            // UI.Mod 未加载时，UI 能力路由到空 → NoServiceException（§10.2 服务路由）
            UiProbeMod.UiError = null;
            var host = new ModRuntimeHost(RuntimeRole.Host);
            host.Load(new ModManifest
            {
                ModId = UiProbeMod.Id,
                Version = new ModVersion(1, 0, 0),
                Entry = typeof(UiProbeMod).FullName!,
                SharedModule = "test.dll",
            }, typeof(UiProbeMod).Assembly);
            Assert.True(UiProbeMod.UiError is NoServiceException,
                $"期望 NoServiceException，实际 {UiProbeMod.UiError}");
        }
    }
}
