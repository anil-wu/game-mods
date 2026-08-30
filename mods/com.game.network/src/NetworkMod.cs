using Game.ECS;
using Game.Mod.Contract;
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
        public static readonly ModId ModIdValue = new("com.game.network");

        public static NetworkRuntimeImpl? Current { get; private set; }

        public void Register(IModContext context)
        {
            var runtime = new NetworkRuntimeImpl();
            Current = runtime;

            // ECS Replication（§11.10）：复制运行时 + 基础设施协议 + 快照泵
            var replication = new ReplicationRuntime(context.Ecs.World, runtime);
            runtime.Replication = replication;

            // 框架 Mod 经周知 ServiceId 发布基础设施实现（§10.2），context.Network 即路由到此
            context.Services.Register(WellKnownServices.NetworkRuntime, runtime);

            context.Network.RegisterProtocol(
                new ReplicationProtocol(ReplicationRuntime.ReplicateProtocolPath),
                new ReplicationFrameHandler(replication, despawn: false));
            context.Network.RegisterProtocol(
                new ReplicationProtocol(ReplicationRuntime.DespawnProtocolPath),
                new ReplicationFrameHandler(replication, despawn: true));
            if (context.HasServer)
                context.Ecs.RegisterSystem(new SnapshotSystem(replication), SystemSide.Server);

            context.Log.Info($"Network.Mod '{context.Info.Id}' v{context.Info.Version} 已注册（Role×Path 正交，Rule 15；" +
                $"版本协商 §11.6 + 兴趣管理 §11.8 已装配）");
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
