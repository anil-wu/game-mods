using Game.Mod.Runtime;

namespace Com.Game.Core
{
    /// <summary>
    /// 游戏核心 Mod（com.game.core）：游戏的基础内容，随运行时分发。
    /// 第三方 Mod 依赖它。当前为最小骨架，后续承载共享组件/系统/内容。
    /// </summary>
    public sealed class CoreMod : IMod
    {
        public void Register(IModContext context)
        {
            context.Log.Info($"核心 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            context.Log.Info($"核心 Mod '{context.Info.Id}' 已注销");
        }
    }
}
