using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>UI 层级（§10.6）。</summary>
    public enum UiLayer
    {
        Background,
        Normal,
        Popup,
        Top,
        System,
    }

    /// <summary>窗口打开选项。</summary>
    public readonly struct WindowOptions
    {
        public readonly bool Modal;

        public WindowOptions(bool modal) => Modal = modal;

        public static WindowOptions Default => new(false);
    }

    /// <summary>
    /// 窗口声明（§10.6）：业务 Mod 在 IMod.Register 中声明；
    /// Prefab 从本 Mod 的 ResourceScope 解析，实例走本 Mod 的 UI Pool（§10.7）。
    /// </summary>
    public sealed class WindowDescriptor
    {
        public WindowId Id { get; set; }
        public UiLayer Layer { get; set; } = UiLayer.Normal;
        public string Prefab { get; set; } = "";
        public bool Poolable { get; set; }
    }

    /// <summary>窗口句柄（Core 只持句柄，实例归业务 Mod，§10.7）。</summary>
    public interface IWindowHandle
    {
        WindowId Id { get; }
        bool IsValid { get; }
    }

    /// <summary>
    /// Core 中的稳定 UI 抽象（§10.6）：业务 Mod 只依赖此接口，永远不引用 UI 实现——
    /// UGUI / UIToolkit / FairyGUI 只是 UI.Mod 的不同实现，业务 Mod 无感知（§10.3）。
    /// 路由到 UI.Mod；UI.Mod 未加载时抛 <see cref="NoServiceException"/>。
    /// </summary>
    public interface IUiContext
    {
        /// <summary>声明窗口（在 IMod.Register 中调用，§10.6）。</summary>
        void RegisterWindow(WindowDescriptor descriptor);

        IWindowHandle Open(WindowId id, WindowOptions options = default);
        void Close(WindowId id);
        bool IsOpen(WindowId id);
    }

    /// <summary>
    /// UI.Mod 实现的窗口管理器（§10.1）：Window 注册表 / 层级 / 窗口栈 / 焦点归 UI.Mod；
    /// 窗口 Prefab / ViewModel / 实例归业务 Mod。卸载时 ModManager 强制 CloseAll(modId)（§10.8）。
    /// </summary>
    public interface IWindowManager
    {
        void RegisterWindow(ModId owner, WindowDescriptor descriptor);
        void UnregisterAll(ModId owner);
        IWindowHandle Open(ModId caller, WindowId id, WindowOptions options);
        void Close(ModId caller, WindowId id);
        bool IsOpen(WindowId id);

        /// <summary>强制关闭某 Mod 的全部窗口（热卸载能干净的关键，§10.8）。</summary>
        void CloseAll(ModId owner);
    }
}
