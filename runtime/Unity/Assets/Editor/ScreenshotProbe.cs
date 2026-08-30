using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Game.Runtime.Editor
{
    /// <summary>最终效果验证探针：加载全部关键模型 → 渲染 → 像素分析（洋红占比）+ 逐模型材质 shader dump。</summary>
    public static class ScreenshotProbe
    {
        private static readonly (string Mod, string Bundle, string Asset)[] Models =
        {
            ("player", "dist/mods/com.fps.player/com.fps.player.bundle",
             "assets/mods/com.fps.player/model/futuristic_soldier/prefabs/sci-fi_character_unity_blue variant.prefab"),
            ("npc", "dist/mods/com.fps.npc/com.fps.npc.bundle",
             "assets/mods/com.fps.npc/model/true_fantastic_creatures/centaur/prefabs/centaur.prefab"),
            ("weapon", "dist/mods/com.fps.weapon/com.fps.weapon.bundle",
             "assets/mods/com.fps.weapon/content/weapon/range/pistol/prefab/pistol fps local.prefab"),
            ("mapgen", "dist/mods/com.fps.mapgen/com.fps.mapgen.bundle",
             "assets/mods/com.fps.mapgen/content/world props/building/city building/bulding a.prefab"),
        };

        public static void Run()
        {
            var camGo = new GameObject("ProbeCam");
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<UniversalAdditionalCameraData>();
            cam.transform.position = new Vector3(0, 3f, -8f);
            cam.transform.LookAt(new Vector3(0, 1.5f, 0));
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.15f, 0.16f, 0.2f);

            var lightGo = new GameObject("ProbeLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);

            var x = -4.5f;
            foreach (var (name, bundleRel, assetName) in Models)
            {
                var bundlePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", bundleRel));
                Debug.Log($"[Probe] {name}: bundle存在={File.Exists(bundlePath)}");
                var bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle is null) { Debug.LogError($"[Probe] {name}: bundle 加载失败"); continue; }
                var prefab = bundle.LoadAsset<GameObject>(assetName);
                if (prefab is null)
                {
                    Debug.LogError($"[Probe] {name}: prefab 加载失败 {assetName}");
                    bundle.Unload(true);
                    continue;
                }
                var go = Object.Instantiate(prefab);
                go.transform.position = new Vector3(x, 0, 0);
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    var mat = r.sharedMaterial;
                    Debug.Log($"[Probe] {name} / {r.name} / 材质={(mat is null ? "null" : mat.name)} / shader={(mat is null ? "null" : mat.shader?.name)}");
                }
                bundle.Unload(false);
                x += 3f;
            }

            var rt = new RenderTexture(1024, 768, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1024, 768, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1024, 768), 0, 0);
            tex.Apply();

            var colors = tex.GetPixels();
            int magenta = 0, nonBg = 0;
            foreach (var c in colors)
            {
                if (c.r > 0.6f && c.b > 0.6f && c.g < 0.3f) magenta++;
                if (c.r > 0.3f || c.g > 0.3f || c.b > 0.3f) nonBg++;
            }
            Debug.Log($"[Probe] 洋红像素={magenta}（{(100f * magenta / colors.Length):F1}%），非背景像素={nonBg}（{(100f * nonBg / colors.Length):F1}%）");
            Debug.Log($"[Probe] 判定: {(magenta > colors.Length * 0.3f ? "整屏洋红（渲染损坏）" : "非洋红（渲染正常）")}");

            var outPath = Path.GetFullPath("probe_screenshot.png");
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Debug.Log($"[Probe] 截图已存 {outPath}");
            EditorApplication.Exit(0);
        }
    }
}
