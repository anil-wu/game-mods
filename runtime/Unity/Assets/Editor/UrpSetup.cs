using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.Runtime.Editor
{
    /// <summary>
    /// 宿主 URP 配置：创建 UniversalRendererData + UniversalRenderPipelineAsset（带渲染器），
    /// 并指派到 Graphics/Quality。无渲染器的 URP 资产会在 CreatePipeline 时 NRE。
    /// 命令行：Unity -batchmode -nographics -executeMethod Game.Runtime.Editor.UrpSetup.Apply -quit
    /// </summary>
    public static class UrpSetup
    {
        public static void Apply()
        {
            const string rendererPath = "Assets/Settings/UniversalRenderer.asset";
            const string pipelinePath = "Assets/Settings/UniversalRenderPipelineAsset.asset";
            if (!AssetDatabase.IsValidFolder("Assets/Settings"))
                AssetDatabase.CreateFolder("Assets", "Settings");

            // 1. 渲染器（URP 资产必需，否则 CreatePipeline NRE）
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer is null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, rendererPath);
                Debug.Log($"[UrpSetup] 创建 UniversalRenderer {rendererPath}");
            }

            // 2. 管线资产：用带渲染器的工厂重建（Create(renderer) 正确挂载渲染器，避免 CreatePipeline NRE）
            if (AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(pipelinePath) is not null)
                AssetDatabase.DeleteAsset(pipelinePath);
            var urp = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(urp, pipelinePath);
            AssetDatabase.SaveAssets();
            var pipeline = (RenderPipelineAsset)urp;
            Debug.Log($"[UrpSetup] 创建 URP 管线资产 {pipelinePath}（含渲染器）");

            // 3. 指派
            GraphicsSettings.renderPipelineAsset = pipeline;
            QualitySettings.renderPipeline = pipeline;
            Debug.Log("[UrpSetup] URP 管线已指派（含渲染器）");
        }
    }
}
