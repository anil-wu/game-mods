using System.Collections.Generic;
using System.IO;
using Game.Mod.Contract;
using Game.Mod.Runtime;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>
    /// Unity AssetBundle 资源后端（§8.2 / §15.1：Mod 自带 assets，技术由 Mod 自选）。
    /// 约定：Mod 包根目录含 `{modId}.bundle`（构建工具产物），
    /// AssetId "{modId}:{path}" → bundle.LoadAsset("assets/modassets/{modId}/{path}")。
    /// 一个 Mod 一个 Bundle，域释放时整域卸载（§8.8）。
    /// </summary>
    public sealed class UnityAssetBundleBackend : IResourceBackend, IScopedResourceBackend
    {
        private readonly Dictionary<string, AssetBundle> _bundles = new();

        /// <summary>Mod 包内相对路径 → bundle 内资源名。</summary>
        private static string AssetName(string contentRoot, string localPath)
        {
            var modId = Path.GetFileName(contentRoot.TrimEnd(Path.DirectorySeparatorChar));
            var key = localPath.Replace('/', Path.DirectorySeparatorChar);
            var withoutExt = key.Contains('.') ? key.Substring(0, key.LastIndexOf('.')) : key;
            // Unity 的 bundle 资源名全小写（相对 Assets 的项目路径去扩展名）
            return $"assets/mods/{modId}/{withoutExt}".ToLowerInvariant();
        }

        public object? Load(string contentRoot, string localPath)
        {
            var bundle = GetBundle(contentRoot);
            if (bundle is null) return null;
            // 新复刻结构 assets/mods/{modId}/…；兼容旧的 assets/modassets/{modId}/…
            var name = AssetName(contentRoot, localPath);
            var asset = bundle.LoadAsset(name);
            if (asset is null)
                asset = bundle.LoadAsset(name.Replace("assets/mods/", "assets/modassets/"));
            return asset;
        }

        public void Release(object resource)
        {
            // AssetBundle 资源不逐个卸载——整域卸载在 ReleaseScope 里 Unload(bundle)（§8.8）
        }

        public void ReleaseScope(string contentRoot)
        {
            if (_bundles.TryGetValue(contentRoot, out var bundle))
            {
                bundle.Unload(unloadAllLoadedObjects: true);
                _bundles.Remove(contentRoot);
            }
        }

        private AssetBundle? GetBundle(string contentRoot)
        {
            if (_bundles.TryGetValue(contentRoot, out var existing))
                return existing;

            // 资源包名以所属 Mod 的 manifest 声明为准（§15.1 assets.bundles；默认 {modId}.bundle）
            var bundleName = ResolveBundleName(contentRoot);
            var bundlePath = Path.Combine(contentRoot, bundleName);
            if (!File.Exists(bundlePath))
            {
                Debug.LogWarning($"[AssetBundleBackend] 未找到 Mod 资源包: {bundlePath}");
                return null;
            }

            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle is null)
            {
                Debug.LogError($"[AssetBundleBackend] 加载失败: {bundlePath}");
                return null;
            }
            _bundles[contentRoot] = bundle;
            return bundle;
        }

        private static string ResolveBundleName(string contentRoot)
        {
            var manifestPath = Path.Combine(contentRoot, "manifest.json");
            if (File.Exists(manifestPath))
            {
                try { return ModManifest.Parse(File.ReadAllText(manifestPath)).DefaultBundleName; }
                catch { /* 坏清单回退到约定名 */ }
            }
            var modId = Path.GetFileName(contentRoot.TrimEnd(Path.DirectorySeparatorChar));
            return $"{modId}.bundle";
        }
    }
}
