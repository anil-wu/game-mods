using System;
using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Fps.Npc
{
    // ---- 组件（本 Mod 自有；复制） ----

    public struct NpcPosition3 : IComponent { public float X, Y, Z; }
    public struct NpcHealth : IComponent { public int Current; public int Max; }
    /// <summary>AI 内部状态（不复制）：State 0=待机 1=追击 2=攻击；Target 目标实体；Timer 攻击/重生计时。</summary>
    public struct NpcBrain : IComponent { public byte State; public uint Target; public float Timer; }
    /// <summary>服务端内部：最近一次受击来源（死亡事件携带）。</summary>
    public struct NpcHit : IComponent { public uint Source; }

    public sealed class NpcPosition3Codec : IComponentCodec
    {
        public Type ComponentType => typeof(NpcPosition3);
        public void Write(object boxed, INetworkWriter w)
        {
            var p = (NpcPosition3)boxed;
            w.WriteFloat(1, p.X); w.WriteFloat(2, p.Y); w.WriteFloat(3, p.Z);
        }
        public object Read(INetworkReader r)
        {
            r.TryReadFloat(1, out var x); r.TryReadFloat(2, out var y); r.TryReadFloat(3, out var z);
            return new NpcPosition3 { X = x, Y = y, Z = z };
        }
    }

    public sealed class NpcHealthCodec : IComponentCodec
    {
        public Type ComponentType => typeof(NpcHealth);
        public void Write(object boxed, INetworkWriter w)
        {
            var h = (NpcHealth)boxed;
            w.WriteInt32(1, h.Current); w.WriteInt32(2, h.Max);
        }
        public object Read(INetworkReader r)
        {
            r.TryReadInt32(1, out var c); r.TryReadInt32(2, out var m);
            return new NpcHealth { Current = c, Max = m };
        }
    }

    /// <summary>NPC 快照行（能力返回）：[0]=EntityId [1]=X [2]=Y [3]=Z [4]=Alive。</summary>
    public readonly struct NpcSnapshot
    {
        public readonly uint EntityId;
        public readonly float X, Y, Z;
        public readonly bool Alive;

        public NpcSnapshot(uint entityId, float x, float y, float z, bool alive)
        {
            EntityId = entityId; X = x; Y = y; Z = z; Alive = alive;
        }

        public object[] Row() => new object[] { EntityId, X, Y, Z, Alive };

        public static bool TryRead(object? row, out NpcSnapshot snap)
        {
            snap = default;
            if (row is not object[] a || a.Length < 5) return false;
            if (a[0] is not uint id || a[1] is not float x || a[2] is not float y ||
                a[3] is not float z || a[4] is not bool alive) return false;
            snap = new NpcSnapshot(id, x, y, z, alive);
            return true;
        }
    }

    /// <summary>
    /// NPC Mod（com.fps.npc）：追击/攻击玩家的敌对 NPC，服务端 AI 状态机 + 复制。
    /// 跨 Mod 一律能力调用（player:get_all_positions / player:apply_damage）。
    /// </summary>
    public sealed class NpcMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.fps.npc");
        public static readonly ArchetypeId NpcArchetype = new(ModIdValue, "npc");
        public static readonly MessageId DiedEvent = new(ModIdValue, "died");

        public static readonly ModId PlayerModId = new("com.fps.player");
        public static readonly CapabilityId GetAllPositionsCap = new(PlayerModId, "get_all_positions");
        public static readonly CapabilityId ApplyDamageCap = new(PlayerModId, "apply_damage");

        // 导出能力（供 weapon 命中 NPC）
        public static readonly CapabilityId GetAllNpcsCap = new(ModIdValue, "get_all_npcs");
        public static readonly CapabilityId ApplyDamageCapSelf = new(ModIdValue, "apply_damage");

        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;

        public static readonly float AggroRange = 8f;
        public static readonly float AttackRange = 1.6f;
        public static readonly float MoveSpeed = 2.6f;
        public static readonly int AttackDamage = 15;
        public static readonly float AttackInterval = 1.0f;
        public static readonly float RespawnDelay = 5f;

        private static readonly (float X, float Y, float Z)[] Spawns =
        {
            (-6f, 0.5f, 6f), (6f, 0.5f, 6f),
        };


        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;

            context.Ecs.RegisterComponent(typeof(NpcPosition3));
            context.Ecs.RegisterComponent(typeof(NpcHealth));
            context.Ecs.RegisterComponent(typeof(NpcBrain));
            context.Ecs.RegisterComponent(typeof(NpcHit));
            context.Network.Replication.RegisterArchetype(NpcArchetype,
                new IComponentCodec[] { new NpcPosition3Codec(), new NpcHealthCodec() });

            context.Mods.Export(GetAllNpcsCap, reader => DataCodec.Write(GetAllNpcs()));
            context.Mods.Export(ApplyDamageCapSelf, reader =>
                DataCodec.Write(new object?[] { NpcApplyDamage(DataCodec.Read(reader)) }));

            if (context.HasServer)
            {
                context.Ecs.RegisterSystem(new NpcAiSystem(context), SystemSide.Server);
                foreach (var (x, y, z) in Spawns) SpawnNpc(context, x, y, z);
            }

            context.Log.Info($"NPC Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            World = null!;
            Context = null!;
        }

        private static void SpawnNpc(IModContext context, float x, float y, float z)
        {
            var e = context.Ecs.CreateEntity();
            context.Ecs.World.Add(e, new NpcPosition3 { X = x, Y = y, Z = z });
            context.Ecs.World.Add(e, new NpcHealth { Current = 100, Max = 100 });
            context.Ecs.World.Add(e, new NpcBrain { State = 0, Target = 0, Timer = 0f });
            context.Network.Replication.SpawnReplicated(e, NpcArchetype);
        }

        /// <summary>能力：全部 NPC 快照（object[] 行集，契约见 CONTRACT.md）。</summary>
        internal static object[] GetAllNpcs()
        {
            var list = new System.Collections.Generic.List<object[]>();
            foreach (var (entityId, pos, hp) in IterateNpcs())
                list.Add(new NpcSnapshot(entityId, pos.X, pos.Y, pos.Z, hp.Current > 0).Row());
            return list.ToArray();
        }

        /// <summary>能力：对 NPC 施加伤害，返回是否致死。</summary>
        internal static bool NpcApplyDamage(object? args)
        {
            if (args is not object[] a || a.Length < 3 || a[0] is not uint target ||
                a[1] is not int amount || a[2] is not uint source) return false;
            var e = new Entity(target);
            if (!World.TryGet<NpcHealth>(e, out var hp) || hp.Current <= 0) return false;
            hp.Current = Math.Max(0, hp.Current - amount);
            World.Add(e, hp);
            if (hp.Current > 0) return false;

            World.Add(e, new NpcHit { Source = source });
            var writer = Context.Messages.CreateWriter();
            writer.WriteUInt32(1, target);
            writer.WriteUInt32(2, source);
            var buffer = writer.ToBuffer();
            Context.Messages.Publish(DiedEvent, 1, in buffer);
            return true;
        }

        internal static IEnumerable<(uint, NpcPosition3, NpcHealth)> IterateNpcs()
        {
            foreach (var (id, pos) in World.Store<NpcPosition3>().All())
            {
                var e = new Entity(id);
                if (World.Has<ReplicaTag>(e)) continue;
                if (World.TryGet<NpcHealth>(e, out var hp))
                    yield return (id, pos, hp);
            }
        }
    }

    /// <summary>NPC AI 系统（简化行为树：待机→追击→攻击，死亡重生）。</summary>
    public sealed class NpcAiSystem : ISystem
    {
        private readonly IModContext _context;

        public NpcAiSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            var players = GetAllPlayers();
            foreach (var (entityId, pos, hp, brain) in NpcIteration())
            {
                var e = new Entity(entityId);
                var p = pos;
                var h = hp;
                var b = brain;

                if (h.Current <= 0)
                {
                    b.State = 0;
                    b.Timer -= ctx.DeltaTime;
                    if (b.Timer <= 0f)
                    {
                        h.Current = h.Max;
                        var (sx, sy, sz) = NpcSpawnPoint(entityId);
                        p.X = sx; p.Y = sy; p.Z = sz;
                        b.Target = 0;
                    }
                    ctx.World.Add(e, p);
                    ctx.World.Add(e, h);
                    ctx.World.Add(e, b);
                    continue;
                }

                // 找最近存活玩家
                uint target = 0;
                var best = float.MaxValue;
                foreach (var pl in players)
                {
                    if (!pl.Alive) continue;
                    var dx = pl.X - p.X; var dy = pl.Y - p.Y; var dz = pl.Z - p.Z;
                    var d = dx * dx + dy * dy + dz * dz;
                    if (d < best) { best = d; target = pl.EntityId; }
                }

                b.Target = target;
                if (target == 0 || best > NpcMod.AggroRange * NpcMod.AggroRange)
                {
                    b.State = 0; // 待机
                }
                else if (best <= NpcMod.AttackRange * NpcMod.AttackRange)
                {
                    b.State = 2; // 攻击
                    b.Timer -= ctx.DeltaTime;
                    if (b.Timer <= 0f)
                    {
                        b.Timer = NpcMod.AttackInterval;
                        _context.Mods.Call(NpcMod.PlayerModId, NpcMod.ApplyDamageCap,
                            DataCodec.Write(new object?[] { target, NpcMod.AttackDamage, entityId }));
                    }
                }
                else
                {
                    b.State = 1; // 追击
                    var tgt = FindPlayer(players, target);
                    if (tgt.HasValue)
                    {
                        var dx = tgt.Value.X - p.X;
                        var dz = tgt.Value.Z - p.Z;
                        var len = (float)Math.Sqrt(dx * dx + dz * dz);
                        if (len > 0.001f)
                        {
                            p.X += dx / len * NpcMod.MoveSpeed * ctx.DeltaTime;
                            p.Z += dz / len * NpcMod.MoveSpeed * ctx.DeltaTime;
                        }
                    }
                }

                ctx.World.Add(e, p);
                ctx.World.Add(e, b);
            }
        }

        private static (float, float, float) NpcSpawnPoint(uint entityId)
        {
            // 简化：固定两个出生点轮换（按实体 id 奇偶）
            return entityId % 2 == 0 ? (-6f, 0.5f, 6f) : (6f, 0.5f, 6f);
        }

        private (float X, float Y, float Z)? FindPlayer(PlayerSnap[] players, uint target)
        {
            foreach (var p in players)
                if (p.EntityId == target) return (p.X, p.Y, p.Z);
            return null;
        }

        private IEnumerable<(uint, NpcPosition3, NpcHealth, NpcBrain)> NpcIteration()
        {
            var world = _context.Ecs.World;
            foreach (var (id, pos) in world.Store<NpcPosition3>().All())
            {
                var e = new Entity(id);
                if (world.Has<ReplicaTag>(e)) continue;
                if (world.TryGet<NpcHealth>(e, out var hp) && world.TryGet<NpcBrain>(e, out var brain))
                    yield return (id, pos, hp, brain);
            }
        }

        /// <summary>玩家快照（BCL 形状解析，见 player CONTRACT.md）。</summary>
        private readonly struct PlayerSnap
        {
            public readonly uint EntityId;
            public readonly float X, Y, Z;
            public readonly bool Alive;
            private PlayerSnap(uint id, float x, float y, float z, bool alive)
            {
                EntityId = id; X = x; Y = y; Z = z; Alive = alive;
            }
            public static bool TryRead(object? row, out PlayerSnap snap)
            {
                snap = default;
                if (row is not object[] a || a.Length < 6) return false;
                if (a[0] is not uint id || a[1] is not float x || a[2] is not float y ||
                    a[3] is not float z || a[5] is not bool alive) return false;
                snap = new PlayerSnap(id, x, y, z, alive);
                return true;
            }
        }

        private PlayerSnap[] GetAllPlayers()
        {
            var buf = _context.Mods.Call(NpcMod.PlayerModId, NpcMod.GetAllPositionsCap, DataCodec.Write(null));
            var raw = DataCodec.Read(new PayloadReader(buf));
            var list = new System.Collections.Generic.List<PlayerSnap>();
            foreach (var row in raw)
                if (PlayerSnap.TryRead(row, out var snap)) list.Add(snap);
            return list.ToArray();
        }
    }
}
