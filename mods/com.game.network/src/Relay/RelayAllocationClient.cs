using System;
using System.Net.Http;

namespace Com.Game.Network
{
    /// <summary>控制面分配结果（管道分隔文本解析产物，§6.3）。</summary>
    public sealed class RelayAllocationResult
    {
        public string JoinCode = "";
        public ulong RoomId;
        public string RelayHost = "";
        public int RelayPort;
        public byte[] Token = Array.Empty<byte>();
        public long ExpiryMs;
    }

    /// <summary>
    /// Relay 控制面客户端（§6.3）：POST /allocate（Host 建房间 → joinCode + token）、
    /// POST /resolve（Client 凭房间码 → token）、GET /health。
    ///
    /// 响应格式 = **管道分隔文本**（relay_server §6.4 已同步调整，JSON 仅保留在 /health）：
    ///     joinCode|roomId|relayEndpoint(host:port)|tokenB64|expiryMs
    /// 客户端零依赖解析（netstandard2.1 无 System.Text.Json），人类可读。
    /// 测试不依赖真实公网：可直接注入 AllocationService 产物构造 Config（RelayTests），
    /// 本类 HTTP 路径由假控制面（TcpListener 伪造 HTTP）验证。
    /// </summary>
    public sealed class RelayAllocationClient
    {
        private readonly HttpClient _http = new();

        public string BaseUrl { get; }

        public RelayAllocationClient(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl 不能为空", nameof(baseUrl));
            BaseUrl = baseUrl.TrimEnd('/');
        }

        /// <summary>POST /allocate：Host 建房间，返回 joinCode + token。</summary>
        public RelayAllocationResult Allocate()
        {
            var body = Post("/allocate", null) ?? "";
            return Parse(body);
        }

        /// <summary>POST /resolve：Client 凭房间码获取 token。返回 null = 房间码无效。</summary>
        public RelayAllocationResult? TryResolve(string joinCode)
        {
            var code = (joinCode ?? "").Trim().ToUpperInvariant();
            if (code.Length == 0) return null;
            // 服务端 record ResolveRequest(string JoinCode) JSON 绑定（§6.4；请求体保持 JSON，仅响应改文本）
            var json = "{\"joinCode\":\"" + code + "\"}";
            var body = Post("/resolve", json);
            if (body is null) return null;
            return Parse(body);
        }

        /// <summary>GET /health：节点/服务健康检查。返回状态文本（失败 null）。</summary>
        public string? Health()
        {
            try
            {
                using var resp = _http.GetAsync(BaseUrl + "/health").GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return null;
                return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 管道分隔文本解析：joinCode|roomId|relayHost:port|tokenB64|expiryMs。
        /// 独立静态方法便于单测（不依赖 HTTP）。
        /// </summary>
        public static RelayAllocationResult Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException("控制面响应为空");
            var parts = text.Split('|');
            if (parts.Length < 5)
                throw new FormatException($"控制面响应格式错误（期望 5 段管道分隔）: {text}");

            var result = new RelayAllocationResult
            {
                JoinCode = parts[0].Trim(),
            };
            if (!ulong.TryParse(parts[1].Trim(), out result.RoomId))
                throw new FormatException($"roomId 解析失败: {parts[1]}");
            if (!long.TryParse(parts[4].Trim(), out result.ExpiryMs))
                throw new FormatException($"expiryMs 解析失败: {parts[4]}");

            var endpoint = parts[2].Trim();
            var idx = endpoint.LastIndexOf(':');
            if (idx < 0 || idx == endpoint.Length - 1)
                throw new FormatException($"relay endpoint 格式错误（期望 host:port）: {endpoint}");
            result.RelayHost = endpoint.Substring(0, idx);
            if (!int.TryParse(endpoint.Substring(idx + 1), out result.RelayPort))
                throw new FormatException($"relay port 解析失败: {endpoint}");

            try { result.Token = Convert.FromBase64String(parts[3].Trim()); }
            catch (FormatException) { throw new FormatException($"token base64 解析失败: {parts[3]}"); }
            if (result.Token.Length != RelayWire.TokenSize)
                throw new FormatException($"token 长度 {result.Token.Length} ≠ {RelayWire.TokenSize}");

            return result;
        }

        /// <summary>POST 并返回响应体；非 2xx 或网络失败返回 null（/resolve 的 404 走这里）。</summary>
        private string? Post(string path, string? jsonBody)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
                if (jsonBody is not null) req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
                using var resp = _http.SendAsync(req).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return null;
                return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
