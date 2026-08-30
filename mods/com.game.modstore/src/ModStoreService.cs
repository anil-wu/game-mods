using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace Com.Game.ModStore
{
    /// <summary>
    /// Mod 商店服务（核心逻辑，无 UnityEngine 依赖）：
    /// 呈现服务器 Mod 列表 → 按依赖闭包下载安装到本地 mods 目录 → 经 ModManager 启动。
    /// </summary>
    public sealed class ModStoreService : IDisposable
    {
        private readonly ModServerApi _api;
        private readonly IModContext _context;

        /// <summary>本地 mods 安装目录（Unity 侧由视图设为 persistentDataPath/mods）。</summary>
        public string LocalModsDir { get; set; }

        /// <summary>服务器地址（env MOD_SERVER_URL 可覆盖）。</summary>
        public string ServerBaseUrl
        {
            get => _api.BaseUrl;
            set => _api.BaseUrl = value;
        }

        /// <summary>最近一次操作的展示状态（供视图/日志呈现）。</summary>
        public string Status { get; private set; } = "";

        public ModStoreService(IModContext context, string serverBaseUrl, string localModsDir)
        {
            _context = context;
            _api = new ModServerApi(serverBaseUrl);
            LocalModsDir = localModsDir;
        }

        /// <summary>呈现：服务器上有哪些 Mod。</summary>
        public async Task<RemoteModInfo[]> ListRemoteMods()
        {
            var mods = await _api.ListMods().ConfigureAwait(false);
            Status = $"服务器 {ServerBaseUrl} 共 {mods.Length} 个 Mod";
            return mods;
        }

        /// <summary>某 Mod 是否已在运行时加载（供视图呈现状态）。</summary>
        public bool IsModLoaded(ModId id) => _context.Mods.IsLoaded(id);

        /// <summary>安装：按依赖闭包下载并解包到本地 mods 目录（已安装/已加载的跳过）。</summary>
        public async Task<string[]> InstallWithClosure(ModId id, ModVersion version)
        {
            var closure = await _api.ResolveClosure(id, version).ConfigureAwait(false);
            var installed = new List<string>();

            foreach (var remote in closure)
            {
                if (_context.Mods.IsLoaded(remote.Id))
                {
                    Status = $"'{remote}' 已加载，跳过安装";
                    continue;
                }
                if (ModPackageInstaller.IsInstalled(remote.Id, LocalModsDir))
                {
                    var installedVersion = ModPackageInstaller.GetInstalledVersion(remote.Id, LocalModsDir);
                    if (installedVersion is not null && installedVersion.Value == remote.Version)
                    {
                        // 同版本已安装：注册包目录使依赖可被解析
                        RegisterIfPresent(remote.Id);
                        continue;
                    }
                    // 版本不一致（或清单损坏）→ 清空重装，杜绝旧包残留
                    Status = $"{remote.Id.Value} 版本更新 {installedVersion} → {remote.Version}，重新安装";
                    ModPackageInstaller.Wipe(remote.Id, LocalModsDir);
                }

                Status = $"正在下载 {remote} …";
                var bytes = await _api.DownloadPackage(remote.Id, remote.Version).ConfigureAwait(false);
                var dir = ModPackageInstaller.Install(bytes, LocalModsDir);
                RegisterIfPresent(remote.Id);
                installed.Add(dir);
                Status = $"已安装 {remote} → {dir}";
            }
            return installed.ToArray();
        }

        /// <summary>启动：加载已安装的 Mod（依赖由 ModManager 递归解析，§12.3）。</summary>
        public ModObject StartInstalled(ModId id)
        {
            var modDir = System.IO.Path.Combine(LocalModsDir, id.Value);
            var mod = _context.Mods.LoadFromDirectory(modDir);
            Status = $"已启动 {id.Value}@{mod.Info.Version}";
            return mod;
        }

        /// <summary>一键：安装闭包并启动目标 Mod（无头/测试场景）。
        /// Unity 调用方注意：StartInstalled 会触发 ModLoaded → 视图实例化（Unity API），
        /// 必须在主线程执行——应拆两段：后台 InstallWithClosure + 主线程 StartInstalled。</summary>
        public async Task<ModObject> InstallAndStart(ModId id, ModVersion version)
        {
            await InstallWithClosure(id, version).ConfigureAwait(false);
            return StartInstalled(id);
        }

        private void RegisterIfPresent(ModId id)
        {
            if (_context.Mods.IsLoaded(id)) return;
            var modDir = System.IO.Path.Combine(LocalModsDir, id.Value);
            if (System.IO.File.Exists(System.IO.Path.Combine(modDir, "manifest.json")))
                _context.Mods.RegisterDirectory(modDir); // 仅注册不加载，供依赖递归解析
        }

        public void Dispose() => _api.Dispose();
    }
}
