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

        public void Register(IModContext context)
        {
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
            ClientSeed = DefaultSeed;
        }

        /// <summary>确定性障碍物生成（LCG）：同 seed 两端生成完全一致。</summary>
        public static (float X, float Y, float Z, float Sx, float Sy, float Sz)[] GenerateBoxes(int seed)
        {
            var state = (uint)seed;
            var boxes = new (float, float, float, float, float, float)[10];
            for (var i = 0; i < boxes.Length; i++)
            {
                state = state * 1664525u + 1013904223u; // LCG
                var gx = ((state >> 8) % 40) - 20f;    // [-20, 20)
                state = state * 1664525u + 1013904223u;
                var gz = ((state >> 8) % 40) - 20f;
                // 避免落在出生点/靶标附近
                if (Math.Abs(gx) < 2f && Math.Abs(gz) < 2f) gx = 10f;
                state = state * 1664525u + 1013904223u;
                var s = 0.8f + ((state >> 8) % 20) * 0.1f; // 0.8 ~ 2.7
                boxes[i] = (gx, s / 2f, gz, s, s, s);
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
