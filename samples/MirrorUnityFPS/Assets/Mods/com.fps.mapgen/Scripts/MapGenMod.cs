using System;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace Com.Fps.MapGen
{
    /// <summary>程序化地图 Mod（com.fps.mapgen）：服务端 seed 权威，两端确定性生成同一布局（§12.9 模式 3 的地图版）。
    /// 障碍物为视觉层（碰撞/挡弹留待后续）；seed 同步协议替代全量地图数据复制。</summary>
    public sealed class MapGenMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.fps.mapgen");
        public static readonly int DefaultSeed = 12345;

        /// <summary>客户端收到的 seed（视图渲染数据源）。</summary>
        public static int ClientSeed { get; private set; } = DefaultSeed;

        /// <summary>静态桥（视图加载资源用）。</summary>
        public static IModContext Context { get; private set; } = null!;

        public void Register(IModContext context)
        {
            Context = context;
            ClientSeed = DefaultSeed;

            if (context.HasServer)
            {
                // 网络就绪后广播一次 seed
                context.Ecs.RegisterSystem(new SeedBroadcastSystem(context), SystemSide.Server);
            }
            if (context.HasClient)
            {
                context.Network.RegisterProtocol(new SeedProtocol(), new ClientSeedHandler());
            }
            context.Log.Info($"地图 Mod '{context.Info.Id}' v{context.Info.Version} 已注册（seed={DefaultSeed}）");
        }

        public void Unregister(IModContext context)
        {
            Context = null!;
            ClientSeed = DefaultSeed;
        }

        /// <summary>确定性障碍物生成（LCG）：同 seed 两端生成完全一致。
        /// 布局：周边建筑带（4 角 + 4 边中点围成 arena）+ 后方散落建筑（避开出生区），建筑尺寸 3~6m（真实结构感）。</summary>
        public static (float X, float Y, float Z, float Sx, float Sy, float Sz)[] GenerateBoxes(int seed)
        {
            var state = (uint)seed;
            var boxes = new (float, float, float, float, float, float)[12];
            var i = 0;

            // 周边建筑带（围合成 arena：四角 + 四边中点）
            var spots = new (float X, float Z)[]
            {
                (-16f, -16f), (-16f, 16f), (16f, -16f), (16f, 16f),
                (0f, -16f), (0f, 16f), (-16f, 0f), (16f, 0f),
            };
            foreach (var (ex, ez) in spots)
            {
                state = state * 1664525u + 1013904223u; // LCG
                var s = 3.5f + ((state >> 8) % 25) * 0.1f; // 3.5 ~ 6m
                boxes[i++] = (ex, s / 2f, ez, s, s, s);
            }

            // 后方散落建筑（z < -3 区域，避开出生点/靶标/NPC 的前方战场）
            while (i < boxes.Length)
            {
                state = state * 1664525u + 1013904223u;
                var gx = ((state >> 8) % 30) - 15f;   // [-15, 15)
                state = state * 1664525u + 1013904223u;
                var gz = ((state >> 8) % 12) - 14f;    // [-14, -3) 后方
                state = state * 1664525u + 1013904223u;
                var s = 3f + ((state >> 8) % 20) * 0.1f; // 3 ~ 5m
                boxes[i++] = (gx, s / 2f, gz, s, s, s);
            }
            return boxes;
        }

        /// <summary>seed 广播（网络就绪后一次）。</summary>
        private sealed class SeedBroadcastSystem : ISystem
        {
            private readonly IModContext _context;
            private bool _sent;

            public SeedBroadcastSystem(IModContext context) => _context = context;

            public void Update(SystemContext context)
            {
                if (_sent || !_context.Network.IsActive) return;
                _sent = true;
                _context.Network.Broadcast(new SeedProtocol().Id, new SeedMessage(DefaultSeed));
            }
        }

        private sealed class ClientSeedHandler : INetworkHandler
        {
            public void Handle(in NetworkContext context, in object message)
            {
                if (context.IsServer) return;
                ClientSeed = ((SeedMessage)message).Seed;
            }
        }
    }

    public readonly struct SeedMessage
    {
        public readonly int Seed;
        public SeedMessage(int seed) => Seed = seed;
    }

    public sealed class SeedProtocol : INetworkProtocol
    {
        public ProtocolId Id => ProtocolId.Of(MapGenMod.ModIdValue, "seed");
        public ushort Version => 1;
        public NetworkDirection Direction => NetworkDirection.ServerToClient;
        public int MaxSize => 16;

        public void Encode(in object message, global::Game.Mod.Contract.Wire.INetworkWriter w)
            => w.WriteInt32(1, ((SeedMessage)message).Seed);

        public object Decode(global::Game.Mod.Contract.Wire.INetworkReader r)
        {
            r.TryReadInt32(1, out var seed);
            return new SeedMessage(seed);
        }
    }
}
