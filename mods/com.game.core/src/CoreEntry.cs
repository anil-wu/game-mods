using Game.ModLoader;

namespace Com.Game.Core
{
    /// <summary>
    /// 游戏核心 Mod（com.game.core）：游戏的基础内容，随运行时分发。
    /// 第三方 Mod 依赖它。当前为最小骨架，后续承载共享组件/系统/内容。
    /// </summary>
    public sealed class CoreEntry : IMod
    {
        public void OnLoad(IModContext context)
        {
            context.Log.Info($"核心 Mod '{context.ModId}' v{context.Version} 已加载");
        }
    }
}
