using System;
using System.IO;
using System.IO.Compression;
using Game.Mod.Contract;

namespace ModServer.Registry;

/// <summary>读取 .mod 包（zip）中的 manifest。</summary>
public static class ModPackageReader
{
    public const string ManifestEntry = "manifest.json";

    public static ModManifest ReadManifest(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry(ManifestEntry)
            ?? throw new Exception("包中缺少 manifest.json");
        using var reader = new StreamReader(entry.Open());
        return ModManifest.Parse(reader.ReadToEnd());
    }
}
