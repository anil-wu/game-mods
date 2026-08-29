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
    ///
    /// 安全围栏（硬规则）：冒烟逻辑必须同时满足两个条件才生效——
    ///   1. 批处理模式（Application.isBatchMode）：编辑器 GUI 里手动 Play 绝不触发 Exit；
    ///   2. SessionState 武装标记（由 -executeMethod Run() 设置，跨域重载存活）。
    /// </summary>
    [InitializeOnLoad]
    public static class AutoPlay
    {
        private const int FramesToRun = 30;
        private const int WatchdogTicks = 5000;
        private const string ArmedKey = "Game.Runtime.AutoPlay.Armed";

        private static int _frames;
        private static int _watchdog;
        private static bool _entered;

        static AutoPlay()
        {
            EditorApplication.playModeStateChanged += OnPlayModeState;
            EditorApplication.update += Watchdog;
        }

        /// <summary>仅由 batchmode -executeMethod 调用：武装冒烟测试并进入 Play。</summary>
        public static void Run()
        {
            SessionState.SetBool(ArmedKey, true);
            _entered = false;
            _frames = 0;
            _watchdog = 0;
            EditorApplication.EnterPlaymode();
        }

        /// <summary>双重围栏：批处理模式 + 显式武装标记，缺一不可。</summary>
        private static bool Armed =>
            Application.isBatchMode && SessionState.GetBool(ArmedKey, false);

        private static void Watchdog()
        {
            if (!Armed || _entered) return;
            if (++_watchdog > WatchdogTicks)
                Finish(false, "超时未进入 Play 模式");
        }

        private static void OnPlayModeState(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !Armed) return;
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
            SessionState.SetBool(ArmedKey, false);
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
