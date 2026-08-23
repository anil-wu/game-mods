using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;
using Game.ModLoader;
using Com.Game.Protocol;

namespace Com.Sample.PushBox
{
    /// <summary>推箱子 Mod 入口：注册组件/系统、创建实体、声明消息契约，并暴露静态桥给本 Mod 的视图。</summary>
    public sealed class ModEntry : IMod
    {
        // 静态桥：供本 Mod 的视图（MonoBehaviour）访问框架服务（视图与入口同属一个 Mod 程序集）
        public static World World { get; private set; } = null!;
        public static SystemGroup Systems { get; private set; } = null!;
        public static MessageBus Messages { get; private set; } = null!;
        public static ModId ModId { get; private set; }

        public void OnLoad(IModContext ctx)
        {
            World = ctx.World;
            Systems = ctx.Systems;
            Messages = ctx.Messages;
            ModId = ctx.ModId;

            // 1. 注册组件（状态类型）
            ctx.World.RegisterComponent<Box>();
            ctx.World.RegisterComponent<Position>();
            ctx.World.RegisterComponent<Velocity>();
            ctx.World.RegisterComponent<Player>();
            ctx.World.RegisterComponent<PushIntent>();
            ctx.World.RegisterComponent<Session>();

            // 2. 注册系统（服务端权威逻辑）
            ctx.Systems.Add(new PushSystem(), SystemSide.Server);
            ctx.Systems.Add(new WinCheckSystem(), SystemSide.Server);

            // 3. 创建游戏实体
            var box = ctx.World.CreateEntity();
            ctx.World.Add(box, new Box());
            ctx.World.Add(box, new Position { X = 0 });
            ctx.World.Add(box, new Velocity { V = 0 });

            var left = ctx.World.CreateEntity();
            ctx.World.Add(left, new Player { Side = PlayerSide.Left });

            var right = ctx.World.CreateEntity();
            ctx.World.Add(right, new Player { Side = PlayerSide.Right });

            var session = ctx.World.CreateEntity();
            ctx.World.Add(session, new Session
            {
                Box = box.Id,
                Left = left.Id,
                Right = right.Id,
                Phase = GamePhase.Running,
                Winner = PlayerSide.Left,
            });

            // 4. 声明消息契约：处理玩家输入
            var leftId = left.Id;
            var rightId = right.Id;
            ctx.Messages.Handle<PushInputMsg>(ctx.ModId, msg =>
            {
                var player = msg.Side == PlayerSide.Left ? new Entity(leftId) : new Entity(rightId);
                if (msg.Pushing)
                    ctx.World.Add(player, new PushIntent
                    {
                        Direction = msg.Side == PlayerSide.Left ? 1f : -1f
                    });
                else
                    ctx.World.Remove<PushIntent>(player);
                return null;
            });

            // 5. 通过协议核心 Mod 注册协议解析器（为网络传输做准备）
            ProtocolEntry.Client.Register<PushInputMsg>(ctx.ModId, new PushInputCodec());
            ProtocolEntry.Client.Register<GameWonMsg>(ctx.ModId, new GameWonCodec());
            ProtocolEntry.Server.Register<PushInputMsg>(ctx.ModId, new PushInputCodec());
            ProtocolEntry.Server.Register<GameWonMsg>(ctx.ModId, new GameWonCodec());

            ctx.Log.Info($"推箱子 Mod '{ctx.ModId}' v{ctx.Version} 已加载");
        }
    }
}
