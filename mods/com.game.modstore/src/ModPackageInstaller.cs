using System;
using System.IO;
using System.IO.Compression;
using Game.Mod.Contract;

namespace Com.Game.ModStore
{
    /// <summary>
    /// .mod 包（zip）安装器：解包到本地 mods 目录（{localModsDir}/{modId}/）。
    /// 防 zip-slip：条目路径越出目标目录一律拒绝（UGC 防护，§11.12 同源思想）。
    /// </summary>
    public static class ModPackageInstaller
    {
        /// <summary>解包 .mod zip 到 {localModsDir}/{modId}/，返回安装目录。</summary>
        public static string Install(byte[] packageBytes, string localModsDir)
        {
            ModManifest manifest;
            using (var zip = new ZipArchive(new MemoryStream(packageBytes), ZipArchiveMode.Read))
            {
                var manifestEntry = zip.GetEntry("manifest.json")
                    ?? throw new InvalidOperationException("包中缺少 manifest.json");
                using var reader = new StreamReader(manifestEntry.Open());
                manifest = ModManifest.Parse(reader.ReadToEnd());
            }

            var targetDir = Path.GetFullPath(Path.Combine(localModsDir, manifest.ModId.Value));
            Directory.CreateDirectory(targetDir);

            using (var zip = new ZipArchive(new MemoryStream(packageBytes), ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // 目录条目
                    var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                    if (!destPath.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"非法包条目路径（zip-slip）: '{entry.FullName}'");

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            return targetDir;
        }

        /// <summary>判断本地 mods 目录中是否已安装某 Mod（含 manifest.json 即视为已安装）。</summary>
        public static bool IsInstalled(ModId id, string localModsDir)
        {
            var manifestPath = Path.Combine(localModsDir, id.Value, "manifest.json");
            return File.Exists(manifestPath);
        }

        /// <summary>读取本地已安装 Mod 的版本（未安装返回 null）。</summary>
        public static ModVersion? GetInstalledVersion(ModId id, string localModsDir)
        {
            var manifestPath = Path.Combine(localModsDir, id.Value, "manifest.json");
            if (!File.Exists(manifestPath)) return null;
            try { return ModManifest.Parse(File.ReadAllText(manifestPath)).Version; }
            catch { return null; }
        }

        /// <summary>清空安装目录（重新安装前调用，避免旧文件残留）。</summary>
        public static void Wipe(ModId id, string localModsDir)
        {
            var dir = Path.Combine(localModsDir, id.Value);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
