using System;
using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>目标 Mod 未加载 / 已卸载（§12.9 硬规则 3：调用方必须处理）。</summary>
    public sealed class NoModException : Exception
    {
        public ModId Mod { get; }
        public NoModException(ModId mod) : base($"Mod '{mod}' 未加载") => Mod = mod;
    }

    /// <summary>目标 Mod 未导出该能力（§12.11）。</summary>
    public sealed class NoCapabilityException : Exception
    {
        public CapabilityId Capability { get; }
        public NoCapabilityException(CapabilityId cap) : base($"能力 '{cap}' 未导出") => Capability = cap;
    }

    /// <summary>服务未注册（如 UI.Mod / Network.Mod 未加载时的基础设施服务）。</summary>
    public sealed class NoServiceException : Exception
    {
        public ServiceId Service { get; }
        public NoServiceException(ServiceId service) : base($"服务 '{service}' 未注册") => Service = service;
    }

    /// <summary>资源访问越权（跨 Mod 直接访问资源，§8.4）。</summary>
    public sealed class ResourceAccessException : Exception
    {
        public ResourceAccessException(string message) : base(message) { }
    }

    /// <summary>Mod 生命周期状态错误（如卸载仍被依赖的 Mod）。</summary>
    public sealed class ModStateException : Exception
    {
        public ModStateException(string message) : base(message) { }
    }

    /// <summary>网络层错误（方向 / 归属 / 版本校验失败等）。</summary>
    public sealed class NetworkException : Exception
    {
        public NetworkException(string message) : base(message) { }
    }
}
