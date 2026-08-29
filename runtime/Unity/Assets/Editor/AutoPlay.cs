using UnityEditor;
using UnityEngine;

namespace Game.Runtime.Editor
{
    /// <summary>
    /// Play 模式冒烟测试：进入 Play 模式跑若干帧，校验预览启动与 Mod 加载，然后退出。
    /// 命令行: Unity.exe -batchmode -nographics -executeMethod Game.Runtime.Editor.AutoPlay.Run
    /// （不带 -quit：EnterPlaymode 是异步的，-quit 会在进入 Play 前直接退出；由本类显式 Exit）
    ///
    /// [InitializeOnLoad]：进入 Play 模式触发域重载，静态事件订阅会丢失——
    /// 域重载后 Unity 重新执行静态构造函数完成重新订阅。
    /// </summary>
    [InitializeOnLoad]
    public static class AutoPlay
    {
        private const int FramesToRun = 30;
        private const int WatchdogTicks = 5000;

        private static int _frames;
        private static int _watchdog;
        private static bool _armed;   // 由 Run() 武装（仅 batchmode 入口）；域重载后由 EnteredPlayMode 接续
        private static bool _entered;

        static AutoPlay()
        {
            EditorApplication.playModeStateChanged += OnPlayModeState;
            EditorApplication.update += Watchdog;
        }

        public static void Run()
        {
            _armed = true;
            _entered = false;
            _frames = 0;
            _watchdog = 0;
            EditorApplication.EnterPlaymode();
        }

        private static void Watchdog()
        {
            if (!_armed || _entered) return;
            if (++_watchdog > WatchdogTicks)
                Finish(false, "超时未进入 Play 模式");
        }

        private static void OnPlayModeState(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            // 域重载后 _armed 已被重置——能进入 Play 只有 Run() 触发这一条路，直接接续
            _entered = true;
            _frames = 0;
            EditorApplication.update += OnUpdate;
        }

        private static void OnUpdate()
        {
            if (++_frames < FramesToRun) return;
            var count = ModRuntime.Host?.Manager.Loaded.Count ?? 0;
            Finish(ModRuntime.Initialized && count >= 2,
                $"Initialized={ModRuntime.Initialized}, Mods={count}");
        }

        private static void Finish(bool ok, string detail)
        {
            _armed = false;
            EditorApplication.update -= OnUpdate;
            Debug.Log(ok
                ? $"[AutoPlay] OK: 已加载 {detail}"
                : $"[AutoPlay] FAIL: {detail}");
            if (EditorApplication.isPlaying)
                EditorApplication.ExitPlaymode();
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
