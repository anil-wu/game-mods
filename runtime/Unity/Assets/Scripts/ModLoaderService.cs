using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.ECS;
using Game.Messaging;
using Game.ModLoader;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>
    /// 运行时 Mod 加载器：发现 → 依赖解析/排序 → 加载 entryDll → 实例化 IMod → OnLoad。
    /// </summary>
    public sealed class ModLoaderService
    {
        public readonly List<Assembly> LoadedAssemblies = new();
        public readonly List<IMod> LoadedMods = new();
        public readonly List<string> Errors = new();

        /// <summary>检测到的协议宿主（协议核心 Mod 实现 IProtocolHost）。</summary>
        public IProtocolHost? ProtocolHost { get; private set; }

        public void Load(World world, SystemGroup systems, MessageBus messages, string modsDir)
        {
            Errors.Clear();
            var packages = ModDiscovery.Discover(modsDir);
            if (packages.Count == 0)
            {
                Errors.Add($"未发现任何 Mod（目录: {modsDir}）");
                Debug.LogError($"[ModLoader] {Errors[0]}");
                return;
            }

            var result = LoadOrder.Resolve(packages);
            if (!result.Success)
            {
                Errors.AddRange(result.Errors);
                foreach (var e in result.Errors)
                    Debug.LogError($"[ModLoader] {e}");
                return;
            }

            foreach (var pkg in result.Order)
                LoadPackage(pkg, world, systems, messages);
        }

        private void LoadPackage(ModPackage pkg, World world, SystemGroup systems, MessageBus messages)
        {
            var entryPath = pkg.ResolveFile(pkg.Manifest.EntryDll);
            if (entryPath is null)
            {
                Debug.LogError($"[ModLoader] 找不到 entryDll: {pkg.Manifest.EntryDll}");
                return;
            }

            // 从字节加载（比 LoadFrom 更可靠，引用解析走已加载的 Plugins 程序集）
            Assembly asm;
            try
            {
                asm = Assembly.Load(File.ReadAllBytes(entryPath));
            }
            catch (Exception e)
            {
                Debug.LogError($"[ModLoader] 加载 {pkg.Manifest.ModId} 失败: {e.Message}");
                return;
            }
            LoadedAssemblies.Add(asm);

            Type? modType = null;
            foreach (var t in asm.GetTypes())
            {
                if (!t.IsAbstract && !t.IsInterface && typeof(IMod).IsAssignableFrom(t))
                {
                    modType = t;
                    break;
                }
            }

            if (modType is null)
            {
                Debug.LogWarning($"[ModLoader] {pkg.Manifest.ModId} 未实现 IMod，跳过");
                return;
            }

            var mod = (IMod)Activator.CreateInstance(modType)!;
            var ctx = new ModContextImpl(pkg.Manifest, world, systems, messages);
            mod.OnLoad(ctx);
            LoadedMods.Add(mod);

            if (mod is IProtocolHost host)
                ProtocolHost = host;

            // 实例化 Mod 自带的视图（MonoBehaviour）——通用机制，运行时无需感知具体 Mod
            InstantiateViews(asm);

            Debug.Log($"[ModLoader] 已加载 {pkg.Manifest.InstanceId}");
        }

        private void InstantiateViews(Assembly asm)
        {
            foreach (var t in asm.GetTypes())
            {
                if (typeof(MonoBehaviour).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(MonoBehaviour))
                {
                    var go = new GameObject(t.Name);
                    go.AddComponent(t);
                    Debug.Log($"[ModLoader] 实例化视图 {t.Name}");
                }
            }
        }
    }
}
