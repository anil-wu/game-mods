using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Runtime.Editor
{
    /// <summary>
    /// Mod AssetBundle 构建器（§8.2/§15.1）：把各 Mod 包的 `assets/` 目录打进
    /// 各自命名的 AssetBundle，输出到 StreamingAssets/mods/&lt;modId&gt;/&lt;modId&gt;.bundle。
    /// 命令行：Unity.exe -batchmode -nographics -executeMethod Game.Runtime.Editor.ModAssetBundleBuilder.Build -quit
    /// </summary>
    public static class ModAssetBundleBuilder
    {
        [MenuItem("Mod/构建全部 Mod AssetBundle")]
        public static void Build()
        {
            var projectDir = Directory.GetParent(Application.dataPath)!.FullName; // runtime/Unity
            var repoDir = Path.GetFullPath(Path.Combine(projectDir, "..", ".."));
            var modAssetsRoot = "Assets/ModAssets";

            // 1. 清理 + 拷贝各 Mod 的 assets/ 到工程内（供 Unity 导入）
            if (AssetDatabase.IsValidFolder(modAssetsRoot))
                AssetDatabase.DeleteAsset(modAssetsRoot);
            AssetDatabase.CreateFolder("Assets", "ModAssets");

            var modDirs = new List<string>();
            modDirs.AddRange(Directory.GetDirectories(Path.Combine(repoDir, "mods")));
            modDirs.AddRange(Directory.GetDirectories(Path.Combine(repoDir, "samples", "*", "mods"))
                .SelectMany(Directory.GetDirectories));

            var bundleMods = new List<string>();
            foreach (var modDir in modDirs)
            {
                var assetsDir = Path.Combine(modDir, "assets");
                if (!Directory.Exists(assetsDir)) continue;
                var modId = Path.GetFileName(modDir);
                var target = Path.Combine(projectDir, modAssetsRoot, modId);
                Directory.CreateDirectory(target);
                CopyDir(assetsDir, target);
                bundleMods.Add(modId);
            }

            AssetDatabase.Refresh();

            // 2. 声明 AssetBundle
            var builds = new List<AssetBundleBuild>();
            foreach (var modId in bundleMods)
            {
                var folder = Path.Combine(projectDir, modAssetsRoot, modId);
                var assetNames = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".meta"))
                    .Select(f => "Assets" + f.Substring(projectDir.Length).Replace('\\', '/'))
                    .ToArray();
                if (assetNames.Length == 0) continue;
                builds.Add(new AssetBundleBuild { assetBundleName = modId, assetNames = assetNames });
            }

            if (builds.Count == 0)
            {
                Debug.Log("[ModAssetBundleBuilder] 无 Mod 含 assets 目录，跳过");
                return;
            }

            // 3. 构建到 StreamingAssets/mods/<modId>/<modId>.bundle
            var outputRoot = "Assets/StreamingAssets/mods";
            Directory.CreateDirectory(Path.Combine(projectDir, outputRoot));
            BuildPipeline.BuildAssetBundles(outputRoot, builds.ToArray(),
                BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);

            foreach (var modId in bundleMods)
            {
                var built = Path.Combine(projectDir, outputRoot, modId);
                var bundleFile = Path.Combine(projectDir, outputRoot, modId, $"{modId}.bundle");
                if (File.Exists(built))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(bundleFile)!);
                    File.Move(built, bundleFile);
                }
            }

            AssetDatabase.Refresh();
            Debug.Log($"[ModAssetBundleBuilder] 完成：{string.Join(", ", bundleMods)}");
        }

        private static void CopyDir(string src, string dst)
        {
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(src))
                CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }
}
