using System;
using System.IO;
using Game.Mod.Contract;

namespace Game.ModLoader
{
    /// <summary>
    /// 一个已定位的 Mod 包（manifest + 物理目录）。
    /// </summary>
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

}
