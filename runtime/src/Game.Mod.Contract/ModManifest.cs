using System;
using System.Collections.Generic;
using Game.Mod.Contract.Json;

namespace Game.Mod.Contract
{
    /// <summary>
    /// Mod 清单 v2（§12.2 / §15.4）：一个 Mod = 一份 Manifest = Shared/Client/Server 模块 + 依赖 + 协议声明。
    /// 解析兼容 v1 字段（modId / entryDll / dependencies 对象形式），序列化一律输出 v2。
    /// </summary>
    public sealed class ModManifest
    {
        /// <summary>Mod 唯一标识（反向域名），发布后永不改变。</summary>
        public ModId ModId { get; set; }

        public ModVersion Version { get; set; }

        /// <summary>IMod 入口类型全名；空 = 扫描程序集中第一个 IMod 实现。</summary>
        public string Entry { get; set; } = "";

        /// <summary>Shared 模块程序集（双端加载），v2 必填。</summary>
        public string SharedModule { get; set; } = "";

        /// <summary>Client 模块程序集（仅 HasClient 环境加载），可空。</summary>
        public string? ClientModule { get; set; }

        /// <summary>Server 模块程序集（仅 HasServer 环境加载），可空。</summary>
        public string? ServerModule { get; set; }

        public List<ModDependency> Dependencies { get; set; } = new();

        /// <summary>协议声明清单（供协商与查重，§15.4）。</summary>
        public List<ProtocolDecl> Protocols { get; set; } = new();

        public List<FileEntry> Files { get; set; } = new();

        /// <summary>根据依赖解析出的版本实例标识。</summary>
        public string InstanceId => $"{ModId.Value}@{Version}";

