using System;
using System.Collections.Generic;
using System.IO;
using Game.Mod.Contract;

namespace Game.ModLoader
{
    /// <summary>
    /// Mod 发现：扫描目录，每个含 manifest.json 的子目录视为一个 Mod 包。
    /// </summary>
    public static class ModDiscovery
    {
        public const string ManifestFileName = "manifest.json";

        public static List<ModPackage> Discover(string modsDirectory)
        {
            var result = new List<ModPackage>();
            if (!Directory.Exists(modsDirectory)) return result;

            foreach (var dir in Directory.EnumerateDirectories(modsDirectory))
            {
                var manifestPath = Path.Combine(dir, ManifestFileName);
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var manifest = ModManifest.Parse(File.ReadAllText(manifestPath));
                    result.Add(new ModPackage(manifest, dir));
                }
                catch (Exception)
                {
                    // 坏包跳过，由调用方决定是否报告
                }
            }
            return result;
        }
    }

}
