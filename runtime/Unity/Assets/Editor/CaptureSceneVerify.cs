using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Runtime.Editor
{
    /// <summary>验收截图：3/4 俯视相机 + 左上方向光，捕获 blockout 场景供与参考图对比。[InitializeOnLoad] 防域重载丢订阅。</summary>
    [InitializeOnLoad]
    public static class CaptureSceneVerify
    {
        private const string ArmedKey = "Game.Runtime.CaptureSceneVerify.Armed";
        private static int _frames;

        static CaptureSceneVerify()
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
            if (++_frames < 60) return; // 等 Mods 加载 + blockout 构建
            SessionState.SetBool(ArmedKey, false);

            // 重定位宿主主相机到 3/4 俯视视角（参考图机位）
            var cam = Camera.main;
            if (cam is null) { Debug.LogError("[Verify] Camera.main 为空"); EditorApplication.Exit(1); return; }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.59f, 0.78f, 0.92f); // 浅青蓝天空
            cam.transform.position = new Vector3(45f, 45f, -25f);
            cam.transform.LookAt(new Vector3(0f, 12f, 20f));
            cam.fieldOfView = 55f;

            // 渐变天空：朝相机的大矩形面片 + 垂直渐变纹理（深海军蓝 → 浅青蓝）
            var sky = new GameObject("SkyBackdrop");
            var mf = sky.AddComponent<MeshFilter>();
            var mr = sky.AddComponent<MeshRenderer>();
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-100f, 0f, 0f), new Vector3(100f, 0f, 0f), new Vector3(-100f, 100f, 0f), new Vector3(100f, 100f, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mf.mesh = mesh;
            sky.transform.position = cam.transform.position + cam.transform.forward * 90f;
            sky.transform.rotation = Quaternion.LookRotation(cam.transform.forward);
            mr.material = new Material(Shader.Find("Unlit/Texture")) { mainTexture = GradientSky() };

            // 左上方向光（参考图：白昼冷色、左上受光）
            var lightGo = new GameObject("VerifyLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -40f, 0f);
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft; // 软阴影找回体积

            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();

            var outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "scene_verify.png"));
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Debug.Log($"[Verify] 截图已存 {outPath}");

            EditorApplication.ExitPlaymode();
            EditorApplication.Exit(0);
        }

        private static Texture2D GradientSky()
        {
            const int h = 256, w = 8;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var top = new Color(35f / 255f, 70f / 255f, 135f / 255f);   // 深海军蓝
            var bot = new Color(150f / 255f, 200f / 255f, 235f / 255f); // 浅青蓝
            for (var y = 0; y < h; y++)
            {
                // y=0 为纹理底部（地平线浅青蓝），y=h-1 为纹理顶部（深海军蓝）
                var c = Color.Lerp(bot, top, y / (float)(h - 1));
                for (var x = 0; x < w; x++) tex.SetPixel(x, y, c);
            }
            tex.Apply();
            return tex;
        }
    }
}
