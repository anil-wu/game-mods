using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Mod 打包工具（复刻案例工程内，§mod-unity-project.md）：
/// 扫描 Assets/Mods/ 下所有 mod.json——每个 mod.json 所在文件夹是一个独立 Mod，
/// 逐个独立打包为 .mod 包（{modId}.dll + {modId}.bundle + manifest.json）。
/// 命令行：Unity -batchmode -nographics -quit -executeMethod ModPacker.Pack -projectPath samples/MirrorUnityFPS
/// 环境变量 MOD_OUTPUT_DIR：输出根目录（默认 dist/mods）。
/// </summary>
public static class ModPacker
{
    public static void Pack()
    {
        var outputRoot = System.Environment.GetEnvironmentVariable("MOD_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputRoot))
            outputRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "dist", "mods"));

        // 内置管线：URP/HDRP/ShaderGraph 材质 → 内置 Standard（宿主走内置管线，渲染确定性可控）
        ConvertUrpMaterialsToBuiltin();

        var modJsonFiles = Directory.GetFiles("Assets/Mods", "mod.json", SearchOption.AllDirectories)
            .Where(p => p.Replace('\\', '/').StartsWith("Assets/Mods/"))
            .OrderBy(p => p)
            .ToArray();
        Debug.Log($"[ModPacker] 发现 {modJsonFiles.Length} 个 Mod");

        foreach (var modJson in modJsonFiles)
            PackOne(Path.GetDirectoryName(modJson)!, outputRoot);

        Debug.Log("[ModPacker] 全部完成");
        EditorApplication.Exit(0);
    }

    private static void PackOne(string modDir, string outputRoot)
    {
        var modId = Path.GetFileName(modDir); // Assets/Mods/com.fps.player → com.fps.player
        var outDir = Path.Combine(outputRoot, modId);
        Directory.CreateDirectory(outDir);

        // 1. 代码程序集（Mod 内唯一 asmdef 编译产物）
        var asmdefs = Directory.GetFiles(modDir, "*.asmdef", SearchOption.AllDirectories)
            .Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();
        if (asmdefs.Length == 1)
        {
            var dll = Path.Combine("Library", "ScriptAssemblies", $"{asmdefs[0]}.dll");
            if (File.Exists(dll))
                File.Copy(dll, Path.Combine(outDir, $"{asmdefs[0]}.dll"), overwrite: true);
        }

        // 2. 资源包（Mod 文件夹内资源，排除脚本/asmdef/mod.json/.meta）
        var assetFiles = Directory.GetFiles(modDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".meta"))
            .Where(f => !f.EndsWith(".cs"))
            .Where(f => !f.EndsWith(".asmdef"))
            .Where(f => Path.GetFileName(f) != "mod.json")
            .Where(f => Path.GetFileName(f) != "CONTRACT.md")
            .Select(f => f.Replace('\\', '/'))
            .ToArray();
        if (assetFiles.Length > 0)
        {
            var bundleDir = Path.Combine(Application.dataPath, "..", "BundleBuild");
            Directory.CreateDirectory(bundleDir);
            BuildPipeline.BuildAssetBundles(bundleDir,
                new[] { new AssetBundleBuild { assetBundleName = modId, assetNames = assetFiles } },
                BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);
            var built = Path.Combine(bundleDir, modId);
            if (File.Exists(built))
                File.Copy(built, Path.Combine(outDir, $"{modId}.bundle"), overwrite: true);
        }

        // 3. manifest
        File.Copy(Path.Combine(modDir, "mod.json"), Path.Combine(outDir, "manifest.json"), overwrite: true);

        Debug.Log($"[ModPacker] 打包 {modId} → {outDir}");
    }

    /// <summary>URP/HDRP/ShaderGraph 材质 → 内置 Standard，映射常用纹理/颜色属性。</summary>
    private static void ConvertUrpMaterialsToBuiltin()
    {
        var converted = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Mods" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat is null) continue;
            var shaderName = mat.shader?.name ?? "";
            if (!IsUrpShader(shaderName)) continue;

            var baseMap = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
            var baseColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
            var bumpMap = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
            var metallicGloss = mat.HasProperty("_MetallicGlossMap") ? mat.GetTexture("_MetallicGlossMap") : null;
            var occlusion = mat.HasProperty("_OcclusionMap") ? mat.GetTexture("_OcclusionMap") : null;

            mat.shader = Shader.Find("Standard");
            if (baseMap is not null) mat.SetTexture("_MainTex", baseMap);
            if (bumpMap is not null) mat.SetTexture("_BumpMap", bumpMap);
            if (metallicGloss is not null) mat.SetTexture("_MetallicGlossMap", metallicGloss);
            if (occlusion is not null) mat.SetTexture("_OcclusionMap", occlusion);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", baseColor);
            EditorUtility.SetDirty(mat);
            converted++;
        }
        if (converted > 0)
        {
            AssetDatabase.SaveAssets();
            Debug.Log($"[ModPacker] 材质转换 {converted} 个 → 内置 Standard");
        }
    }

    private static bool IsUrpShader(string shaderName)
    {
        if (shaderName.Contains("Universal")) return true;
        if (shaderName.Contains("HDRP")) return true;
        if (shaderName.Contains("Shader Graph")) return true;
        if (shaderName.Contains("SpeedTree")) return true;
        return false;
    }
}
