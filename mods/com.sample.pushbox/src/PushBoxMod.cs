using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Sample.PushBox
{
    /// <summary>
    /// 推箱子 Mod 入口（IMod v2，Rule 1）：
    /// 注册 ECS / 网络协议 / 事件；Client/Server/Host 是 Context 能力（Rule 2）——
    /// Shared 逻辑 + HasServer 注册服务端权威系统与协议 Handler + HasClient 注册表现侧 Handler。
    /// </summary>
    public sealed class PushBoxMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.sample.pushbox");

        /// <summary>本地事件：游戏分出胜负（MessageBus 二进制事件，谁定义谁发布，Rule 11）。</summary>
        public static readonly MessageId GameWonEvent = new(ModIdValue, "game_won");

        // 静态桥：供本 Mod 的视图（MonoBehaviour）访问（视图与入口同属一个 Mod 程序集，Mod 内部状态）
        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;
        public static uint SessionEntityId { get; private set; }

        /// <summary>Client 侧最新胜负状态（由 game_won 协议 Handler 维护，Host 下本地回环获得）。</summary>
        public static PlayerSide? ClientWinner { get; private set; }

        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;
            ClientWinner = null;

            // 1. 注册组件（状态类型，归属本 ModObject）
            context.Ecs.RegisterComponent(typeof(Box));
            context.Ecs.RegisterComponent(typeof(Position));
            context.Ecs.RegisterComponent(typeof(Velocity));
            context.Ecs.RegisterComponent(typeof(Player));
            context.Ecs.RegisterComponent(typeof(PushIntent));
            context.Ecs.RegisterComponent(typeof(Session));

            // 2. 服务端权威系统（HasServer 环境，§6 能力判断）
            if (context.HasServer)
            {
                var winCheck = new WinCheckSystem();
                winCheck.OnGameWon = winner =>
                {
                    // 服务端判胜 → 广播给所有客户端（ServerToClient 协议，Rule 5）
                    context.Network.Broadcast(new GameWonProtocol().Id, new GameWon(winner));
                };
                context.Ecs.RegisterSystem(new PushSystem(), SystemSide.Server);
                context.Ecs.RegisterSystem(winCheck, SystemSide.Server);
            }

            // 3. 创建游戏实体（示例简化：真实游戏应在会话开始时由 Server 逻辑生成）
            if (context.HasServer)
            {
                var world = context.Ecs.World;
                var box = world.CreateEntity();
                world.Add(box, new Box());
                world.Add(box, new Position { X = 0 });
                world.Add(box, new Velocity { V = 0 });

                var left = world.CreateEntity();
                world.Add(left, new Player { Side = PlayerSide.Left });

                var right = world.CreateEntity();
                world.Add(right, new Player { Side = PlayerSide.Right });

                var session = world.CreateEntity();
                world.Add(session, new Session
                {
                    Box = box.Id,
                    Left = left.Id,
                    Right = right.Id,
                    Phase = GamePhase.Running,
                    Winner = PlayerSide.Left,
                });
                SessionEntityId = session.Id;

                // 4. 协议：玩家输入（ClientToServer，Handler 在 Server 侧应用 PushIntent）
                context.Network.RegisterProtocol(
                    new PushInputProtocol(),
                    new ServerInputHandler(world, left.Id, right.Id));
            }

            // 5. 协议：胜负广播（ServerToClient，Handler 在 Client 侧更新状态 + 发布本地事件）
            if (context.HasClient)
            {
                context.Network.RegisterProtocol(
                    new GameWonProtocol(),
                    new ClientGameWonHandler(context));
            }

            context.Log.Info($"推箱子 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            // 对称注销（协议/系统/订阅由 Core 兜底强制清理，§7 卸载流程）
            World = null!;
            Context = null!;
            SessionEntityId = 0;
            ClientWinner = null;
            context.Log.Info($"推箱子 Mod '{context.Info.Id}' 已注销");
        }

        /// <summary>发布本地 game_won 二进制事件（编码一次，订阅者共享只读视图，§14.12）。</summary>
        internal static void PublishGameWonEvent(IModContext context, PlayerSide winner)
        {
            var writer = context.Messages.CreateWriter();
            writer.WriteByte(1, (byte)winner);
            var buffer = writer.ToBuffer();
            context.Messages.Publish(GameWonEvent, 1, in buffer);
        }

        /// <summary>Server 侧输入处理：网络消息是输入，ECS 是权威状态（§11.10 规则）。</summary>
        private sealed class ServerInputHandler : INetworkHandler
        {
            private readonly World _world;
            private readonly uint _leftId;
            private readonly uint _rightId;

            public ServerInputHandler(World world, uint leftId, uint rightId)
            {
                _world = world;
                _leftId = leftId;
                _rightId = rightId;
            }

            public void Handle(in NetworkContext context, in object message)
            {
                var input = (PushInput)message;
                var player = new Entity(input.Side == PlayerSide.Left ? _leftId : _rightId);
                if (input.Pushing)
                {
                    _world.Add(player, new PushIntent
                    {
                        Direction = input.Side == PlayerSide.Left ? 1f : -1f,
                    });
                }
                else
                {
                    _world.Remove<PushIntent>(player);
                }
            }
        }

        /// <summary>Client 侧胜负处理：跨端数据落地为本地状态 + 发布本地事件（§12.9 模式 3）。</summary>
        private sealed class ClientGameWonHandler : INetworkHandler
        {
            private readonly IModContext _context;

            public ClientGameWonHandler(IModContext context) => _context = context;

            public void Handle(in NetworkContext context, in object message)
            {
                var won = (GameWon)message;
                ClientWinner = won.Winner;
                PublishGameWonEvent(_context, won.Winner);
            }
        }
    }
}
