using System;
using System.Collections.Generic;
using System.IO;
using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>一个已定位的 Mod 包（manifest + 物理目录）。</summary>
    public sealed class ModPackage
    {
        public ModManifest Manifest { get; }
        public string RootDirectory { get; }

        public ModPackage(ModManifest manifest, string rootDirectory)
        {
            Manifest = manifest;
            RootDirectory = rootDirectory;
        }

        /// <summary>按包内相对路径解析物理文件，不存在返回 null。</summary>
        public string? ResolveFile(string relativePath)
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.Combine(RootDirectory, normalized);
            return File.Exists(full) ? full : null;
        }

        public override string ToString() => Manifest.InstanceId;
    }

    /// <summary>Mod 发现：扫描目录，每个含 manifest.json 的子目录视为一个 Mod 包。</summary>
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
