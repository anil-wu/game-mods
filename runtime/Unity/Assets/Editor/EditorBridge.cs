using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Game.Runtime.Editor
{
    /// <summary>
    /// EditorBridge：编辑器内 HTTP 命令桥（自实现 MCP-lite），供外部（curl/agent）实时驱动编辑器。
    /// 端点：/health /console /screenshot /bounds /shaders /quit
    /// 启动：Unity -batchmode -executeMethod Game.Runtime.Editor.EditorBridge.Run（不带 -quit，保持存活）
    /// Unity API 调用经 EditorApplication.update 主线程调度（请求队列 + ManualResetEvent）。
    /// </summary>
    [InitializeOnLoad]
    public static class EditorBridge
    {
        private const int Port = 8123;
        private static readonly List<string> Logs = new();
        private static HttpListener? _listener;
        private static Thread? _thread;
        private static readonly Queue<Request> _queue = new();

        private sealed class Request
        {
            public int Id;
            public string Cmd = "";
            public string Query = "";
            public string Result = "";
            public readonly ManualResetEventSlim Done = new(false);
        }

        static EditorBridge()
        {
            Application.logMessageReceived += (condition, _, type) =>
            {
                lock (Logs) { Logs.Add($"[{type}] {condition}"); if (Logs.Count > 2000) Logs.RemoveAt(0); }
            };
            EditorApplication.update += DrainQueue;
        }

        [MenuItem("Mod/启动 EditorBridge")]
        public static void Run() => Start();

        private static void Start()
        {
            if (_listener is not null) return;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
            Debug.Log($"[EditorBridge] 监听 http://127.0.0.1:{Port}");
        }

        private static void Loop()
        {
            while (_listener is { IsListening: true })
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; }
                try { Handle(ctx); }
                catch (Exception e) { Debug.LogError($"[EditorBridge] {e}"); }
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            var url = ctx.Request.Url!;
            var path = url.AbsolutePath;
            var query = url.Query;

            if (path == "/quit")
            {
                Write(ctx, 200, "bye");
                EditorApplication.delayCall += () => EditorApplication.Exit(0);
                return;
            }
            if (path == "/health")
            {
                Write(ctx, 200, "ok");
                return;
            }
            if (path == "/console")
            {
                lock (Logs) Write(ctx, 200, string.Join("\n", Logs));
                return;
            }

            // 需要主线程 Unity API 的命令 → 入队等待
            var req = new Request { Id = Environment.TickCount, Cmd = path, Query = query };
            lock (_queue) _queue.Enqueue(req);
            if (!req.Done.Wait(TimeSpan.FromSeconds(60)))
                Write(ctx, 500, "timeout");
            else
                Write(ctx, 200, req.Result);
        }

        private static void DrainQueue()
        {
            Request? req = null;
            lock (_queue) { if (_queue.Count > 0) req = _queue.Dequeue(); }
            if (req is null) return;
            try
            {
                req.Result = req.Cmd switch
                {
                    "/screenshot" => DoScreenshot(),
                    "/bounds" => DoBounds(req.Query),
                    "/shaders" => DoShaders(req.Query),
                    _ => "unknown cmd",
                };
            }
            catch (Exception e)
            {
                req.Result = $"error: {e.Message}";
            }
            req.Done.Set();
        }

        // ---- 命令实现（主线程） ----

        private static string DoScreenshot()
        {
            var cam = Camera.main;
            if (cam is null) cam = CreateCamera();
            var rt = new RenderTexture(800, 600, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(800, 600, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 800, 600), 0, 0);
            tex.Apply();
            var colors = tex.GetPixels();
            int magenta = 0, nonBg = 0;
            foreach (var c in colors)
            {
                if (c.r > 0.6f && c.b > 0.6f && c.g < 0.3f) magenta++;
                if (c.r > 0.25f || c.g > 0.25f || c.b > 0.25f) nonBg++;
            }
            return $"magenta={100f * magenta / colors.Length:F1}% nonBg={100f * nonBg / colors.Length:F1}%";
        }

        private static string DoBounds(string query)
        {
            var (bundle, asset) = ParseAsset(query);
            var prefab = LoadPrefab(bundle, asset);
            if (prefab is null) return "prefab null";
            var go = UnityEngine.Object.Instantiate(prefab);
            var bounds = new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in go.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);
            UnityEngine.Object.DestroyImmediate(go);
            return $"size=({bounds.size.x:F2},{bounds.size.y:F2},{bounds.size.z:F2})";
        }

        private static string DoShaders(string query)
        {
            var (bundle, asset) = ParseAsset(query);
            var prefab = LoadPrefab(bundle, asset);
            if (prefab is null) return "prefab null";
            var go = UnityEngine.Object.Instantiate(prefab);
            var sb = new System.Text.StringBuilder();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var m = r.sharedMaterial;
                sb.AppendLine($"{r.name}: {(m is null ? "null" : m.name)} / {(m is null ? "null" : m.shader?.name)}");
            }
            UnityEngine.Object.DestroyImmediate(go);
            return sb.ToString();
        }

        private static GameObject? LoadPrefab(string bundlePath, string assetName)
        {
            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle is null) return null;
            var go = bundle.LoadAsset<GameObject>(assetName);
            bundle.Unload(false);
            return go;
        }

        private static (string bundle, string asset) ParseAsset(string query)
        {
            var q = query.TrimStart('?');
            var dict = new Dictionary<string, string>();
            foreach (var kv in q.Split('&'))
            {
                var p = kv.Split('=');
                if (p.Length == 2) dict[Uri.UnescapeDataString(p[0])] = Uri.UnescapeDataString(p[1]);
            }
            dict.TryGetValue("bundle", out var b);
            dict.TryGetValue("asset", out var a);
            return (b ?? "", a ?? "");
        }

        private static Camera CreateCamera()
        {
            var go = new GameObject("BridgeCam");
            go.tag = "MainCamera";
            go.AddComponent<AudioListener>();
            return go.AddComponent<Camera>();
        }

        private static void Write(HttpListenerContext ctx, int code, string body)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
    }
}
