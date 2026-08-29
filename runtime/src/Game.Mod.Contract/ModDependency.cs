using System;
using System.Collections.Generic;

namespace Game.Mod.Contract
{
    /// <summary>依赖端侧：决定依赖在哪个运行环境生效（§12.6，Rule 8）。</summary>
    public enum ModScope
    {
        /// <summary>双端都需要。</summary>
        Shared,

        /// <summary>仅 Client（Host 也加载）。</summary>
        Client,

        /// <summary>仅 Server（Host 也加载）。</summary>
        Server,
    }

    /// <summary>
    /// Mod 依赖声明（§12.2）："能力依赖" + "版本约束"，不是 DLL 依赖。
    /// </summary>
    public sealed class ModDependency
    {
        public ModId Id { get; set; }

        public VersionRange Version { get; set; } = VersionRange.Any;

        /// <summary>Optional：存在则增强，不存在仍可运行（§12.4）。</summary>
        public bool Optional { get; set; }

        /// <summary>依赖生效端侧（§12.6）。</summary>
        public ModScope Scope { get; set; } = ModScope.Shared;

        /// <summary>只依赖需要的 Feature（§12.5），空 = 整个 Mod。</summary>
        public List<string> Features { get; set; } = new();

        public override string ToString() =>
            $"{Id} {Version}{(Optional ? " (optional)" : "")}{(Scope != ModScope.Shared ? $" [{Scope}]" : "")}";
    }
}
