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
}
