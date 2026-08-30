using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Mod.Contract;

namespace ModServer.Registry;

/// <summary>
/// Mod 注册表：内存索引 + 磁盘包存储。
/// 磁盘布局: {packagesDir}/{modId}/{version}.mod + {version}.manifest.json
/// </summary>
public sealed class ModRegistry
{
    public sealed class ModRecord
    {
        public ModManifest Manifest { get; }
        public string PackagePath { get; }

        public ModRecord(ModManifest manifest, string packagePath)
        {
            Manifest = manifest;
            PackagePath = packagePath;
        }
    }

    private readonly string _packagesDir;
    private readonly object _lock = new();
    private readonly Dictionary<ModId, SortedDictionary<ModVersion, ModRecord>> _index = new();

    public ModRegistry(string packagesDir)
    {
        _packagesDir = packagesDir;
        Directory.CreateDirectory(packagesDir);
        LoadFromDisk();
    }

    public int Count
    {
        get { lock (_lock) return _index.Sum(kv => kv.Value.Count); }
    }

    public void Add(ModManifest manifest, byte[] packageBytes)
    {
        var dir = Path.Combine(_packagesDir, manifest.ModId.Value);
        Directory.CreateDirectory(dir);
        var versionStr = manifest.Version.ToString();
        var pkgPath = Path.Combine(dir, versionStr + ".mod");
        File.WriteAllBytes(pkgPath, packageBytes);
        File.WriteAllText(Path.Combine(dir, versionStr + ".manifest.json"), manifest.ToJson());

        lock (_lock)
        {
            if (!_index.TryGetValue(manifest.ModId, out var versions))
            {
                versions = new SortedDictionary<ModVersion, ModRecord>();
                _index[manifest.ModId] = versions;
            }
            versions[manifest.Version] = new ModRecord(manifest, pkgPath);
        }
    }

    /// <summary>列出全部 Mod，每 modId 只取最新版本（商店列表不呈现历史版本）。</summary>
    public List<ModManifest> ListAll()
    {
        lock (_lock)
        {
            // SortedDictionary 按键（版本）升序 → Values.Last() = 每 Mod 的最高版本
            return _index.Values.Select(v => v.Values.Last().Manifest).ToList();
        }
    }

    public List<ModManifest> ListVersions(ModId id)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(id, out var versions)) return new List<ModManifest>();
            return versions.Values.Select(r => r.Manifest).ToList();
        }
    }

    public ModRecord? Get(ModId id, ModVersion version)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(id, out var versions)) return null;
            return versions.TryGetValue(version, out var rec) ? rec : null;
        }
    }

    public ModRecord? GetLatest(ModId id)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(id, out var versions) || versions.Count == 0) return null;
            return versions.Values.Last();
        }
    }

    /// <summary>解析依赖闭包（BFS），返回包含根 Mod 在内的完整记录集合。</summary>
    public List<ModRecord> Resolve(ModId id, ModVersion version)
    {
        var result = new List<ModRecord>();
        var visited = new HashSet<string>();
        var queue = new Queue<(ModId Id, ModVersion Ver)>();
        queue.Enqueue((id, version));

        while (queue.Count > 0)
        {
            var (mid, ver) = queue.Dequeue();
            var key = $"{mid.Value}@{ver}";
            if (!visited.Add(key)) continue;

            var rec = Get(mid, ver) ?? throw new Exception($"找不到 {key}");
            result.Add(rec);

            foreach (var dep in rec.Manifest.Dependencies)
            {
                if (dep.Optional) continue; // 可选依赖不进闭包（§12.4）
                var depVer = FindSatisfying(dep.Id, dep.Version)
                    ?? throw new Exception($"依赖 '{dep.Id}' 无满足 {dep.Version} 的版本");
                queue.Enqueue((dep.Id, depVer));
            }
        }
        return result;
    }

    private ModVersion? FindSatisfying(ModId id, VersionRange range)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(id, out var versions)) return null;
            foreach (var kv in versions.Reverse()) // 新版本优先
                if (range.SatisfiedBy(kv.Key)) return kv.Key;
        }
        return null;
    }

    private void LoadFromDisk()
    {
        foreach (var manifestFile in Directory.EnumerateFiles(_packagesDir, "*.manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = ModManifest.Parse(File.ReadAllText(manifestFile));
                var pkgPath = Path.Combine(Path.GetDirectoryName(manifestFile)!, manifest.Version + ".mod");
                if (!File.Exists(pkgPath)) continue;

                if (!_index.TryGetValue(manifest.ModId, out var versions))
                {
                    versions = new SortedDictionary<ModVersion, ModRecord>();
                    _index[manifest.ModId] = versions;
                }
                versions[manifest.Version] = new ModRecord(manifest, pkgPath);
            }
            catch (Exception)
            {
                // 跳过损坏的 manifest
            }
        }
    }
}
