using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Fps.Weapon
{
    /// <summary>
    /// 武器 Mod（com.fps.weapon）：射击 / 换弹 / 弹药 / 命中。
    /// 依赖 com.fps.player（Manifest 声明）——跨 Mod 一律经能力调用拿 BCL 快照（Rule 12/19）。
    /// </summary>
    public sealed class WeaponMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.fps.weapon");
        public static readonly MessageId ShotFiredEvent = new(ModIdValue, "shot");

        /// <summary>对端能力寻址（契约见 com.fps.player/CONTRACT.md）。</summary>
        public static readonly ModId PlayerModId = new("com.fps.player");
        public static readonly CapabilityId GetPositionCap = new(PlayerModId, "get_position");
        public static readonly CapabilityId GetAllPositionsCap = new(PlayerModId, "get_all_positions");
        public static readonly CapabilityId ApplyDamageCap = new(PlayerModId, "apply_damage");

        /// <summary>对端 NPC 能力（可选依赖；未加载则跳过 NPC 命中）。</summary>
        public static readonly ModId NpcModId = new("com.fps.npc");
        public static readonly CapabilityId NpcGetAllCap = new(NpcModId, "get_all_npcs");
        public static readonly CapabilityId NpcApplyDamageCap = new(NpcModId, "apply_damage");

        /// <summary>本 Mod 导出的能力（供拾取/控制台等）。</summary>
        public static readonly CapabilityId AddAmmoCap = new(ModIdValue, "add_ammo");
        public static readonly CapabilityId CmdGiveAmmoCap = new(ModIdValue, "cmd_give_ammo");

        // 静态桥（视图访问，Mod 内部状态）
        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;

        /// <summary>客户端最近开火事件（弹道表现驱动，协议 Handler 落地）。</summary>
        public static ShotEvent? LastShot { get; private set; }
        public static long LastShotTicks { get; private set; }

        /// <summary>客户端武器状态缓存：netId → 状态（HUD 数据源）。</summary>
        public static readonly System.Collections.Generic.Dictionary<uint, WeaponStateSync> ClientWeaponStates = new();

        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;
            LastShot = null;
            ClientWeaponStates.Clear();

            context.Ecs.RegisterComponent(typeof(WeaponState));
            context.Ecs.RegisterComponent(typeof(FireIntent));

            context.Mods.Export(AddAmmoCap, reader =>
                DataCodec.Write(new object?[] { AddAmmo(DataCodec.Read(reader)) }));
            context.Mods.Export(CmdGiveAmmoCap, reader =>
                DataCodec.Write(new object?[] { AddAmmo(new object?[] { GetLocalShooterEntityId(context), 15 }) }));

            // 控制台命令注册（可选依赖；经 console:register 能力，零跨 Mod 类型引用）
            var consoleId = new ModId("com.fps.console");
            if (context.Mods.IsLoaded(consoleId))
            {
                context.Mods.Call(consoleId, new CapabilityId(consoleId, "register"),
                    DataCodec.Write(new object?[] { "give_ammo", ModIdValue.Value, CmdGiveAmmoCap.Id.ToString() }));
            }

            if (context.HasServer)
            {
                context.Ecs.RegisterSystem(new WeaponSystem(context), SystemSide.Server);
                context.Network.RegisterProtocol(new FireProtocol(), new FireIntentHandler(context));
                context.Network.RegisterProtocol(new ReloadProtocol(), new ReloadHandler(context));
            }

            if (context.HasClient)
            {
                context.Network.RegisterProtocol(new ShotProtocol(), new ClientShotHandler());
                context.Network.RegisterProtocol(new StateProtocol(), new ClientStateHandler());
            }

            context.Log.Info($"武器 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            World = null!;
            Context = null!;
            LastShot = null;
            ClientWeaponStates.Clear();
            context.Log.Info($"武器 Mod '{context.Info.Id}' 已注销");
        }

        /// <summary>补弹：args=[target u32, amount i32]，懒挂 WeaponState 后加弹（§12.9）。</summary>
        private static bool AddAmmo(object? args)
        {
            if (args is not object[] a || a.Length < 2 || a[0] is not uint target || a[1] is not int amount)
                return false;
            var e = new Entity(target);
            if (!World.TryGet<WeaponState>(e, out var state))
            {
                state = new WeaponState { Ammo = 0, MaxAmmo = WeaponConfig.MaxAmmo };
            }
            state.Ammo = System.Math.Min(state.MaxAmmo, state.Ammo + amount);
            World.Add(e, state);
            return true;
        }

        /// <summary>取本地射手实体（经能力调用，契约：args=null 返回本地玩家行）。</summary>
        public static uint GetLocalShooterEntityId(IModContext context)
        {
            var buf = context.Mods.Call(PlayerModId, GetPositionCap, DataCodec.Write(null));
            var row = DataCodec.Read(new PayloadReader(buf));
            return row.Length >= 1 && row[0] is uint id ? id : 0u;
        }

        // ---- 协议 Handler ----

        /// <summary>服务端：开火意图落地（网络消息是输入，ECS 是权威状态）。</summary>
        private sealed class FireIntentHandler : INetworkHandler
        {
            private readonly IModContext _context;
            public FireIntentHandler(IModContext context) => _context = context;

            public void Handle(in NetworkContext context, in object message)
            {
                var shooterId = GetLocalShooterEntityId(_context);
                if (shooterId == 0) return;
                var shooter = new Entity(shooterId);
                if (!_context.Ecs.World.Exists(shooter)) return;
                _context.Ecs.World.Add(shooter, new FireIntent { Yaw = ((FireRequest)message).Yaw });
            }
        }

        /// <summary>服务端：换弹请求落地。</summary>
        private sealed class ReloadHandler : INetworkHandler
        {
            private readonly IModContext _context;
            public ReloadHandler(IModContext context) => _context = context;

            public void Handle(in NetworkContext context, in object message)
            {
                var shooterId = GetLocalShooterEntityId(_context);
                if (shooterId == 0) return;
                var shooter = new Entity(shooterId);
                if (!_context.Ecs.World.TryGet<WeaponState>(shooter, out var state)) return;
                if (state.Ammo >= state.MaxAmmo || state.ReloadT > 0f) return;
                state.ReloadT = WeaponConfig.ReloadDuration;
                _context.Ecs.World.Add(shooter, state);
                _context.Network.Broadcast(new StateProtocol().Id,
                    new WeaponStateSync(
                        _context.Network.Replication.TryGetNetworkId(shooter, out var netId) ? netId : 0u,
                        state.Ammo, state.MaxAmmo, true));
            }
        }

        /// <summary>客户端：开火事件落地（驱动弹道线/命中反馈）。</summary>
        private sealed class ClientShotHandler : INetworkHandler
        {
            public void Handle(in NetworkContext context, in object message)
            {
                if (context.IsServer) return;
                LastShot = (ShotEvent)message;
                LastShotTicks = System.DateTime.UtcNow.Ticks;
            }
        }

        /// <summary>客户端：武器状态落地（HUD 缓存）。</summary>
        private sealed class ClientStateHandler : INetworkHandler
        {
            public void Handle(in NetworkContext context, in object message)
            {
                if (context.IsServer) return;
                var sync = (WeaponStateSync)message;
                ClientWeaponStates[sync.NetId] = sync;
            }
        }
    }
}
