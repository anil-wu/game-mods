using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>一条能力调用 Trace（§12.11 规则 5：谁在调谁的什么能力、多频繁、多慢）。</summary>
    public readonly struct ModCallTraceEntry
    {
        public readonly ModId Caller;
        public readonly ModId Target;
        public readonly CapabilityId Capability;
        public readonly long ElapsedTicks;

        public ModCallTraceEntry(ModId caller, ModId target, CapabilityId capability, long elapsedTicks)
        {
            Caller = caller;
            Target = target;
            Capability = capability;
            ElapsedTicks = elapsedTicks;
        }
    }

    /// <summary>
    /// ModManager（§7）：所有 Mod 生命周期的真正管理者。
    /// 加载：Manifest → 依赖解析 → 程序集 → ModObject → Context → Scope → IMod.Register。
    /// 卸载：IMod.Unregister → CloseAll(modId) → 强制退订 → 协议注销 → ECS 注销 → 服务/能力撤销 →
    ///       清池 → 释放资源域 → Context 失效 → 卸载程序集（§7/§10.8/§13.5）。
    /// </summary>
    public sealed class ModManager
    {
        private readonly Dictionary<ModId, ModObject> _loaded = new();
        private readonly List<ModId> _loadOrder = new(); // 真实加载顺序（依赖在前），UnloadAll 取其镜像
        private readonly Dictionary<CapabilityId, (ModId Owner, Delegate Handler)> _capabilities = new();
        private readonly List<ModCallTraceEntry> _callTrace = new(256);
        private int _callTraceHead;
        private int _callTraceCount;

        private readonly World _world;
        private readonly SystemGroup _systems;
        private readonly MessageBus _bus;
        private readonly ServiceRegistry _services;
        private readonly IAssemblyLoader _assemblyLoader;
        private readonly IResourceBackend _resourceBackend;
        private readonly ILog _log;

        public RuntimeRole Role { get; }
        public bool HasClient => Role != RuntimeRole.Server;
        public bool HasServer => Role != RuntimeRole.Client;

        /// <summary>已发现的包（LoadDirectory 后可用）。</summary>
        public IReadOnlyDictionary<ModId, ModPackage> Packages => _packages;
        private readonly Dictionary<ModId, ModPackage> _packages = new();

        /// <summary>Mod 加载完成事件（平台层挂视图等通用机制挂接点）。</summary>
        public event Action<ModObject>? ModLoaded;

        /// <summary>Mod 卸载完成事件。</summary>
        public event Action<ModId>? ModUnloaded;

        public ModManager(
            RuntimeRole role,
            World world,
            SystemGroup systems,
            MessageBus bus,
            ServiceRegistry services,
            IAssemblyLoader? assemblyLoader = null,
            IResourceBackend? resourceBackend = null,
            ILog? log = null)
        {
            Role = role;
            _world = world;
            _systems = systems;
            _bus = bus;
            _services = services;
            _assemblyLoader = assemblyLoader ?? new ByteArrayAssemblyLoader();
            _resourceBackend = resourceBackend ?? FileResourceBackend.Instance;
            _log = log ?? NullLog.Instance;
        }

        // ---- 发现与整批加载 ----

        /// <summary>发现目录下全部 Mod 包并按依赖顺序加载（ModLoader + ModResolver，§12.3）。</summary>
        public List<string> LoadDirectory(string modsDir)
        {
            var packages = ModDiscovery.Discover(modsDir);
            if (packages.Count == 0)
                return new List<string> { $"未发现任何 Mod（目录: {modsDir}）" };

            foreach (var p in packages)
                _packages[p.Manifest.ModId] = p;

            var result = ModResolver.Resolve(packages.Select(p => p.Manifest), HasClient, HasServer);
            if (!result.Success)
                return result.Errors;

            foreach (var reason in result.SkippedOptional.Values)
                _log.Warn($"[ModManager] {reason}");

            var errors = new List<string>();
            foreach (var manifest in result.Order)
            {
                try
                {
                    LoadFromPackage(_packages[manifest.ModId]);
                }
                catch (Exception e)
                {
                    errors.Add($"加载 {manifest.InstanceId} 失败: {e.Message}");
                    _log.Error($"[ModManager] {errors[^1]}");
                }
            }
            return errors;
        }

        /// <summary>注册一个外部清单（无物理包，供测试 / 嵌入式 Mod）。</summary>
        public void RegisterManifest(ModManifest manifest, string? contentRoot = null)
        {
            _packages[manifest.ModId] = new ModPackage(manifest, contentRoot ?? "");
        }

        /// <summary>注册清单 + 预加载程序集（测试 / 嵌入式路径：Load(id) 递归加载依赖时直接使用）。</summary>
        public void RegisterManifest(ModManifest manifest, System.Reflection.Assembly[] assemblies, string? contentRoot = null)
        {
            RegisterManifest(manifest, contentRoot);
            _preloadedAssemblies[manifest.ModId] = (assemblies, contentRoot);
        }

        private readonly Dictionary<ModId, (System.Reflection.Assembly[] Assemblies, string? ContentRoot)> _preloadedAssemblies = new();

        /// <summary>从单个包目录注册并加载（Mod 商店等管理型 Mod 的动态安装入口）。</summary>
        public ModObject LoadFromDirectory(string modDirectory)
        {
            var manifestPath = Path.Combine(modDirectory, ModDiscovery.ManifestFileName);
            if (!File.Exists(manifestPath))
                throw new ModStateException($"目录不含 {ModDiscovery.ManifestFileName}: {modDirectory}");
            var manifest = ModManifest.Parse(File.ReadAllText(manifestPath));
            _packages[manifest.ModId] = new ModPackage(manifest, modDirectory);
            return Load(manifest.ModId); // 依赖递归解析（§12.3）
        }

        /// <summary>仅注册包目录不加载（批量安装闭包后统一 Load 目标 Mod）。</summary>
        public ModManifest RegisterDirectory(string modDirectory)
        {
            var manifestPath = Path.Combine(modDirectory, ModDiscovery.ManifestFileName);
            if (!File.Exists(manifestPath))
                throw new ModStateException($"目录不含 {ModDiscovery.ManifestFileName}: {modDirectory}");
            var manifest = ModManifest.Parse(File.ReadAllText(manifestPath));
            _packages[manifest.ModId] = new ModPackage(manifest, modDirectory);
            return manifest;
        }

        // ---- IModManager 语义（显式调用方） ----

        public ModObject? Get(ModId id) => _loaded.TryGetValue(id, out var m) ? m : null;

        public bool IsLoaded(ModId id) => _loaded.ContainsKey(id);

        public IReadOnlyCollection<ModObject> Loaded => _loaded.Values;

        /// <summary>按依赖顺序加载指定 Mod（未加载的依赖先加载）。</summary>
        public ModObject Load(ModId id)
        {
            if (_loaded.TryGetValue(id, out var existing)) return existing;
            if (!_packages.TryGetValue(id, out var package))
                throw new NoModException(id);

            var manifest = package.Manifest;
            foreach (var dep in ModResolver.ApplicableDependencies(manifest, HasClient, HasServer))
            {
                if (!IsLoaded(dep.Id))
                {
                    if (dep.Optional) continue; // Optional：存在则增强，不存在仍可运行（§12.4）
                    Load(dep.Id); // Required：递归先加载依赖（§12.3 依赖在前）
                }
            }
            // 预加载程序集路径（测试 / 嵌入式）优先于物理包文件
            if (_preloadedAssemblies.TryGetValue(id, out var preloaded))
                return CreateModObject(manifest, preloaded.Assemblies, preloaded.ContentRoot, null);
            return LoadFromPackage(package);
        }

        /// <summary>从已加载程序集创建 ModObject（测试 / Unity 嵌入式路径）。</summary>
        public ModObject Load(ModManifest manifest, Assembly[] assemblies, string? contentRoot = null)
        {
            return CreateModObject(manifest, assemblies, contentRoot, null);
        }

        // ---- 卸载（§7 最终定义） ----

        public void Unload(ModId id)
        {
            if (!_loaded.TryGetValue(id, out var mod))
                throw new NoModException(id);

            // 主 Mod 禁止销毁（§10.4：随进程生命周期存活）
            if (mod.Info.Manifest.Pinned)
                throw new ModStateException($"主 Mod '{id}' 禁止销毁（pinned，§10.4）");

            // 卸载仍被依赖的 Mod 属于生命周期错误（Rule 7：注销顺序与注册相反）
            foreach (var other in _loaded.Values)
            {
                if (other.Info.Id == id) continue;
                foreach (var dep in ModResolver.ApplicableDependencies(other.Info.Manifest, HasClient, HasServer))
                {
                    if (dep.Id == id)
                        throw new ModStateException(
                            $"Mod '{id}' 仍被 '{other.Info.Id}' 依赖，请先卸载依赖方（依赖必须形成 DAG，§12.8）");
                }
            }

            mod.State = ModObjectState.Unloading;
            var modId = mod.Info.Id;

            // 1. 对称注销（业务自己撤销声明）
            try { mod.Instance.Unregister(mod.Context); }
            catch (Exception e) { _log.Error($"[ModManager] {modId}.Unregister 异常: {e.Message}"); }

            // 2. UI.Mod 强制关闭该 Mod 全部窗口（§10.8，不依赖业务 Mod 自觉）
            if (_services.TryGet(WellKnownServices.WindowManager, out var wm))
                ((IWindowManager)wm).CloseAll(modId);

            // 3. 消息总线强制退订（§13.5）
            _bus.UnsubscribeAll(modId);

            // 4. 网络协议按 OwnerMod 一次性清理（§11.6）
            if (_services.TryGet(WellKnownServices.NetworkRuntime, out var net))
                ((INetworkRuntime)net).UnregisterAll(modId);

            // 5. ECS 系统移除（§9.2：World 属于 Runtime，Mod 只注册）
            _systems.RemoveAll(modId);

            // 5.1 销毁本 Mod 拥有的全部实体（ModObject 资源边界，§4/§7）
            _world.DestroyAll(modId);

            // 6. 服务注册全部撤销（含框架 Mod 基础设施）
            _services.UnregisterAll(modId);

            // 7. 导出的能力全部撤销（§13.5：再调用得到 NoCapabilityException 而非僵尸代码）
            var staleCaps = new List<CapabilityId>();
            foreach (var (capId, entry) in _capabilities)
                if (entry.Owner == modId) staleCaps.Add(capId);
            foreach (var cap in staleCaps) _capabilities.Remove(cap);

            // 8. 清空对象池（§9.1）
            mod.Pool.Clear();

            // 9. 释放资源域（§8.7）
            mod.Resources.ReleaseAll();

            // 10. Context 失效（热卸载后残留引用立即报错而非野指针）
            if (mod.Context is ModContextImpl impl) impl.Invalidate();

            // 11. 卸载程序集（net8 可收集 ALC；Unity 字节加载为平台限制空操作）
            if (mod.AssemblyLoadHandle is AssemblyLoadResult alr)
                _assemblyLoader.Unload(alr);

            mod.State = ModObjectState.Unloaded;
            _loaded.Remove(modId);
            _loadOrder.Remove(modId);
            ModUnloaded?.Invoke(modId);
            _log.Info($"[ModManager] 已卸载 {mod.Info}");
        }

        /// <summary>
        /// 全部卸载（退出游戏）：按加载顺序的镜像销毁——
        /// 依赖方先卸、被依赖方后卸（加载时依赖在前由拓扑保证，镜像即合法注销顺序，§12.3）。
        /// 尽力而为：单个失败记录错误并继续，保证整体关闭不中断。
        /// </summary>
        public List<string> UnloadAll()
        {
            var errors = new List<string>();
            var mirror = _loadOrder.ToArray();
            for (var i = mirror.Length - 1; i >= 0; i--)
            {
                var id = mirror[i];
                if (!_loaded.ContainsKey(id)) continue;
                if (_loaded[id].Info.Manifest.Pinned)
                {
                    _log.Info($"[ModManager] 主 Mod '{id}' 跳过销毁（pinned）");
                    continue; // 主 Mod 不被销毁，随进程生命周期存活
                }
                try
                {
                    Unload(id);
                }
                catch (Exception e)
                {
                    errors.Add($"卸载 {id} 失败: {e.Message}");
                    _log.Error($"[ModManager] {errors[^1]}");
                }
            }
            return errors;
        }

        // ---- ModCall（§12.11） ----

        internal void Export(ModId caller, CapabilityId id, Delegate handler)
        {
            if (handler is null) throw new ArgumentNullException(nameof(handler));
            if (id.Mod != caller)
                throw new ModStateException($"Mod '{caller}' 不能导出其他 Mod 命名空间的能力 '{id}'");
            _capabilities[id] = (caller, handler);
        }

        internal object? Call(ModId caller, ModId target, CapabilityId id, object? args)
        {
            // 规则 1：必须先声明依赖——未声明依赖的 Call 直接被 Core 拒绝（§12.11）
            if (caller != target)
            {
                if (!_loaded.TryGetValue(caller, out var callerMod))
                    throw new NoModException(caller);
                var declared = ModResolver
                    .ApplicableDependencies(callerMod.Info.Manifest, HasClient, HasServer)
                    .Any(d => d.Id == target);
                if (!declared)
                    throw new ModDependencyException(
                        $"Mod '{caller}' 未在 Manifest 声明对 '{target}' 的依赖，能力调用被拒绝（§12.11 规则 1）");
            }

            // 规则 2：目标未加载 → NoModException；能力未导出 → NoCapabilityException
            if (!_loaded.ContainsKey(target))
                throw new NoModException(target);
            if (id.Mod != target || !_capabilities.TryGetValue(id, out var entry))
                throw new NoCapabilityException(id);

            var start = Stopwatch.GetTimestamp();
            var result = entry.Handler.DynamicInvoke(args); // 生成包装保证签名匹配（§15.3）
            RecordCallTrace(caller, target, id, Stopwatch.GetTimestamp() - start);
            return result;
        }

        /// <summary>最近 N 条能力调用 Trace。</summary>
        public IReadOnlyList<ModCallTraceEntry> CallTrace()
        {
            var result = new List<ModCallTraceEntry>(_callTraceCount);
            for (var i = 0; i < _callTraceCount; i++)
            {
                var idx = (_callTraceHead - 1 - i + _callTrace.Capacity) % _callTrace.Capacity;
                result.Add(_callTrace[idx]);
            }
            return result;
        }

        internal bool HasCapability(CapabilityId id) => _capabilities.ContainsKey(id);

        // ---- 内部：加载 ----

        private ModObject LoadFromPackage(ModPackage package)
        {
            var manifest = package.Manifest;
            var modulePaths = new List<string>();
            foreach (var module in manifest.ModulesFor(HasClient, HasServer))
            {
                var path = package.ResolveFile(module);
                if (path is null)
                    throw new ModStateException($"找不到模块程序集: {module}（{manifest.InstanceId}）");
                modulePaths.Add(path);
            }

            var loadResult = _assemblyLoader.Load(modulePaths);
            try
            {
                return CreateModObject(manifest, loadResult.Assemblies, package.RootDirectory, loadResult);
            }
            catch
            {
                _assemblyLoader.Unload(loadResult);
                throw;
            }
        }

        private ModObject CreateModObject(
            ModManifest manifest,
            Assembly[] assemblies,
            string? contentRoot,
            AssemblyLoadResult? loadResult)
        {
            if (_loaded.TryGetValue(manifest.ModId, out var existing)) return existing;

            var modType = ResolveEntryType(manifest, assemblies)
                ?? throw new ModStateException($"{manifest.InstanceId} 未实现 IMod");

            var info = new ModInfo(manifest);
            var resources = new ModResourceScope(
                manifest.ModId,
                string.IsNullOrEmpty(contentRoot) ? null : contentRoot,
                _resourceBackend,
                ResolveExternalScope);
            var pool = new ModPoolScope(manifest.ModId);
            var context = new ModContextImpl(
                info, this, _services, _world, _systems, _bus, resources, pool, Role, _log);

            var instance = (IMod)Activator.CreateInstance(modType)!;
            var mod = new ModObject(info, instance, context, resources, pool, assemblies, contentRoot)
            {
                AssemblyLoadHandle = loadResult,
            };
            _loaded[manifest.ModId] = mod;

            // IMod.Register：向 Game Runtime 声明并注册自己提供的能力（§3）
            instance.Register(context);
            mod.State = ModObjectState.Running;
            _loadOrder.Add(manifest.ModId);

            _log.Info($"[ModManager] 已加载 {info}");
            ModLoaded?.Invoke(mod);
            return mod;
        }

        private IModResourceScope? ResolveExternalScope(AssetId id)
        {
            // 跨 Mod 资源解析（§8.4）：目标必须已加载；调用方依赖校验由 Mod 侧 Resolve 通道约束
            return _loaded.TryGetValue(id.Mod, out var target) ? target.Resources : null;
        }

        private static Type? ResolveEntryType(ModManifest manifest, Assembly[] assemblies)
        {
            if (!string.IsNullOrEmpty(manifest.Entry))
            {
                foreach (var asm in assemblies)
                {
                    var t = asm.GetType(manifest.Entry);
                    if (t is not null) return t;
                }
                throw new ModStateException($"找不到入口类型 '{manifest.Entry}'（{manifest.InstanceId}）");
            }

            foreach (var asm in assemblies)
            {
                foreach (var t in asm.GetTypes())
                {
                    if (!t.IsAbstract && !t.IsInterface && typeof(IMod).IsAssignableFrom(t))
                        return t;
                }
            }
            return null;
        }

        private void RecordCallTrace(ModId caller, ModId target, CapabilityId id, long elapsedTicks)
        {
            if (_callTrace.Count < _callTrace.Capacity) _callTrace.Add(default);
            _callTrace[_callTraceHead] = new ModCallTraceEntry(caller, target, id, elapsedTicks);
            _callTraceHead = (_callTraceHead + 1) % _callTrace.Capacity;
            if (_callTraceCount < _callTrace.Capacity) _callTraceCount++;
        }
    }
}