        /// <summary>按运行环境取应加载的模块程序集（§15.4 环境裁剪）。</summary>
        public IEnumerable<string> ModulesFor(bool hasClient, bool hasServer)
        {
            if (!string.IsNullOrEmpty(SharedModule)) yield return SharedModule;
            if (hasClient && !string.IsNullOrEmpty(ClientModule)) yield return ClientModule!;
            if (hasServer && !string.IsNullOrEmpty(ServerModule)) yield return ServerModule!;
        }

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
                    case "id":
                    case "modId": // v1 兼容
                        m.ModId = new ModId(kv.Value.AsString);
                        break;
                    case "version":
                        m.Version = ModVersion.Parse(kv.Value.AsString);
                        break;
                    case "entry":
                        m.Entry = kv.Value.AsString;
                        break;
                    case "entryDll": // v1 兼容 → shared 模块
                        m.SharedModule = kv.Value.AsString;
                        break;
                    case "modules":
                        foreach (var mod in kv.Value.AsObject)
                        {
                            switch (mod.Key)
                            {
                                case "shared": m.SharedModule = mod.Value.AsString; break;
                                case "client": m.ClientModule = mod.Value.AsString; break;
                                case "server": m.ServerModule = mod.Value.AsString; break;
                            }
                        }
                        break;
                    case "dependencies":
                        if (kv.Value.Kind == JsonKind.Object)
                        {
                            // v1 兼容：{ "com.xx.yy": ">=1.0.0" }
                            foreach (var d in kv.Value.AsObject)
                                m.Dependencies.Add(new ModDependency
                                {
                                    Id = new ModId(d.Key),
                                    Version = VersionRange.Parse(d.Value.AsString),
                                });
                        }
                        else
                        {
                            foreach (var d in kv.Value.AsArray)
                                m.Dependencies.Add(ParseDependency(d));
                        }
                        break;
                    case "network":
                        foreach (var n in kv.Value.AsObject)
                        {
                            if (n.Key == "protocols")
                                foreach (var p in n.Value.AsArray)
                                    m.Protocols.Add(ParseProtocol(p));
                        }
                        break;
                    case "files":
                        foreach (var f in kv.Value.AsArray)
                            m.Files.Add(ParseFile(f));
                        break;
                }
            }

            if (string.IsNullOrEmpty(m.ModId.Value))
                throw new FormatException("manifest 缺少 id");
            if (string.IsNullOrEmpty(m.SharedModule))
                throw new FormatException("manifest 缺少 modules.shared");
            return m;
        }

        public string ToJson()
        {
            var root = new Dictionary<string, JsonValue>
            {
                ["id"] = JsonValue.Of(ModId.Value),
                ["version"] = JsonValue.Of(Version.ToString()),
            };

            if (!string.IsNullOrEmpty(Entry))
                root["entry"] = JsonValue.Of(Entry);

            var modules = new Dictionary<string, JsonValue> { ["shared"] = JsonValue.Of(SharedModule) };
            if (ClientModule is { } cm) modules["client"] = JsonValue.Of(cm);
            if (ServerModule is { } sm) modules["server"] = JsonValue.Of(sm);
            root["modules"] = JsonValue.NewObject(modules);

            if (Dependencies.Count > 0)
            {
                var deps = new List<JsonValue>();
                foreach (var d in Dependencies)
                {
                    var obj = new Dictionary<string, JsonValue>
                    {
                        ["id"] = JsonValue.Of(d.Id.Value),
                        ["version"] = JsonValue.Of(d.Version.ToString()),
                    };
                    if (d.Optional) obj["optional"] = JsonValue.Of(true);
                    if (d.Scope != ModScope.Shared) obj["scope"] = JsonValue.Of(d.Scope.ToString().ToLowerInvariant());
                    if (d.Features.Count > 0)
                    {
                        var feats = new List<JsonValue>();
                        foreach (var f in d.Features) feats.Add(JsonValue.Of(f));
                        obj["features"] = JsonValue.NewArray(feats);
                    }
                    deps.Add(JsonValue.NewObject(obj));
                }
                root["dependencies"] = JsonValue.NewArray(deps);
            }

            if (Protocols.Count > 0)
            {
                var protos = new List<JsonValue>();
                foreach (var p in Protocols)
                {
                    protos.Add(JsonValue.NewObject(new Dictionary<string, JsonValue>
                    {
                        ["id"] = JsonValue.Of(p.Path),
                        ["version"] = JsonValue.Of((long)p.Version),
                        ["direction"] = JsonValue.Of(p.Direction.ToString()),
                        ["delivery"] = JsonValue.Of(p.Delivery.ToString()),
                        ["frequency"] = JsonValue.Of(p.Frequency.ToString()),
                    }));
                }
                root["network"] = JsonValue.NewObject(new Dictionary<string, JsonValue>
                {
                    ["protocols"] = JsonValue.NewArray(protos),
                });
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

        private static ModDependency ParseDependency(JsonValue v)
        {
            var d = new ModDependency();
            foreach (var kv in v.AsObject)
            {
                switch (kv.Key)
                {
                    case "id": d.Id = new ModId(kv.Value.AsString); break;
                    case "version": d.Version = VersionRange.Parse(kv.Value.AsString); break;
                    case "optional": d.Optional = kv.Value.AsBool; break;
                    case "required": d.Optional = !kv.Value.AsBool; break;
                    case "scope":
                        d.Scope = kv.Value.AsString.ToLowerInvariant() switch
                        {
                            "shared" => ModScope.Shared,
                            "client" => ModScope.Client,
                            "server" => ModScope.Server,
                            var s => throw new FormatException($"未知依赖 scope: '{s}'"),
                        };
                        break;
                    case "features":
                        foreach (var f in kv.Value.AsArray) d.Features.Add(f.AsString);
                        break;
                }
            }
            if (string.IsNullOrEmpty(d.Id.Value))
                throw new FormatException("dependency 缺少 id");
            return d;
        }

        private static ProtocolDecl ParseProtocol(JsonValue v)
        {
            var p = new ProtocolDecl();
            foreach (var kv in v.AsObject)
            {
                switch (kv.Key)
                {
                    case "id": p.Path = kv.Value.AsString; break;
                    case "version": p.Version = (ushort)kv.Value.AsLong; break;
                    case "direction":
                        p.Direction = kv.Value.AsString switch
                        {
                            "ClientToServer" => NetworkDirection.ClientToServer,
                            "ServerToClient" => NetworkDirection.ServerToClient,
                            var s => throw new FormatException($"未知协议方向: '{s}'"),
                        };
                        break;
                    case "delivery":
                        p.Delivery = kv.Value.AsString switch
                        {
                            "Reliable" => Delivery.Reliable,
                            "Unreliable" => Delivery.Unreliable,
                            "Sequenced" => Delivery.Sequenced,
                            _ => Delivery.Reliable,
                        };
                        break;
                    case "frequency":
                        p.Frequency = kv.Value.AsString switch
                        {
                            "High" => Frequency.High,
                            _ => Frequency.Low,
                        };
                        break;
                }
            }
            if (string.IsNullOrEmpty(p.Path))
                throw new FormatException("protocol 缺少 id");
            return p;
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
    }
}
