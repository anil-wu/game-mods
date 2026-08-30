using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace Com.Fps.Player
{
    /// <summary>
    /// 能力 ABI 形状（Rule 19 进程内务实形态）：跨 Mod 参数/返回只用 BCL 纯数据——
    /// 基元 + object[] 元组（字段布局即契约，见 CONTRACT.md），双方各自按文档解析，
    /// 零自定义类型共享；跨信任边界时同一布局走字节流（§14.11）。
    /// </summary>
    public static class CapabilityShapes
    {
        /// <summary>PositionDto 行：[0]=EntityId(u32) [1]=X(f32) [2]=Y(f32) [3]=Z(f32) [4]=Yaw(f32) [5]=Alive(bool)。</summary>
        public static object[] PositionRow(uint entityId, float x, float y, float z, float yaw, bool alive) =>
            new object[] { entityId, x, y, z, yaw, alive };

        /// <summary>DamageArgs：[0]=Target(u32) [1]=Amount(i32) [2]=Source(u32)。</summary>
        public static bool TryReadDamage(object? args, out uint target, out int amount, out uint source)
        {
            target = 0; amount = 0; source = 0;
            if (args is not object[] a || a.Length < 3) return false;
            if (a[0] is not uint t || a[1] is not int amt || a[2] is not uint s) return false;
            target = t; amount = amt; source = s;
            return true;
        }
    }

    public delegate object? GetPositionHandler(object? args);
    public delegate object[] GetAllPositionsHandler(object? args);
    public delegate bool ApplyDamageHandler(object? args);

    /// <summary>
    /// 玩家 Mod（com.fps.player）：移动 / 生命 / 生成 / 复制。
    /// 服务端权威：输入 C2S → MovementSystem 积分 → Replication 回客户端（§11.10）。
    /// </summary>
    public sealed class PlayerMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.fps.player");
        public static readonly ArchetypeId PlayerArchetype = new(ModIdValue, "player");
        public static readonly MessageId DiedEvent = new(ModIdValue, "died");

        // 能力（§12.11，契约见 CONTRACT.md）
        public static readonly CapabilityId GetPositionCap = new(ModIdValue, "get_position");
        public static readonly CapabilityId GetAllPositionsCap = new(ModIdValue, "get_all_positions");
        public static readonly CapabilityId ApplyDamageCap = new(ModIdValue, "apply_damage");

        // 静态桥（视图访问，Mod 内部状态）
        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;
        public static uint LocalPlayerNetId { get; private set; }
        public static uint LocalPlayerEntityId { get; private set; }

        private static readonly (float X, float Y, float Z)[] SpawnPoints =
        {
            (0, PlayerConfig.GroundY, 0),   // 本地玩家
            (-3, PlayerConfig.GroundY, 5),  // 靶标 A
            (3, PlayerConfig.GroundY, 5),   // 靶标 B
        };

        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;
            LocalPlayerNetId = 0;
            LocalPlayerEntityId = 0;

            context.Ecs.RegisterComponent(typeof(Position3));
            context.Ecs.RegisterComponent(typeof(Velocity3));
            context.Ecs.RegisterComponent(typeof(PlayerInput));
            context.Ecs.RegisterComponent(typeof(Health));
            context.Ecs.RegisterComponent(typeof(PlayerTag));
            context.Ecs.RegisterComponent(typeof(RespawnTimer));

            // 复制 Archetype 双端注册（§11.10：Spawn/Delta 共用同一套 Codec，Rule 14）
            context.Network.Replication.RegisterArchetype(PlayerArchetype,
                new IComponentCodec[] { new Position3Codec(), new HealthCodec(), new PlayerTagCodec() });

            // 能力导出（§12.11）
            context.Mods.Export(GetPositionCap, new GetPositionHandler(GetPosition));
            context.Mods.Export(GetAllPositionsCap, new GetAllPositionsHandler(_ => GetAllPositions()));
            context.Mods.Export(ApplyDamageCap, new ApplyDamageHandler(ApplyDamage));

            if (context.HasServer)
            {
                context.Ecs.RegisterSystem(new MovementSystem(), SystemSide.Server);
                context.Ecs.RegisterSystem(new RespawnSystem(SpawnPointOf), SystemSide.Server);

                // 生成：本地玩家 + 2 个静态靶标
                var localNetId = SpawnPlayer(context, 0, isDummy: false);
                SpawnPlayer(context, 1, isDummy: true);
                SpawnPlayer(context, 2, isDummy: true);
                context.Ecs.RegisterSystem(new AssignLocalSystem(context, localNetId), SystemSide.Server);

                // 服务端输入落地
                context.Network.RegisterProtocol(new InputProtocol(), new ServerInputHandler(World));
            }

            if (context.HasClient)
            {
                context.Network.RegisterProtocol(new LocalProtocol(), new LocalAssignHandler());
            }

            context.Log.Info($"玩家 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            World = null!;
            Context = null!;
            LocalPlayerNetId = 0;
            LocalPlayerEntityId = 0;
            context.Log.Info($"玩家 Mod '{context.Info.Id}' 已注销");
        }

        // ---- 生成 ----

        private static uint SpawnPlayer(IModContext context, int spawnIndex, bool isDummy)
        {
            var (x, y, z) = SpawnPoints[spawnIndex];
            var e = context.Ecs.CreateEntity(); // 归属本 ModObject，卸载销毁（§9.2）
            context.Ecs.World.Add(e, new Position3 { X = x, Y = y, Z = z });
            context.Ecs.World.Add(e, new Velocity3());
            context.Ecs.World.Add(e, new Health { Current = PlayerConfig.MaxHealth, Max = PlayerConfig.MaxHealth });
            context.Ecs.World.Add(e, new PlayerTag { IsDummy = isDummy });
            if (!isDummy)
            {
                context.Ecs.World.Add(e, new PlayerInput());
                LocalPlayerEntityId = e.Id;
            }
            var netId = context.Network.Replication.SpawnReplicated(e, PlayerArchetype);
            if (!isDummy) LocalPlayerNetId = netId;
            return netId;
        }

        internal static (float X, float Y, float Z) SpawnPointOf(uint entityId) =>
            entityId == LocalPlayerEntityId ? SpawnPoints[0] : SpawnPoints[1];

        // ---- 能力实现 ----

        private static object? GetPosition(object? args)
        {
            var entityId = args is uint id ? id : LocalPlayerEntityId;
            var e = new Entity(entityId);
            if (!World.TryGet<Position3>(e, out var pos)) return null;
            var yaw = World.TryGet<PlayerInput>(e, out var input) ? input.Yaw : 0f;
            var alive = World.TryGet<Health>(e, out var hp) && hp.Current > 0;
            return CapabilityShapes.PositionRow(entityId, pos.X, pos.Y, pos.Z, yaw, alive);
        }

        private static object[] GetAllPositions()
        {
            var list = new System.Collections.Generic.List<object[]>();
            foreach (var (entityId, pos) in World.Store<Position3>().All())
            {
                var e = new Entity(entityId);
                if (World.Has<ReplicaTag>(e)) continue; // 服务端权威视角：排除客户端副本（§11.10）
                var yaw = World.TryGet<PlayerInput>(e, out var input) ? input.Yaw : 0f;
                var alive = World.TryGet<Health>(e, out var hp) && hp.Current > 0;
                list.Add(CapabilityShapes.PositionRow(entityId, pos.X, pos.Y, pos.Z, yaw, alive));
            }
            return list.ToArray();
        }

        private static bool ApplyDamage(object? args)
        {
            if (!CapabilityShapes.TryReadDamage(args, out var target, out var amount, out var source))
                return false;
            var e = new Entity(target);
            if (!World.TryGet<Health>(e, out var hp) || hp.Current <= 0) return false;

            hp.Current = System.Math.Max(0, hp.Current - amount);
            World.Add(e, hp);

            if (hp.Current > 0) return false;

            // 死亡：发布事件（谁定义谁发布，Rule 11）+ 挂重生计时
            World.Add(e, new RespawnTimer { T = PlayerConfig.RespawnDelay });
            var writer = Context.Messages.CreateWriter();
            writer.WriteUInt32(1, target);
            writer.WriteUInt32(2, source);
            var buffer = writer.ToBuffer();
            Context.Messages.Publish(DiedEvent, 1, in buffer);
            return true;
        }

        // ---- 协议 Handler ----

        /// <summary>服务端输入落地：网络消息是输入，ECS 是权威状态（§11.10 规则）。</summary>
        private sealed class ServerInputHandler : INetworkHandler
        {
            private readonly World _world;

            public ServerInputHandler(World world) => _world = world;

            public void Handle(in NetworkContext context, in object message)
            {
                var input = (InputState)message;
                var player = new Entity(LocalPlayerEntityId);
                if (!_world.Exists(player)) return;
                _world.Add(player, new PlayerInput
                {
                    MoveX = input.MoveX,
                    MoveZ = input.MoveZ,
                    Yaw = input.Yaw,
                    Jump = input.Jump,
                });
            }
        }

        /// <summary>客户端：记录本地玩家 NetworkId（驱动相机与 HUD 归属）。</summary>
        private sealed class LocalAssignHandler : INetworkHandler
        {
            public void Handle(in NetworkContext context, in object message)
            {
                if (context.IsServer) return;
                LocalPlayerNetId = ((LocalAssigned)message).NetworkId;
            }
        }
    }
}
