using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>运行角色（§6）：Client / Server / Host 决定 HasClient / HasServer。</summary>
    public enum RuntimeRole
    {
        /// <summary>纯客户端。</summary>
        Client,

        /// <summary>Dedicated Server（无 Client 能力）。</summary>
        Server,

        /// <summary>同时运行 Client + Server 两套逻辑。</summary>
        Host,
    }

    /// <summary>周知服务 ID：框架 Mod 发布基础设施实现的约定键（§10.2）。</summary>
    public static class WellKnownServices
    {
        /// <summary>Network.Mod 发布 INetworkRuntime。</summary>
        public static readonly ServiceId NetworkRuntime = ServiceId.Parse("core.network:runtime");

        /// <summary>UI.Mod 发布 IWindowManager。</summary>
        public static readonly ServiceId WindowManager = ServiceId.Parse("core.ui:window_manager");
    }

    /// <summary>
    /// Mod 管理器（§7）：Mod 生命周期真正管理者 + Mod 间能力调用（ModCall，§12.11）。
    /// 经 IModContext.Mods 访问时自动绑定调用方 Mod：Export 归属调用方，Call 校验依赖声明。
    /// </summary>
    public interface IModManager
    {
        ModObject? Get(ModId id);
        bool IsLoaded(ModId id);

        ModObject Load(ModId id);

        /// <summary>从单个包目录（含 manifest.json）注册并加载，依赖递归解析（§12.3 由 ModResolver 统一负责）。
        /// 供 Mod 商店等管理型 Mod 在下载安装后启动 Mod 使用。</summary>
        ModObject LoadFromDirectory(string modDirectory);

        /// <summary>仅注册包目录不加载（批量安装依赖闭包后统一 Load 目标 Mod）。</summary>
        ModManifest RegisterDirectory(string modDirectory);

        /// <summary>热卸载（§7）：业务注销 → CloseAll → 强制退订 → 协议/ECS/UI 注销 → 清池 → 释放资源 → 卸载程序集。</summary>
        void Unload(ModId id);

        /// <summary>全部卸载（退出游戏时调用）：按加载顺序的镜像销毁——依赖方先卸、被依赖方后卸，
        /// 与注册顺序严格相反（§12.3：注册 Common→Inventory→UI→Weapon，注销 Weapon→UI→Inventory→Common）。
        /// 返回逐个卸载时的错误列表（尽力而为，不中断整体关闭）。</summary>
        System.Collections.Generic.List<string> UnloadAll();

        /// <summary>当前 Mod 导出能力（id 命名空间必须属于当前 Mod；卸载时随 UnsubscribeAll 一并撤销，§13.5）。</summary>
        void Export(CapabilityId id, System.Delegate handler);

        /// <summary>
        /// 调用另一个 Mod 的能力（跨 Mod 唯一的"有返回"通道，§12.11）。
        /// 未声明依赖直接拒绝；目标未加载 → NoModException；能力未导出 → NoCapabilityException。
        /// </summary>
        object? Call(ModId target, CapabilityId id, object? args);
    }
}
