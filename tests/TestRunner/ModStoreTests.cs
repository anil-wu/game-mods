using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Com.Game.ModStore;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// Mod 商店端到端：假 mod_server（TcpListener 最小 HTTP 实现，免权限）→
    /// 列表呈现 → 闭包安装到本地 mods 目录 → ModManager 启动。
    /// </summary>
    public static class ModStoreTests
    {
        // ---- 最小 HTTP 假服务器（仅实现 mod_server 的三个端点） ----

        private sealed class FakeModServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _thread;
            private volatile bool _running;

            public string BaseUrl { get; }
            public string ListJson = "[]";
            public string ResolveJson = "{\"mods\":[]}";
            public byte[]? PackageBytes;

            public FakeModServer()
            {
                // 取空闲端口
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();

                BaseUrl = $"http://127.0.0.1:{port}";
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true };
                _thread.Start();
            }

            private void Loop()
            {
                while (_running)
                {
                    TcpClient? client = null;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                        Handle(client);
                    }
                    catch { if (!_running) return; }
                    finally { client?.Close(); }
                }
            }

            private void Handle(TcpClient client)
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var requestLine = reader.ReadLine();
                if (requestLine is null) return;
                var parts = requestLine.Split(' ');
                var method = parts[0];
                var path = parts[1];

                var contentLength = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.Substring(15).Trim());
                }
                if (contentLength > 0)
                {
                    var buf = new char[contentLength];
                    reader.Read(buf, 0, contentLength); // 请求体（本用例无需解析）
                }

                byte[] body;
                var contentType = "application/json";
                if (method == "GET" && path == "/api/mods")
                {
                    body = Encoding.UTF8.GetBytes(ListJson);
                }
                else if (method == "POST" && path == "/api/resolve")
                {
                    body = Encoding.UTF8.GetBytes(ResolveJson);
                }
                else if (method == "GET" && path!.EndsWith("/download", StringComparison.Ordinal))
                {
                    body = PackageBytes ?? Array.Empty<byte>();
                    contentType = "application/octet-stream";
                }
                else
                {
                    Write(stream, 404, Encoding.UTF8.GetBytes("{\"error\":\"not found\"}"), "application/json");
                    return;
                }
                Write(stream, 200, body, contentType);
            }

            private static void Write(NetworkStream stream, int status, byte[] body, string contentType)
            {
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                    $"Content-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                stream.Write(header, 0, header.Length);
                stream.Write(body, 0, body.Length);
            }

            public void Dispose()
            {
                _running = false;
                _listener.Stop();
            }
        }

        // ---- 辅助 ----

        private static readonly ModId PluginId = new("com.test.plugin");

        /// <summary>用合成插件 DLL（TestPlugin 工程产物）制作 .mod 包（zip）。</summary>
        private static byte[] BuildPluginPackage()
        {
            var dllPath = Path.Combine(RepoPaths.Root, "tests", "TestPlugin", "bin", "Release", "netstandard2.1", "com.test.plugin.dll");
            Assert.True(File.Exists(dllPath), $"插件 DLL 不存在: {dllPath}");

            var manifest = new ModManifest
            {
                ModId = PluginId,
                Version = new ModVersion(1, 0, 0),
                Entry = "Com.Test.Plugin.PluginMod",
                SharedModule = "com.test.plugin.dll",
            };
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifestEntry = zip.CreateEntry("manifest.json");
                using (var w = new StreamWriter(manifestEntry.Open()))
                    w.Write(manifest.ToJson());
                var dllEntry = zip.CreateEntry("com.test.plugin.dll");
                using (var w = dllEntry.Open())
                {
                    var bytes = File.ReadAllBytes(dllPath);
                    w.Write(bytes, 0, bytes.Length);
                }
            }
            return ms.ToArray();
        }

        private static (ModRuntimeHost host, FakeModServer server, string modsDir, string tempRoot) Setup()
        {
            var server = new FakeModServer();
            var tempRoot = Path.Combine(Path.GetTempPath(), "modstore_test_" + Guid.NewGuid().ToString("N"));
            var modsDir = Path.Combine(tempRoot, "mods");
            Directory.CreateDirectory(modsDir);

            server.PackageBytes = BuildPluginPackage();
            server.ListJson = "[{\"modId\":\"com.test.plugin\",\"version\":\"1.0.0\",\"dependencies\":{}}]";
            server.ResolveJson = "{\"mods\":[{\"modId\":\"com.test.plugin\",\"version\":\"1.0.0\"}]}";

            var host = new ModRuntimeHost(RuntimeRole.Host);
            // 商店 Mod 加载（入口经真实 mod.json 解析）
            host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.modstore")), typeof(ModStoreMod).Assembly);
            ModStoreMod.Service.ServerBaseUrl = server.BaseUrl;
            ModStoreMod.Service.LocalModsDir = modsDir;
            return (host, server, modsDir, tempRoot);
        }

        // ---- 测试 ----

        [Test]
        public static void ListRemoteMods_PresentsServerMods()
        {
            var (host, server, _, tempRoot) = Setup();
            try
            {
                var mods = ModStoreMod.Service.ListRemoteMods().GetAwaiter().GetResult();
                Assert.Equal(1, mods.Length);
                Assert.Equal("com.test.plugin", mods[0].Id.Value);
                Assert.Equal("1.0.0", mods[0].Version.ToString());
            }
            finally { server.Dispose(); host.Manager.Unload(new ModId("com.game.modstore")); Directory.Delete(tempRoot, true); }
        }

        [Test]
        public static void InstallAndStart_DownloadsToLocalModsDir_AndStarts()
        {
            var (host, server, modsDir, tempRoot) = Setup();
            try
            {
                Assert.True(!host.Manager.IsLoaded(PluginId));

                // 一键：闭包安装 → 启动（核心路径：呈现 → 下载到本地 mods 目录 → 启动）
                var mod = ModStoreMod.Service
                    .InstallAndStart(PluginId, new ModVersion(1, 0, 0))
                    .GetAwaiter().GetResult();

                // 安装到本地 mods 目录
                Assert.True(File.Exists(Path.Combine(modsDir, "com.test.plugin", "manifest.json")));
                Assert.True(File.Exists(Path.Combine(modsDir, "com.test.plugin", "com.test.plugin.dll")));
                // 已启动：Register 生效（组件/系统已注册）
                Assert.True(host.Manager.IsLoaded(PluginId));
                Assert.Equal(ModObjectState.Running, mod.State);
                Assert.Equal(1, host.Systems.CountOf(PluginId));
                // 动态加载的是插件程序集自身的入口（而非测试进程内类型）
                Assert.Equal("Com.Test.Plugin.PluginMod", mod.Instance.GetType().FullName);
                Assert.Equal("com.test.plugin", mod.Assemblies[0].GetName().Name);
            }
            finally { server.Dispose(); Directory.Delete(tempRoot, true); }
        }

        [Test]
        public static void Install_Idempotent_WhenAlreadyInstalled()
        {
            var (host, server, modsDir, tempRoot) = Setup();
            try
            {
                ModStoreMod.Service.InstallWithClosure(PluginId, new ModVersion(1, 0, 0)).GetAwaiter().GetResult();
                // 第二次：已安装 → 跳过下载仍可用
                var again = ModStoreMod.Service.InstallWithClosure(PluginId, new ModVersion(1, 0, 0)).GetAwaiter().GetResult();
                Assert.Equal(0, again.Length); // 无新安装
                var mod = ModStoreMod.Service.StartInstalled(PluginId);
                Assert.Equal(ModObjectState.Running, mod.State);
            }
            finally { server.Dispose(); Directory.Delete(tempRoot, true); }
        }

        [Test]
        public static void Installer_ZipSlip_Rejected()
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var e1 = zip.CreateEntry("manifest.json");
                using (var w = new StreamWriter(e1.Open()))
                    w.Write("{\"id\":\"com.evil.mod\",\"version\":\"1.0.0\",\"modules\":{\"shared\":\"x.dll\"}}");
                zip.CreateEntry("../evil.txt");
            }
            var dir = Path.Combine(Path.GetTempPath(), "zipslip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.Throws<InvalidOperationException>(() => ModPackageInstaller.Install(ms.ToArray(), dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>探针 Mod：经 ModCall 消费商店能力（§12.11）。</summary>
        public sealed class StoreProbeMod : IMod
        {
            public static readonly ModId Id = new("com.test.storeprobe");
            public static string[]? Listed;

            public void Register(IModContext context)
            {
                Listed = ((string[]?)context.Mods.Call(
                    ModStoreMod.ModIdValue, ModStoreMod.ListCap, null)) ?? Array.Empty<string>();
            }

            public void Unregister(IModContext context) { }
        }

        [Test]
        public static void Capability_List_ViaModCall()
        {
            var (host, server, _, tempRoot) = Setup();
            try
            {
                host.Load(new ModManifest
                {
                    ModId = StoreProbeMod.Id,
                    Version = new ModVersion(1, 0, 0),
                    Entry = typeof(StoreProbeMod).FullName!,
                    SharedModule = "test.dll",
                    Dependencies =
                    {
                        new ModDependency { Id = new ModId("com.game.modstore"), Version = VersionRange.Any },
                    },
                }, typeof(StoreProbeMod).Assembly);

                Assert.True(StoreProbeMod.Listed is not null);
                Assert.True(StoreProbeMod.Listed!.Any(s => s.StartsWith("com.test.plugin@")),
                    $"能力返回不含 hello: [{string.Join(", ", StoreProbeMod.Listed!)}]");
            }
            finally { server.Dispose(); Directory.Delete(tempRoot, true); }
        }
    }
}
