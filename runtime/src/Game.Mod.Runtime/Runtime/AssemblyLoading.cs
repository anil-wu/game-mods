using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
#if NET8_0_OR_GREATER
using System.Runtime.Loader;
#endif

namespace Game.Mod.Runtime
{
    /// <summary>程序集加载结果（Unload 句柄）。</summary>
    public sealed class AssemblyLoadResult
    {
        public Assembly[] Assemblies { get; }
        internal object? Handle { get; set; }

        public AssemblyLoadResult(Assembly[] assemblies) => Assemblies = assemblies;
    }

    /// <summary>
    /// 程序集加载抽象：Unity 侧字节加载（HybridCLR 接入前不支持真正卸载），
    /// net8 侧可收集 ALC 验证完整卸载流程。
    /// </summary>
    public interface IAssemblyLoader
    {
        AssemblyLoadResult Load(IEnumerable<string> modulePaths);
        void Unload(AssemblyLoadResult result);
    }

    /// <summary>字节数组加载（跨平台；引用解析走已加载的框架程序集）。Unload 为平台限制空操作。</summary>
    public sealed class ByteArrayAssemblyLoader : IAssemblyLoader
    {
        public AssemblyLoadResult Load(IEnumerable<string> modulePaths)
        {
            var list = new List<Assembly>();
            foreach (var path in modulePaths)
                list.Add(Assembly.Load(File.ReadAllBytes(path)));
            return new AssemblyLoadResult(list.ToArray());
        }

        public void Unload(AssemblyLoadResult result) { /* 域内卸载需 HybridCLR / ALC */ }
    }

#if NET8_0_OR_GREATER
    /// <summary>可收集 ALC 加载（net8）：Unload 后 GC 可回收程序集（验证热卸载流程）。</summary>
    public sealed class CollectibleAssemblyLoader : IAssemblyLoader
    {
        private sealed class ModAlc : AssemblyLoadContext
        {
            public ModAlc() : base(isCollectible: true) { }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                // 框架程序集（Game.Mod.* / Game.ECS / Game.Messaging）回落到 Default 上下文共享
                return null;
            }
        }

        public AssemblyLoadResult Load(IEnumerable<string> modulePaths)
        {
            var alc = new ModAlc();
            var list = new List<Assembly>();
            foreach (var path in modulePaths)
            {
                using var fs = File.OpenRead(path);
                list.Add(alc.LoadFromStream(fs));
            }
            return new AssemblyLoadResult(list.ToArray()) { Handle = alc };
        }

        public void Unload(AssemblyLoadResult result)
        {
            if (result.Handle is AssemblyLoadContext alc)
                alc.Unload();
        }
    }
#endif
}
