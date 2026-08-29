using System.Reflection;
using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>Mod 运行信息。</summary>
    public sealed class ModInfo : IModInfo
    {
        public ModId Id { get; }
        public ModVersion Version { get; }
        public ModManifest Manifest { get; }

        public ModInfo(ModManifest manifest)
        {
            Manifest = manifest;
            Id = manifest.ModId;
            Version = manifest.Version;
        }

        public override string ToString() => Manifest.InstanceId;
    }

    /// <summary>ModObject 状态。</summary>
    public enum ModObjectState
    {
        Loading,
        Running,
        Unloading,
        Unloaded,
    }

    /// <summary>
    /// ModObject：Core 对 Mod 的运行时实例，真正的"资源边界"（§4）。
    /// Unload ModObject = 全部资源释放 + 全部 Pool 清理 + 全部注册注销 + 全部引用解除。
    /// </summary>
    public sealed class ModObject
    {
        public ModInfo Info { get; }
        public IMod Instance { get; }
        public IModContext Context { get; }
        public IModResourceScope Resources { get; }
        public IModPoolScope Pool { get; }

        /// <summary>本 Mod 加载的程序集（供平台层挂视图 / 调试）。</summary>
        public Assembly[] Assemblies { get; }

        /// <summary>程序集加载句柄（可收集 ALC 时用于卸载）。</summary>
        internal object? AssemblyLoadHandle { get; set; }

        /// <summary>包内容根目录（文件后端资源根；无内容包可为空）。</summary>
        public string? ContentRoot { get; }

        public ModObjectState State { get; internal set; }

        internal ModObject(
            ModInfo info,
            IMod instance,
            IModContext context,
            IModResourceScope resources,
            IModPoolScope pool,
            Assembly[] assemblies,
            string? contentRoot)
        {
            Info = info;
            Instance = instance;
            Context = context;
            Resources = resources;
            Pool = pool;
            Assemblies = assemblies;
            ContentRoot = contentRoot;
            State = ModObjectState.Loading;
        }

        public override string ToString() => $"ModObject({Info}, {State})";
    }
}
