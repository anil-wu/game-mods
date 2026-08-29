using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// Network.Mod（§11）：所有 Mod 共享的网络基础设施，Framework Mod（§10.4）。
    /// 是整个游戏唯一的网络入口和网络出口：协议注册表 / 路由 / 方向校验 / 版本 / 统计都在这里；
    /// 业务 Mod 只负责定义协议 + Encode/Decode/Handler（Rule 4），永远不引用 Mirror / Transport。
    /// </summary>
    public sealed class NetworkMod : IMod
    {
        public static NetworkRuntimeImpl? Current { get; private set; }

        public void Register(IModContext context)
        {
            var runtime = new NetworkRuntimeImpl();
            Current = runtime;

            // 框架 Mod 经周知 ServiceId 发布基础设施实现（§10.2），context.Network 即路由到此
            context.Services.Register(WellKnownServices.NetworkRuntime, runtime);

            context.Log.Info($"Network.Mod '{context.Info.Id}' v{context.Info.Version} 已注册（Role×Path 正交，Rule 15）");
        }

        public void Unregister(IModContext context)
        {
            if (Current is not null)
            {
                Current.Stop();
                Current.DetachTransport();
            }
            context.Services.Unregister(WellKnownServices.NetworkRuntime);
            Current = null;
        }
    }
}
