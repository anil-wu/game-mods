using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Game.Mod.Contract;
using Game.Mod.Contract.Json;

namespace Com.Game.ModStore
{
    /// <summary>服务器上的 Mod 条目（GET /api/mods）。</summary>
    public readonly struct RemoteModInfo
    {
        public readonly ModId Id;
        public readonly ModVersion Version;

        public RemoteModInfo(ModId id, ModVersion version)
        {
            Id = id;
            Version = version;
        }

        public override string ToString() => $"{Id.Value}@{Version}";
    }

    /// <summary>
    /// mod_server HTTP API 客户端（契约见 CONTRACT.md）。
    /// 仅用 HttpClient + 零依赖 JSON，netstandard2.1 / Unity Mono 可用。
    /// </summary>
    public sealed class ModServerApi : IDisposable
    {
        private readonly HttpClient _http;

        public string BaseUrl { get; set; }

        public ModServerApi(string baseUrl, TimeSpan? timeout = null)
        {
            BaseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        }

        /// <summary>GET /api/mods → 服务器全部 Mod。</summary>
        public async Task<RemoteModInfo[]> ListMods()
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/api/mods").ConfigureAwait(false);
            var root = JsonValue.Parse(json);
            var list = new List<RemoteModInfo>();
            foreach (var item in root.AsArray)
            {
                string id = "", version = "";
                foreach (var kv in item.AsObject)
                {
                    if (kv.Key == "modId") id = kv.Value.AsString;
                    else if (kv.Key == "version") version = kv.Value.AsString;
                }
                if (id.Length > 0 && ModVersion.TryParse(version, out var v))
                    list.Add(new RemoteModInfo(new ModId(id), v));
            }
            return list.ToArray();
        }

        /// <summary>POST /api/resolve → 依赖闭包（含根 Mod）。</summary>
        public async Task<RemoteModInfo[]> ResolveClosure(ModId id, ModVersion version)
        {
            var body = $"{{\"modId\":\"{id.Value}\",\"version\":\"{version}\"}}";
            var response = await _http.PostAsync(
                $"{BaseUrl}/api/resolve",
                new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var list = new List<RemoteModInfo>();
            foreach (var kv in JsonValue.Parse(json).AsObject)
            {
                if (kv.Key != "mods") continue;
                foreach (var item in kv.Value.AsArray)
                {
                    string mid = "", ver = "";
                    foreach (var m in item.AsObject)
                    {
                        if (m.Key == "modId") mid = m.Value.AsString;
                        else if (m.Key == "version") ver = m.Value.AsString;
                    }
                    if (mid.Length > 0 && ModVersion.TryParse(ver, out var v))
                        list.Add(new RemoteModInfo(new ModId(mid), v));
                }
            }
            return list.ToArray();
        }

        /// <summary>GET /api/mods/{modId}/{version}/download → .mod 包（zip）字节。</summary>
        public async Task<byte[]> DownloadPackage(ModId id, ModVersion version)
        {
            var bytes = await _http.GetByteArrayAsync(
                $"{BaseUrl}/api/mods/{id.Value}/{version}/download").ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
                throw new InvalidOperationException($"下载包为空: {id.Value}@{version}");
            return bytes;
        }

        public void Dispose() => _http.Dispose();
    }
}
