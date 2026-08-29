using Game.Mod.Runtime;
using UnityEditor;
using UnityEngine;

namespace Game.Runtime.Editor
{
    /// <summary>
    /// Play 模式冒烟测试：进入 Play 模式跑若干帧，校验预览启动与 Mod 加载，然后退出。
    /// 命令行: Unity.exe -batchmode -nographics -executeMethod Game.Runtime.Editor.AutoPlay.Run -quit
    /// </summary>
    public static class AutoPlay
    {
        private static int _frames;

        public static void Run()
        {
            EditorApplication.playModeStateChanged += OnPlayModeState;
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeState(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            _frames = 0;
            EditorApplication.update += OnUpdate;
        }

        private static void OnUpdate()
        {
            _frames++;
            if (_frames < 30) return;

            EditorApplication.update -= OnUpdate;
            var count = ModRuntime.Host?.Manager.Loaded.Count ?? 0;
            bool ok = ModRuntime.Initialized && count >= 2;
            Debug.Log(ok
                ? $"[AutoPlay] OK: 已加载 {count} 个 Mod"
                : $"[AutoPlay] FAIL: Initialized={ModRuntime.Initialized}, Mods={count}");

            EditorApplication.ExitPlaymode();
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
