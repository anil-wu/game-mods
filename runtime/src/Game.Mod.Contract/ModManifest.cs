using System;
using System.Collections.Generic;
using Game.Mod.Contract.Json;

namespace Game.Mod.Contract
{
    /// <summary>
    /// Mod 清单。源工程与包共用同一 schema，区别仅在于构建工具补全 files。
    /// 序列化使用零依赖内置 JSON（netstandard2.1 兼容，供 Unity 2022.3 使用）。
    /// </summary>
    public sealed class ModManifest
    {
        public ModId ModId { get; set; }

        public ModVersion Version { get; set; }

        public string EntryDll { get; set; } = "";

        public Dictionary<ModId, VersionRange> Dependencies { get; set; } = new();

        public List<FileEntry> Files { get; set; } = new();

        public static ModManifest Parse(string json)
        {
            var root = JsonValue.Parse(json);
            if (root.Kind != JsonKind.Object)
                throw new FormatException("manifest 应为 JSON 对象");

            var m = new ModManifest();
            foreach (var kv in root.AsObject)
            {
                switch (kv.Key)
                {
                    case "modId":
                        m.ModId = new ModId(kv.Value.AsString);
                        break;
                    case "version":
                        m.Version = ModVersion.Parse(kv.Value.AsString);
                        break;
                    case "entryDll":
                        m.EntryDll = kv.Value.AsString;
                        break;
                    case "dependencies":
                        foreach (var d in kv.Value.AsObject)
                            m.Dependencies[new ModId(d.Key)] = VersionRange.Parse(d.Value.AsString);
                        break;
                    case "files":
                        foreach (var f in kv.Value.AsArray)
                            m.Files.Add(ParseFile(f));
                        break;
                }
            }

            if (string.IsNullOrEmpty(m.ModId.Value))
                throw new FormatException("manifest 缺少 modId");
            if (string.IsNullOrEmpty(m.EntryDll))
                throw new FormatException("manifest 缺少 entryDll");
            return m;
        }

        public string ToJson()
        {
            var root = new Dictionary<string, JsonValue>
            {
                ["modId"] = JsonValue.Of(ModId.Value),
                ["version"] = JsonValue.Of(Version.ToString()),
                ["entryDll"] = JsonValue.Of(EntryDll),
            };

            if (Dependencies.Count > 0)
            {
                var deps = new Dictionary<string, JsonValue>();
                foreach (var kv in Dependencies)
                    deps[kv.Key.Value] = JsonValue.Of(kv.Value.ToString());
                root["dependencies"] = JsonValue.NewObject(deps);
            }

            if (Files.Count > 0)
            {
                var files = new List<JsonValue>();
                foreach (var f in Files)
                {
                    files.Add(JsonValue.NewObject(new Dictionary<string, JsonValue>
                    {
                        ["path"] = JsonValue.Of(f.Path),
                        ["type"] = JsonValue.Of(FileTypeName(f.Type)),
                        ["size"] = JsonValue.Of(f.Size),
                        ["sha256"] = JsonValue.Of(f.Sha256),
                    }));
                }
                root["files"] = JsonValue.NewArray(files);
            }

            return JsonValue.NewObject(root).ToJsonString();
        }

        private static FileEntry ParseFile(JsonValue v)
        {
            var e = new FileEntry();
            foreach (var kv in v.AsObject)
            {
                switch (kv.Key)
                {
                    case "path": e.Path = kv.Value.AsString; break;
                    case "type": e.Type = ParseFileType(kv.Value.AsString); break;
                    case "size": e.Size = kv.Value.AsLong; break;
                    case "sha256": e.Sha256 = kv.Value.AsString; break;
                }
            }
            return e;
        }

        private static FileType ParseFileType(string s) => s switch
        {
            "code" => FileType.Code,
            "data" => FileType.Data,
            "asset" => FileType.Asset,
            _ => throw new FormatException($"未知文件类型: '{s}'"),
        };

        private static string FileTypeName(FileType t) => t switch
        {
            FileType.Code => "code",
            FileType.Data => "data",
            FileType.Asset => "asset",
            _ => "code",
        };

        /// <summary>根据依赖解析出的版本实例标识。</summary>
        public string InstanceId => $"{ModId.Value}@{Version}";
    }

}
