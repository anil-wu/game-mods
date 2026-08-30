using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Runtime.Editor
{
    /// <summary>
    /// 宿主 URP 配置：创建 UniversalRenderPipelineAsset 并指派到 Graphics/Quality（Mod 资源是 URP 材质，§mod-unity-project.md）。
    /// 命令行：Unity -batchmode -nographics -executeMethod Game.Runtime.Editor.UrpSetup.Apply -quit
    /// </summary>
    public static class UrpSetup
    {
        public static void Apply()
        {
            const string path = "Assets/Settings/UniversalRenderPipelineAsset.asset";

            var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
            if (pipeline is null)
            {
                var urp = UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset.Create();
                AssetDatabase.CreateFolder("Assets", "Settings");
                AssetDatabase.CreateAsset(urp, path);
                pipeline = urp;
                AssetDatabase.SaveAssets();
                Debug.Log($"[UrpSetup] 创建 URP 资产 {path}");
            }

            GraphicsSettings.renderPipelineAsset = pipeline;
            QualitySettings.renderPipeline = pipeline;
            Debug.Log("[UrpSetup] URP 管线已指派");
        }
    }
}
