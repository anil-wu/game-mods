using System;
using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace Com.Game.Ui
{
    /// <summary>
    /// WindowManager（§10.7）：Window 注册表 / 层级 / 开关调度归 UI.Mod；
    /// 窗口 Prefab / ViewModel / 实例归业务 Mod——这里只持有句柄与归属关系。
    /// 天然知道每个窗口属于哪个 ModObject（§10.5），这是热卸载能干净的关键。
    /// </summary>
    public sealed class WindowManager : IWindowManager
    {
        private sealed class WindowState
        {
            public WindowDescriptor Descriptor = null!;
            public ModId Owner;
            public bool IsOpen;
            public WindowOptions Options;
        }

        private sealed class Handle : IWindowHandle
        {
            private readonly WindowState _state;
            public WindowId Id => _state.Descriptor.Id;
            public bool IsValid => _state.IsOpen;

            public Handle(WindowState state) => _state = state;
        }

        private readonly Dictionary<WindowId, WindowState> _windows = new();

        /// <summary>打开的窗口按层级排序（Background → System）。</summary>
        public IReadOnlyList<WindowId> OpenWindowsByLayer
        {
            get
            {
                var list = new List<(UiLayer Layer, WindowId Id)>();
                foreach (var (id, state) in _windows)
                    if (state.IsOpen) list.Add((state.Descriptor.Layer, id));
                list.Sort((a, b) => a.Layer.CompareTo(b.Layer));
                var result = new List<WindowId>(list.Count);
                foreach (var (_, id) in list) result.Add(id);
                return result;
            }
        }

        public void RegisterWindow(ModId owner, WindowDescriptor descriptor)
        {
            if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
            if (descriptor.Id.Mod != owner)
                throw new ModStateException($"Mod '{owner}' 不能注册其他 Mod 命名空间的窗口 '{descriptor.Id}'");
            _windows[descriptor.Id] = new WindowState { Descriptor = descriptor, Owner = owner };
        }

        public void UnregisterAll(ModId owner)
        {
            var stale = new List<WindowId>();
            foreach (var (id, state) in _windows)
                if (state.Owner == owner) stale.Add(id);
            foreach (var id in stale) _windows.Remove(id);
        }

        public IWindowHandle Open(ModId caller, WindowId id, WindowOptions options)
        {
            if (!_windows.TryGetValue(id, out var state))
                throw new ModStateException($"窗口 '{id}' 未注册（§10.9：窗口必须经 IUiContext 注册和打开）");
            state.IsOpen = true;
            state.Options = options;
            return new Handle(state);
        }

        public void Close(ModId caller, WindowId id)
        {
            if (_windows.TryGetValue(id, out var state))
                state.IsOpen = false;
        }

        public bool IsOpen(WindowId id) =>
            _windows.TryGetValue(id, out var state) && state.IsOpen;

        /// <summary>强制关闭某 Mod 的全部窗口（§10.8：ModManager 在卸载流程中调用）。</summary>
        public void CloseAll(ModId owner)
        {
            foreach (var state in _windows.Values)
                if (state.Owner == owner)
                    state.IsOpen = false;
        }

        /// <summary>校验某 Mod 的窗口引用计数清零（§10.8 卸载完成校验）。</summary>
        public int OpenCountOf(ModId owner)
        {
            var n = 0;
            foreach (var state in _windows.Values)
                if (state.Owner == owner && state.IsOpen) n++;
            return n;
        }
    }
}
