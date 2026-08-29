using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>Mod 信息（§5 IModContext.Info）。</summary>
    public interface IModInfo
    {
        ModId Id { get; }
        ModVersion Version { get; }
        ModManifest Manifest { get; }
    }

    /// <summary>
    /// Mod 唯一入口（Rule 1）：整个 HybridCLR Mod ABI 的核心。
    /// 只有两个方法——Mod Runtime 负责生命周期，Mod 自己不控制整个生命周期。
    /// Register 的核心是"向 Game Runtime 声明并注册自己提供的能力"，应尽可能无业务副作用：
    /// Mod 负责声明，Runtime 负责执行（§3）。
    /// </summary>
    public interface IMod
    {
        /// <summary>注册阶段：注册 ECS / 协议 / 服务 / 窗口 / 能力。不要 StartGame / ConnectServer / StartThread。</summary>
        void Register(IModContext context);

        /// <summary>对称注销：撤销 Register 中声明的一切。</summary>
        void Unregister(IModContext context);
    }
}
