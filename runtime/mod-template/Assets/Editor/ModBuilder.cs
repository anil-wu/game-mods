using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Mod 工程构建器（本脚本随 Mod 工程一起，由 build-mod-unity.py 经 Unity 批处理调用）。
/// 产出 .mod 包：{modId}.dll（asmdef 编译程序集）+ {modId}.bundle（资源包）+ manifest.json。
/// </summary>
public static class ModBuilder
{
    public static void Build()
    {
        var outputRoot = System.Environment.GetEnvironmentVariable("MOD_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputRoot))
        {
            Debug.LogError("[ModBuilder] 缺少 MOD_OUTPUT_DIR");
            EditorApplication.Exit(1);
            return;
        }

        // 1. ModId = 工程内唯一的 asmdef 名（如 com.fps.player.asmdef）
        var asmdefs = Directory.GetFiles("Assets", "*.asmdef", SearchOption.AllDirectories)
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(n => n != "ModBuilder") // 排除构建器自身
            .ToArray();
        if (asmdefs.Length != 1)
        {
            Debug.LogError($"[ModBuilder] 需且仅需一个 Mod 程序集 asmdef，实际 {asmdefs.Length} 个");
            EditorApplication.Exit(1);
            return;
        }
        var modId = asmdefs[0];

        // 2. 构建 AssetBundle：资源（排除 Scripts/Plugins/Editor/asmdef）
        var assets = Directory.GetFiles("Assets", "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".meta"))
            .Where(f => !f.EndsWith(".cs"))
            .Where(f => !f.EndsWith(".asmdef"))
            .Where(f => !f.Replace('\\', '/').StartsWith("Assets/Scripts/"))
            .Where(f => !f.Replace('\\', '/').StartsWith("Assets/Plugins/"))
            .Where(f => !f.Replace('\\', '/').StartsWith("Assets/Editor/"))
            .ToArray();

        var bundleDir = Path.Combine(Application.dataPath, "..", "AssetBundleBuild");
        Directory.CreateDirectory(bundleDir);
        if (assets.Length > 0)
        {
            var build = new AssetBundleBuild { assetBundleName = modId, assetNames = assets };
            BuildPipeline.BuildAssetBundles(bundleDir, new[] { build },
                BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);
        }

        // 3. 聚合 .mod 包
        var outDir = Path.Combine(outputRoot, modId);
        Directory.CreateDirectory(outDir);

        // 代码程序集（Unity 编译产物）
        var dll = Path.Combine("Library", "ScriptAssemblies", $"{modId}.dll");
        if (File.Exists(dll))
            File.Copy(dll, Path.Combine(outDir, $"{modId}.dll"), overwrite: true);
        else
            Debug.LogWarning($"[ModBuilder] 未找到程序集 {dll}");

        // 资源包
        var bundle = Path.Combine(bundleDir, modId);
        if (File.Exists(bundle))
            File.Copy(bundle, Path.Combine(outDir, $"{modId}.bundle"), overwrite: true);

        // manifest
        var modJson = Path.Combine(Application.dataPath, "..", "mod.json");
        if (File.Exists(modJson))
            File.Copy(modJson, Path.Combine(outDir, "manifest.json"), overwrite: true);

        Debug.Log($"[ModBuilder] 完成 {modId} → {outDir}");
        EditorApplication.Exit(0);
    }
}
