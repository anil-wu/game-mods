using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace Com.Fps.Game
{
    /// <summary>
    /// 主 Mod / 启动 Mod（com.fps.game）：FPS 游戏的入口与身份。
    /// - 依赖全部组件 Mod（player/weapon/inventory/npc/mapgen/console）——装它一个 = 装整个游戏（依赖闭包）。
    /// - Register 即"启动游戏"：当前组件 Mod 各自完成生成；未来会话生命周期（开局/加入/结束）在此编排。
    /// - 导出能力供商店/大厅查询游戏状态（§12.11）。
    /// </summary>
    public sealed class GameMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.fps.game");
        public static readonly CapabilityId StatusCap = new(ModIdValue, "status");

        public delegate string StatusHandler(object? args);

        public static IModContext Context { get; private set; } = null!;
        public static bool IsRunning { get; private set; }

        public void Register(IModContext context)
        {
            Context = context;
            IsRunning = true;
            context.Mods.Export(StatusCap, new StatusHandler(_ => IsRunning ? "running" : "stopped"));
            context.Log.Info($"FPS 游戏 '{context.Info.Id}' v{context.Info.Version} 已启动（依赖组件 Mod 已就绪）");
        }

        public void Unregister(IModContext context)
        {
            IsRunning = false;
            Context = null!;
            context.Log.Info($"FPS 游戏 '{context.Info.Id}' 已退出");
        }
    }
}
