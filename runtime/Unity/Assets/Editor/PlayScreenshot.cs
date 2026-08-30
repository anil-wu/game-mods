using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Runtime.Editor
{
    /// <summary>真实游戏画面截图（Play 模式 + 像素分析）。[InitializeOnLoad] 防域重载丢订阅。</summary>
    [InitializeOnLoad]
    public static class PlayScreenshot
    {
        private const string ArmedKey = "Game.Runtime.PlayScreenshot.Armed";
        private static int _frames;

        static PlayScreenshot()
        {
            EditorApplication.playModeStateChanged += OnState;
            EditorApplication.update += OnUpdate;
        }

        public static void Run()
        {
            SessionState.SetBool(ArmedKey, true);
            _frames = 0;
            EditorApplication.EnterPlaymode();
        }

        private static bool Armed => Application.isBatchMode && SessionState.GetBool(ArmedKey, false);

        private static void OnState(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !Armed) return;
            _frames = 0;
        }

        private static void OnUpdate()
        {
            if (!Armed) return;
            if (++_frames < 40) return;
            SessionState.SetBool(ArmedKey, false);

            var cam = Camera.main;
            if (cam is null)
            {
                Debug.Log("[PlayShot] Camera.main 为空");
                EditorApplication.ExitPlaymode();
                EditorApplication.Exit(1);
                return;
            }

            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();

            var colors = tex.GetPixels();
            int magenta = 0, nonBg = 0;
            foreach (var c in colors)
            {
                if (c.r > 0.6f && c.b > 0.6f && c.g < 0.3f) magenta++;
                if (c.r > 0.25f || c.g > 0.25f || c.b > 0.25f) nonBg++;
            }
            Debug.Log($"[PlayShot] 洋红={magenta}（{(100f * magenta / colors.Length):F1}%），非背景={nonBg}（{(100f * nonBg / colors.Length):F1}%）");

            var outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "game_screenshot.png"));
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Debug.Log($"[PlayShot] 截图已存 {outPath}");

            EditorApplication.ExitPlaymode();
            EditorApplication.Exit(0);
        }
    }
}
