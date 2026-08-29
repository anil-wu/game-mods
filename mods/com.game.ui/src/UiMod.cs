using Game.Mod.Runtime;

namespace Com.Game.Ui
{
    /// <summary>
    /// UI.Mod（§10）：UI 基础设施 Mod（Framework Mod，§10.4）——
    /// WindowManager 自身走 Mod 生命周期，改 UI 框架不需要动 Core；
    /// UGUI / UIToolkit / FairyGUI 只是 UI.Mod 的不同实现，业务 Mod 无感知（§10.3）。
    /// 只在 HasClient 环境生效（Dedicated Server 注册为空实现，§10.4 环境裁剪）。
    /// </summary>
    public sealed class UiMod : IMod
    {
        public static WindowManager? Current { get; private set; }

        public void Register(IModContext context)
        {
            if (!context.HasClient)
            {
                context.Log.Info($"UI.Mod '{context.Info.Id}' 在非 Client 环境退化为空（§10.2）");
                return;
            }

            var windowManager = new WindowManager();
            Current = windowManager;
            context.Services.Register(WellKnownServices.WindowManager, windowManager);
            context.Log.Info($"UI.Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            if (Current is not null)
            {
                // 关闭所有窗口、释放 UI.Mod 自己的资源（§10.2）
                foreach (var id in Current.OpenWindowsByLayer)
                    Current.Close(context.Info.Id, id);
            }
            context.Services.Unregister(WellKnownServices.WindowManager);
            Current = null;
        }
    }
}
