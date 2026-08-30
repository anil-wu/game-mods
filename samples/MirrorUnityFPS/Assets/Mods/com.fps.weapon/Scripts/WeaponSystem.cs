using System;
using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Fps.Weapon
{
    /// <summary>
    /// 武器系统（服务端权威）：
    /// 懒挂 WeaponState → 冷却/换弹计时 → 消费开火意图 → ray-sphere 命中判定 →
    /// 伤害经 ModCall（player:apply_damage，§12.11）→ 广播 weapon:shot / weapon:state。
    /// 跨 Mod 一律经能力调用拿快照（Rule 12：不引用玩家 Mod 任何类型）。
    /// </summary>
    public sealed class WeaponSystem : ISystem
    {
        private readonly IModContext _context;

        public WeaponSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            // 1. 玩家清单（快照 DTO，§12.9 规则 1）→ 懒挂 WeaponState
            var players = GetAllPlayers();
            foreach (var p in players)
            {
                var e = new Entity(p.EntityId);
                if (!ctx.World.Has<WeaponState>(e))
                {
                    ctx.World.Add(e, new WeaponState
                    {
                        Ammo = WeaponConfig.MaxAmmo,
                        MaxAmmo = WeaponConfig.MaxAmmo,
                    });
                }
            }

            // 2. 计时：冷却 / 换弹完成
            foreach (var (entityId, state) in ctx.World.Store<WeaponState>().All())
            {
                var e = new Entity(entityId);
                var s = state;
                var dirty = false;
                if (s.Cooldown > 0f) { s.Cooldown -= ctx.DeltaTime; dirty = true; }
                if (s.ReloadT > 0f)
                {
                    s.ReloadT -= ctx.DeltaTime;
                    if (s.ReloadT <= 0f)
                    {
                        s.Ammo = s.MaxAmmo;
                        BroadcastState(e, in s);
                    }
                    dirty = true;
                }
                if (dirty) ctx.World.Add(e, s);
            }

            // 3. 消费开火意图
            foreach (var (entityId, intent) in ctx.World.Store<FireIntent>().All())
            {
                var e = new Entity(entityId);
                ctx.World.Remove<FireIntent>(e);
                if (ctx.World.TryGet<WeaponState>(e, out var state))
                    TryFire(ctx, e, intent.Yaw, ref state, players);
            }
        }

        // ---- 开火 ----

        private void TryFire(SystemContext ctx, Entity shooter, float yaw, ref WeaponState state, PlayerSnapshot[] players)
        {
            if (state.Cooldown > 0f || state.ReloadT > 0f || state.Ammo <= 0) return;

            // 射手必须存活（快照校验，防尸体开枪）
            PlayerSnapshot? shooterSnap = null;
            foreach (var p in players)
                if (p.EntityId == shooter.Id) { shooterSnap = p; break; }
            if (shooterSnap is null || !shooterSnap.Value.Alive) return;

            state.Ammo--;
            state.Cooldown = WeaponConfig.FireInterval;
            ctx.World.Add(shooter, state);

            // 射线：眼睛高度 + 偏航方向（服务端权威原点，防客户端伪造）
            var ox = shooterSnap.Value.X;
            var oy = shooterSnap.Value.Y + WeaponConfig.EyeHeight;
            var oz = shooterSnap.Value.Z;
            var yawRad = yaw * (float)(Math.PI / 180.0);
            var dx = (float)Math.Sin(yawRad);
            var dz = (float)Math.Cos(yawRad);

            // ray-sphere 命中（最近者胜，排除自己；玩家 + 可选 NPC）
            uint hitEntity = 0;
            var hitApproach = float.MaxValue;
            foreach (var p in players)
            {
                if (p.EntityId == shooter.Id || !p.Alive) continue;
                var approach = TryHit(p.X, p.Y, p.Z, ox, oy, oz, dx, dz);
                if (approach < hitApproach)
                {
                    hitApproach = approach;
                    hitEntity = p.EntityId;
                }
            }
            foreach (var n in GetAllNpcs())
            {
                if (!n.Alive) continue;
                var approach = TryHit(n.X, n.Y, n.Z, ox, oy, oz, dx, dz);
                if (approach < hitApproach)
                {
                    hitApproach = approach;
                    hitEntity = n.EntityId;
                }
            }

            float hx, hy, hz;
            var killed = false;
            if (hitEntity != 0)
            {
                hx = ox + dx * hitApproach;
                hy = oy;
                hz = oz + dz * hitApproach;
                killed = IsNpc(hitEntity)
                    ? ApplyNpcDamage(hitEntity, WeaponConfig.Damage, shooter.Id)
                    : ApplyDamage(hitEntity, WeaponConfig.Damage, shooter.Id);
            }
            else
            {
                hx = ox + dx * WeaponConfig.Range;
                hy = oy;
                hz = oz + dz * WeaponConfig.Range;
            }

            // 广播开火事件（表现）与武器状态（HUD）
            var shooterNetId = NetIdOf(shooter);
            var hitNetId = hitEntity == 0 ? 0u : NetIdOf(new Entity(hitEntity));
            _context.Network.Broadcast(new ShotProtocol().Id,
                new ShotEvent(shooterNetId, hitNetId, hx, hy, hz, killed));
            BroadcastState(shooter, in state);

            // 本地事件（进程内消费方，如命中音效/成就，Rule 11 谁定义谁发布）
            var writer = _context.Messages.CreateWriter();
            writer.WriteUInt32(1, shooter.Id);
            writer.WriteUInt32(2, hitEntity);
            writer.WriteBool(3, killed);
            var buffer = writer.ToBuffer();
            _context.Messages.Publish(WeaponMod.ShotFiredEvent, 1, in buffer);
        }

        /// <summary>ray-sphere：命中返回最近距离，未命中返回 float.MaxValue。</summary>
        private static float TryHit(float px, float py, float pz, float ox, float oy, float oz, float dx, float dz)
        {
            var tx = px - ox;
            var ty = (py + WeaponConfig.ChestHeight) - oy;
            var tz = pz - oz;
            var approach = tx * dx + tz * dz;
            if (approach < 0f || approach > WeaponConfig.Range) return float.MaxValue;
            var distSq = tx * tx + ty * ty + tz * tz - approach * approach;
            return distSq > WeaponConfig.HitRadius * WeaponConfig.HitRadius ? float.MaxValue : approach;
        }

        private uint NetIdOf(Entity entity) =>
            _context.Network.Replication.TryGetNetworkId(entity, out var netId) ? netId : 0u;

        private void BroadcastState(Entity entity, in WeaponState state)
        {
            _context.Network.Broadcast(new StateProtocol().Id,
                new WeaponStateSync(NetIdOf(entity), state.Ammo, state.MaxAmmo, state.ReloadT > 0f));
        }

        // ---- 跨 Mod 能力调用（快照 DTO，零类型引用） ----

        private PlayerSnapshot[] GetAllPlayers()
        {
            // 能力 ABI：object[] 行集（字段布局见 com.fps.player/CONTRACT.md，Rule 19）
            var raw = InvokeCap(WeaponMod.PlayerModId, WeaponMod.GetAllPositionsCap, null);
            var result = new List<PlayerSnapshot>(raw.Length);
            foreach (var row in raw)
            {
                if (PlayerSnapshot.TryRead(row, out var snap))
                    result.Add(snap);
            }
            return result.ToArray();
        }

        private bool ApplyDamage(uint target, int amount, uint source)
        {
            var result = InvokeCap(WeaponMod.PlayerModId, WeaponMod.ApplyDamageCap,
                new object?[] { target, amount, source });
            return result.Length > 0 && result[0] is bool b && b;
        }

        // ---- 可选 NPC 目标（com.fps.npc 未加载则跳过，§12.4 Optional） ----

        private readonly HashSet<uint> _npcIds = new();

        private NpcSnapshot[] GetAllNpcs()
        {
            if (!_context.Mods.IsLoaded(WeaponMod.NpcModId)) return Array.Empty<NpcSnapshot>();
            var raw = InvokeCap(WeaponMod.NpcModId, WeaponMod.NpcGetAllCap, null);
            _npcIds.Clear();
            var list = new List<NpcSnapshot>();
            foreach (var row in raw)
            {
                if (!NpcSnapshot.TryRead(row, out var snap)) continue;
                _npcIds.Add(snap.EntityId);
                list.Add(snap);
            }
            return list.ToArray();
        }

        private bool IsNpc(uint entityId) => _npcIds.Contains(entityId);

        private bool ApplyNpcDamage(uint target, int amount, uint source)
        {
            var result = InvokeCap(WeaponMod.NpcModId, WeaponMod.NpcApplyDamageCap,
                new object?[] { target, amount, source });
            return result.Length > 0 && result[0] is bool b && b;
        }

        /// <summary>跨 Mod 能力调用（Rule 14 二进制）：编码入参 → Call → 解码返回。</summary>
        private object?[] InvokeCap(ModId target, CapabilityId cap, object?[]? args)
        {
            var buf = _context.Mods.Call(target, cap, DataCodec.Write(args));
            return DataCodec.Read(new PayloadReader(buf));
        }
    }

    /// <summary>NPC 位置快照（按 com.fps.npc CONTRACT.md 自实现解析，Rule 19）。</summary>
    public readonly struct NpcSnapshot
    {
        public readonly uint EntityId;
        public readonly float X, Y, Z;
        public readonly bool Alive;

        private NpcSnapshot(uint entityId, float x, float y, float z, bool alive)
        {
            EntityId = entityId; X = x; Y = y; Z = z; Alive = alive;
        }

        /// <summary>行布局：[0]=EntityId [1]=X [2]=Y [3]=Z [4]=Alive。</summary>
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

    /// <summary>玩家位置快照（按契约文档自实现解析，Rule 19：零类型共享）。</summary>
    public readonly struct PlayerSnapshot
    {
        public readonly uint EntityId;
        public readonly float X, Y, Z;
        public readonly bool Alive;

        private PlayerSnapshot(uint entityId, float x, float y, float z, bool alive)
        {
            EntityId = entityId; X = x; Y = y; Z = z; Alive = alive;
        }

        /// <summary>解析 PositionDto 行：[0]=EntityId [1]=X [2]=Y [3]=Z [4]=Yaw [5]=Alive。</summary>
        public static bool TryRead(object? row, out PlayerSnapshot snapshot)
        {
            snapshot = default;
            if (row is not object[] a || a.Length < 6) return false;
            if (a[0] is not uint id || a[1] is not float x || a[2] is not float y ||
                a[3] is not float z || a[5] is not bool alive) return false;
            snapshot = new PlayerSnapshot(id, x, y, z, alive);
            return true;
        }
    }
}
