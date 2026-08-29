using System;
using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>
    /// 资源域（§8.8）：Scope.Dispose → 域内资源全部释放。
    /// 全非泛型（Rule 13）；强类型由生成包装提供（如 WeaponAssets.Sword(scope)）。
    /// </summary>
    public interface IAssetScope : IDisposable
    {
        /// <summary>加载资源（仅限本 Mod 命名空间；跨 Mod 用 Resolve 且需声明依赖，§8.4）。</summary>
        object Load(AssetId id);

        /// <summary>释放引用；最终由 Resource Runtime 决定何时真正释放底层对象（§8.7）。</summary>
        void Release(AssetId id);
    }

    /// <summary>
    /// Mod 资源域：资源生命周期归属于 ModObject.Resources（§8.1）。
    /// Mod 自治：资源技术由 Mod 自己决定（Addressables / AssetBundle / YooAsset / Custom，§8.2），
    /// Core 只通过 <see cref="IResourceBackend"/> 看到统一加载结果。
    /// </summary>
    public interface IModResourceScope : IAssetScope
    {
        /// <summary>创建子域（§8.8 示例：界面级资源域，Dispose 即整域释放）。</summary>
        IAssetScope CreateScope();

        /// <summary>跨 Mod 受限资源解析（§8.4 四条合法通道之一）：要求已声明对目标 Mod 的依赖。</summary>
        object Resolve(AssetId id);

        /// <summary>卸载时释放本 Mod 全部资源（由 Core 调用）。</summary>
        void ReleaseAll();
    }

    /// <summary>
    /// 资源后端：真正的加载机制由平台/管线提供（§8.2 Mod 自治）。
    /// 默认实现为文件后端（Mod 包内数据文件）；Unity 侧可换成 Addressables / Bundle。
    /// </summary>
    public interface IResourceBackend
    {
        /// <summary>按 Mod 包内相对路径加载资源（不存在返回 null）。</summary>
        object? Load(string contentRoot, string localPath);

        /// <summary>释放底层资源（文件后端无操作；Unity 后端释放 Unity Object）。</summary>
        void Release(object resource);
    }
}
