using System;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Fps.Inventory
{
    /// <summary>物品类型。</summary>
    public enum ItemKind : byte { Health = 0, Ammo = 1 }

    // ---- 组件（本 Mod 自有类型；复制，§11.10） ----

    public struct ItemPosition3 : IComponent { public float X, Y, Z; }
    public struct ItemTag : IComponent { public ItemKind Kind; public int Amount; }

    public sealed class ItemPosition3Codec : IComponentCodec
    {
        public Type ComponentType => typeof(ItemPosition3);
        public void Write(object boxed, INetworkWriter w)
        {
            var p = (ItemPosition3)boxed;
            w.WriteFloat(1, p.X); w.WriteFloat(2, p.Y); w.WriteFloat(3, p.Z);
        }
        public object Read(INetworkReader r)
        {
            r.TryReadFloat(1, out var x); r.TryReadFloat(2, out var y); r.TryReadFloat(3, out var z);
            return new ItemPosition3 { X = x, Y = y, Z = z };
        }
    }

    public sealed class ItemTagCodec : IComponentCodec
    {
        public Type ComponentType => typeof(ItemTag);
        public void Write(object boxed, INetworkWriter w)
        {
            var t = (ItemTag)boxed;
            w.WriteByte(1, (byte)t.Kind);
            w.WriteInt32(2, t.Amount);
        }
        public object Read(INetworkReader r)
        {
            r.TryReadByte(1, out var kind); r.TryReadInt32(2, out var amount);
            return new ItemTag { Kind = (ItemKind)kind, Amount = amount };
        }
    }

    // ---- 协议载荷 ----

    public readonly struct PickupRequest { public readonly uint NetId; public PickupRequest(uint netId) => NetId = netId; }
    public readonly struct PickedEvent
    {
        public readonly uint NetId; public readonly ItemKind Kind; public readonly int Amount;
        public PickedEvent(uint netId, ItemKind kind, int amount) { NetId = netId; Kind = kind; Amount = amount; }
    }

    public abstract class InventoryProtocolBase : INetworkProtocol
    {
        public abstract string Path { get; }
        public ProtocolId Id => ProtocolId.Of(InventoryMod.ModIdValue, Path);
        public ushort Version => 1;
        public abstract NetworkDirection Direction { get; }
        public int MaxSize => 64;
        public abstract void Encode(in object message, INetworkWriter writer);
        public abstract object Decode(INetworkReader reader);
    }

    public sealed class PickupProtocol : InventoryProtocolBase
    {
        public override string Path => "pickup";
        public override NetworkDirection Direction => NetworkDirection.ClientToServer;
        public override void Encode(in object message, INetworkWriter w) => w.WriteUInt32(1, ((PickupRequest)message).NetId);
        public override object Decode(INetworkReader r)
        {
            r.TryReadUInt32(1, out var netId);
            return new PickupRequest(netId);
        }
    }

    public sealed class PickedProtocol : InventoryProtocolBase
    {
        public override string Path => "picked";
        public override NetworkDirection Direction => NetworkDirection.ServerToClient;
        public override void Encode(in object message, INetworkWriter w)
        {
            var m = (PickedEvent)message;
            w.WriteUInt32(1, m.NetId); w.WriteByte(2, (byte)m.Kind); w.WriteInt32(3, m.Amount);
        }
        public override object Decode(INetworkReader r)
        {
            r.TryReadUInt32(1, out var netId); r.TryReadByte(2, out var kind); r.TryReadInt32(3, out var amount);
            return new PickedEvent(netId, (ItemKind)kind, amount);
        }
    }

    /// <summary>
    /// 拾取/背包 Mod（com.fps.inventory）：世界拾取物（治疗包/弹药箱），E 拾取即时生效。
    /// 效果经能力调用施加（player:heal / weapon:add_ammo，§12.11）；拖拽背包 UI 待 UI.Mod 接 UGUI（阶段遗留）。
    /// </summary>
    public sealed class InventoryMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.fps.inventory");
        public static readonly ArchetypeId ItemArchetype = new(ModIdValue, "item");

        public static readonly ModId PlayerModId = new("com.fps.player");
        public static readonly CapabilityId GetPositionCap = new(PlayerModId, "get_position");
        public static readonly CapabilityId HealCap = new(PlayerModId, "heal");
        public static readonly ModId WeaponModId = new("com.fps.weapon");
        public static readonly CapabilityId AddAmmoCap = new(WeaponModId, "add_ammo");

        // 静态桥
        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;
        public static PickedEvent? LastPicked { get; private set; }
        public static long LastPickedTicks { get; private set; }
        public static readonly System.Collections.Generic.Dictionary<ItemKind, int> Collected = new();

        private static readonly (float X, float Y, float Z, ItemKind Kind, int Amount)[] Spawns =
        {
            (-5f, 0.5f, 3f, ItemKind.Health, 40),
            (5f, 0.5f, 3f, ItemKind.Ammo, 15),
            (0f, 0.5f, 8f, ItemKind.Health, 40),
        };

        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;
            LastPicked = null;
            Collected.Clear();

            context.Ecs.RegisterComponent(typeof(ItemPosition3));
            context.Ecs.RegisterComponent(typeof(ItemTag));
            context.Network.Replication.RegisterArchetype(ItemArchetype,
                new IComponentCodec[] { new ItemPosition3Codec(), new ItemTagCodec() });

            if (context.HasServer)
            {
                foreach (var (x, y, z, kind, amount) in Spawns)
                {
                    var e = context.Ecs.CreateEntity();
                    context.Ecs.World.Add(e, new ItemPosition3 { X = x, Y = y, Z = z });
                    context.Ecs.World.Add(e, new ItemTag { Kind = kind, Amount = amount });
                    context.Network.Replication.SpawnReplicated(e, ItemArchetype);
                }
                context.Network.RegisterProtocol(new PickupProtocol(), new PickupHandler(context));
            }
            if (context.HasClient)
                context.Network.RegisterProtocol(new PickedProtocol(), new ClientPickedHandler());

            context.Log.Info($"拾取 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            World = null!; Context = null!; LastPicked = null; Collected.Clear();
        }

        /// <summary>本地玩家实体（经能力调用，零跨 Mod 类型引用）。</summary>
        public static uint LocalPlayerEntityId()
        {
            var buf = Context.Mods.Call(PlayerModId, GetPositionCap, DataCodec.Write(null));
            var row = DataCodec.Read(new PayloadReader(buf));
            return row.Length >= 1 && row[0] is uint id ? id : 0u;
        }

        /// <summary>服务端拾取：校验存在/距离 → 施加效果 → 广播 → 销毁拾取物。</summary>
        private sealed class PickupHandler : INetworkHandler
        {
            private readonly IModContext _context;
            public PickupHandler(IModContext context) => _context = context;

            public void Handle(in NetworkContext context, in object message)
            {
                var netId = ((PickupRequest)message).NetId;
                // 服务端判定：用权威实体（TryGetEntity 副本优先，不可用于服务端逻辑）
                if (!_context.Network.Replication.TryGetServerEntity(netId, out var itemEntity)) return;
                var world = _context.Ecs.World;
                if (!world.TryGet<ItemTag>(itemEntity, out var tag)) return;

                // 拾取者（本地玩家）位置校验（2.5m 内）
                var pickerId = LocalPlayerEntityId();
                if (pickerId == 0) return;
                var pickerBuf = _context.Mods.Call(PlayerModId, GetPositionCap, DataCodec.Write(new object?[] { pickerId }));
                var pickerRow = DataCodec.Read(new PayloadReader(pickerBuf));
                if (pickerRow.Length < 4) return;
                var px = (float)pickerRow[1]!; var py = (float)pickerRow[2]!; var pz = (float)pickerRow[3]!;
                var pos = world.Get<ItemPosition3>(itemEntity);
                var dx = pos.X - px; var dy = pos.Y - py; var dz = pos.Z - pz;
                if (dx * dx + dy * dy + dz * dz > 2.5f * 2.5f) return;

                // 施加效果（跨 Mod 能力调用）
                if (tag.Kind == ItemKind.Health)
                    _context.Mods.Call(PlayerModId, HealCap, DataCodec.Write(new object?[] { pickerId, tag.Amount }));
                else
                    _context.Mods.Call(WeaponModId, AddAmmoCap, DataCodec.Write(new object?[] { pickerId, tag.Amount }));

                _context.Network.Broadcast(new PickedProtocol().Id,
                    new PickedEvent(netId, tag.Kind, tag.Amount));
                _context.Network.Replication.DespawnReplicated(itemEntity);
                world.Destroy(itemEntity);
            }
        }

        /// <summary>客户端：拾取反馈落地（HUD/计数）。</summary>
        private sealed class ClientPickedHandler : INetworkHandler
        {
            public void Handle(in NetworkContext context, in object message)
            {
                if (context.IsServer) return;
                var picked = (PickedEvent)message;
                LastPicked = picked;
                LastPickedTicks = DateTime.UtcNow.Ticks;
                Collected[picked.Kind] = (Collected.TryGetValue(picked.Kind, out var n) ? n : 0) + 1;
            }
        }
    }
}
