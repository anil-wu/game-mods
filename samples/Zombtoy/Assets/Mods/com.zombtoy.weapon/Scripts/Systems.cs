using System;
using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Weapon
{
    /// <summary>
    /// 武器周期系统（契约 §3，全部 SystemSide.Server：Host 单机 = 全逻辑端，无头测试用 UpdateAll）。
    /// 伤害只在系统内发生（FireSystem/ProjectileSystem 调 enemy:damage，契约 §3 注）——能力层不直接扣敌人血，
    /// 所有跨 Mod 伤害调用可被无头测试覆盖。
    /// 更新顺序（按注册序）：Cooldown(计时) → Fire → Reload → Switch → Projectile(M2)。
    /// </summary>

    /// <summary>
    /// 冷却计时（任务约定独立系统；契约 §3 序 1 FireSystem 职责中的"冷却计时"拆出）：
    /// 每帧递减全部槽位的 FireCooldown（含非当前槽；FireSystem 校验 FireCooldown≤0 才可开火）。
    /// </summary>
    public sealed class CooldownSystem : ISystem
    {
        public void Update(SystemContext ctx)
        {
            foreach (var (entityId, ammo) in ctx.World.Store<WeaponAmmo>().All())
            {
                if (ammo.FireCooldown <= 0f) continue;
                var a = ammo;
                a.FireCooldown -= ctx.DeltaTime;
                if (a.FireCooldown < 0f) a.FireCooldown = 0f;
                ctx.World.Add(new Entity(entityId), a);
            }
        }
    }

    /// <summary>
    /// 射击（序 1，契约 §3）：消费 ShotRequest（帧末清空）→ 校验冷却/弹药/非换弹/玩家存活 →
    /// 扣弹（AmmoPerShot，霰弹=5）→ 冷却计时 → 按武器类别分发（射线武器调 enemy:damage；
    /// 火箭筒/冰手枪生成投射物实体）→ 发 weapon:AmmoChanged。
    /// 霰弹：视图聚合多条射线为单请求，命中目标承受 10×5=50（契约 §9"每枪 5 条射线各 10"的总量等值）。
    /// </summary>
    public sealed class FireSystem : ISystem
    {
        private readonly IModContext _context;

        public FireSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            var domain = new Entity(WeaponMod.ActiveEntityId);
            if (!ctx.World.Exists(domain)) return;
            if (!ctx.World.TryGet<ShotRequest>(domain, out var req)) return; // 无射击请求
            ctx.World.Remove<ShotRequest>(domain); // 单槽，帧末清空（契约 §2 注）

            // 玩家存活（player:get_position；失败/死亡 → 忽略本次射击，防尸体开枪）
            var player = WeaponMod.TryGetPlayerPosition(_context);
            if (player is null || !player.Value.Alive) return;

            var active = ctx.World.TryGet<WeaponActive>(domain, out var ac) ? ac : default;
            if (active.Count == 0 || active.CurrentIndex >= active.Count) return;
            var slotEntity = new Entity(WeaponMod.SlotEntityIds[active.CurrentIndex]);
            if (!ctx.World.TryGet<WeaponSlot>(slotEntity, out var slot)) return;
            if (!ctx.World.TryGet<WeaponAmmo>(slotEntity, out var ammo)) return;
            var owner = ctx.World.TryGet<WeaponOwner>(domain, out var wo) ? wo : default;

            // 校验（契约 §3 序 1）：冷却 ≤0 + 弹药充足 + 非换弹
            var def = WeaponConfig.Slots[slot.Index];
            if (ammo.FireCooldown > 0f || ammo.IsReloading) return;
            if (ammo.InMag < def.AmmoPerShot) return;

            var shooter = req.ShooterId != 0 ? req.ShooterId : owner.PlayerEntityId;

            // 扣弹 + 冷却计时（契约 §3 序 1）
            ammo.InMag -= def.AmmoPerShot;
            ammo.FireCooldown = def.Interval;
            ctx.World.Add(slotEntity, ammo);
            PublishAmmoChanged(slot.Index, in ammo);

            // 按武器类别分发（伤害只在系统内，契约 §3 注；sourceKind 区分，0=Default）
            switch (slot.Kind)
            {
                case (byte)WeaponKind.MachineGun:
                    if (req.HitEntityId != 0)
                        CallEnemyDamage(req.HitEntityId, def.Damage, shooter, SourceKind.Default);
                    break;
                case (byte)WeaponKind.Shotgun:
                    if (req.HitEntityId != 0)
                        CallEnemyDamage(req.HitEntityId, def.Damage * def.AmmoPerShot, shooter, SourceKind.Default);
                    break;
                case (byte)WeaponKind.RocketLauncher:
                case (byte)WeaponKind.CryoPistol:
                    SpawnProjectile(ctx, slot.Kind, shooter, player.Value, def, in req); // 直击/命中判定交给投射物系统（M2）
                    break;
            }
        }

        /// <summary>生成投射物实体（火箭/冰弹，契约 §3 序 1 + §2 Projectile）：枪口原点 = 玩家位置 + 枪口高度；
        /// 朝向 = 视图命中点（ShotRequest.HitX/Y/Z）相对枪口的方向（未命中时为射程终点）。</summary>
        private void SpawnProjectile(SystemContext ctx, byte kind, uint shooter,
            (float X, float Y, float Z, bool Alive) player, WeaponConfig.WeaponDef def, in ShotRequest req)
        {
            var ox = player.X;
            var oy = player.Y + WeaponConfig.MuzzleHeight;
            var oz = player.Z;

            var dx = req.HitX - ox;
            var dy = req.HitY - oy;
            var dz = req.HitZ - oz;
            var len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 0.001f) { dx = 0f; dy = 0f; dz = 1f; }
            else { dx /= len; dy /= len; dz /= len; }

            var e = _context.Ecs.CreateEntity(); // 归属本 ModObject，卸载强制回收（§9.2）
            var life = kind == (byte)WeaponKind.RocketLauncher ? WeaponConfig.RocketLife : WeaponConfig.IceLife;
            ctx.World.Add(e, new Projectile
            {
                Kind = kind,
                OwnerId = shooter,
                X = ox, Y = oy, Z = oz,
                DirX = dx, DirY = dy, DirZ = dz,
                Damage = def.Damage, // 火箭=直击 100；冰弹=30
                Life = life,
                State = 0,
            });
            WeaponMod.NotifyProjectileSpawned(e.Id, kind); // 视图桥（CONTRACT.md §8：宿主 ProjectileView 实例化原 prefab）
        }

        /// <summary>调 enemy:damage（二进制 ModCall，Rule 14；sourceKind 区分，契约 §4/§0.7）。</summary>
        internal static bool CallEnemyDamage(IModContext ctx, uint target, int amount, uint source, SourceKind sourceKind)
        {
            try
            {
                var buf = ctx.Mods.Call(WeaponMod.EnemyModId, WeaponMod.EnemyDamageCap,
                    DataCodec.Write(new object?[] { target, amount, source, (uint)sourceKind }));
                var row = DataCodec.Read(new PayloadReader(buf));
                return row.Length > 0 && row[0] is bool b && b;
            }
            catch (Exception)
            {
                return false; // enemy 未加载（卸载竞态）：本次伤害落空，不中断系统
            }
        }

        private void CallEnemyDamage(uint target, int amount, uint source, SourceKind sourceKind)
            => CallEnemyDamage(_context, target, amount, source, sourceKind);

        private void PublishAmmoChanged(uint slot, in WeaponAmmo ammo)
        {
            var buffer = WeaponMessagePayload.AmmoChanged(slot, ammo.InMag, ammo.Reserve, ammo.MaxMag);
            _context.Messages.Publish(WeaponMod.AmmoChangedEvent, 1, in buffer);
        }
    }

    /// <summary>
    /// 换弹（序 2，契约 §3）：IsReloading → 到点从 Reserve 补弹至 MaxMag（Reserve 不足全补）→
    /// 清 IsReloading → 发 weapon:AmmoChanged。换弹触发由视图负责（弹药=0 自动 / R 键，契约 §3 注），
    /// 本系统只做计时与结算，与触发无耦合。
    /// </summary>
    public sealed class ReloadSystem : ISystem
    {
        private readonly IModContext _context;

        public ReloadSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            foreach (var (entityId, ammo) in ctx.World.Store<WeaponAmmo>().All())
            {
                if (!ammo.IsReloading) continue;
                var e = new Entity(entityId);
                var a = ammo;
                a.ReloadTimer -= ctx.DeltaTime;
                if (a.ReloadTimer > 0f)
                {
                    ctx.World.Add(e, a);
                    continue;
                }

                // 到点补弹（契约 §3 序 2：Reserve 不足全补）
                var need = a.MaxMag - a.InMag;
                var take = Math.Min(need, a.Reserve);
                a.InMag += take;
                a.Reserve -= take;
                a.IsReloading = false;
                a.ReloadTimer = 0f;
                ctx.World.Add(e, a);
                if (ctx.World.TryGet<WeaponSlot>(e, out var slot))
                    PublishAmmoChanged(slot.Index, in a);
            }
        }

        private void PublishAmmoChanged(uint slot, in WeaponAmmo ammo)
        {
            var buffer = WeaponMessagePayload.AmmoChanged(slot, ammo.InMag, ammo.Reserve, ammo.MaxMag);
            _context.Messages.Publish(WeaponMod.AmmoChangedEvent, 1, in buffer);
        }
    }

    /// <summary>
    /// 切换武器（序 3，契约 §3）：消费 SwitchRequest（帧末清空）→ 校验槽位范围 / 目标槽非换弹中 →
    /// 更新 WeaponActive → 发 weapon:WeaponSwitched。换弹在后台独立进行（ReloadSystem 无耦合），
    /// 目标槽换弹中拒绝切入（契约 §3"非换弹中"）。
    /// </summary>
    public sealed class WeaponSwitchSystem : ISystem
    {
        private readonly IModContext _context;

        public WeaponSwitchSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            var domain = new Entity(WeaponMod.ActiveEntityId);
            if (!ctx.World.Exists(domain)) return;
            if (!ctx.World.TryGet<SwitchRequest>(domain, out var req)) return;
            ctx.World.Remove<SwitchRequest>(domain);

            var active = ctx.World.TryGet<WeaponActive>(domain, out var ac) ? ac : default;
            if (req.Index >= active.Count) return; // 槽位范围（契约 §3 序 3）
            if (req.Index == active.CurrentIndex) return;

            // 目标槽非换弹中（契约 §3 序 3）
            var target = new Entity(WeaponMod.SlotEntityIds[req.Index]);
            if (ctx.World.TryGet<WeaponAmmo>(target, out var ta) && ta.IsReloading) return;

            active.CurrentIndex = req.Index;
            ctx.World.Add(domain, active);

            var buffer = WeaponMessagePayload.WeaponSwitched(req.Index);
            _context.Messages.Publish(WeaponMod.WeaponSwitchedEvent, 1, in buffer);
        }
    }

    /// <summary>
    /// 投射物（序 4，M2，契约 §3）：Rocket 直线飞行 + 命中爆炸（直击 100 / 内圈 80@3m / 外圈 40@6m，
    /// sourceKind=Blast，Titan 爆炸免疫由 enemy:damage 内裁决）；IceBullet 追踪（enemy:get_all 选最近目标，
    /// 命中调 enemy:damage sourceKind=Ice + enemy:apply_slow）；Tornado 持续伤害 tick（sourceKind=Default）
    /// + 生命倒计时。另消费 TornadoRequest（副手 Fire2，契约 §8）并维护 30s CD。
    /// </summary>
    public sealed class ProjectileSystem : ISystem
    {
        private readonly IModContext _context;
        private float _tornadoCooldown;                       // 副手 CD（契约 §9：30s，weapon:reset 复位）
        private readonly Dictionary<uint, float> _tickTimers = new(); // 龙卷风伤害 tick 计时（系统内状态，无 ABI）

        public ProjectileSystem(IModContext context) => _context = context;

        /// <summary>会话复位（weapon:reset：副手 CD 清空，契约 §4）。</summary>
        public void ResetSession()
        {
            _tornadoCooldown = 0f;
            _tickTimers.Clear();
        }

        public void Update(SystemContext ctx)
        {
            // 副手请求（契约 §8：Fire2 → TornadoRequest → 生成 Tornado 实体，M2）
            var domain = new Entity(WeaponMod.ActiveEntityId);
            if (ctx.World.Exists(domain) && ctx.World.TryGet<TornadoRequest>(domain, out var treq))
            {
                ctx.World.Remove<TornadoRequest>(domain);
                if (_tornadoCooldown <= 0f)
                {
                    var player = WeaponMod.TryGetPlayerPosition(_context);
                    if (player is not null && player.Value.Alive)
                    {
                        SpawnTornado(ctx, in treq);
                        _tornadoCooldown = WeaponConfig.TornadoCooldown;
                    }
                }
            }
            if (_tornadoCooldown > 0f)
                _tornadoCooldown -= ctx.DeltaTime;

            // 投射物更新（ComponentStore 快照迭代，爆炸/命中销毁安全，同 EnemyHealthSystem 先例）
            foreach (var (entityId, proj) in ctx.World.Store<Projectile>().All())
            {
                var e = new Entity(entityId);
                var p = proj;
                p.Life -= ctx.DeltaTime;
                if (p.Life <= 0f)
                {
                    // 超时自毁（未命中；原版 Destroy(gameObject, life)）：无爆炸，通知视图直接销毁
                    WeaponMod.NotifyProjectileDestroyed(entityId, p.Kind, hitBurst: false);
                    ctx.World.Destroy(e);
                    _tickTimers.Remove(entityId);
                    continue;
                }
                switch (p.Kind)
                {
                    case (byte)WeaponKind.RocketLauncher: UpdateRocket(ctx, e, ref p); break;
                    case (byte)WeaponKind.CryoPistol: UpdateIceBullet(ctx, e, ref p); break;
                    case (byte)WeaponKind.Tornado: UpdateTornado(ctx, e, ref p); break;
                }
            }
        }

        /// <summary>火箭：直线飞行 + 与最近敌人 XZ 距离 &lt; 碰撞半径 → 爆炸（契约 §3 序 4）。</summary>
        private void UpdateRocket(SystemContext ctx, Entity e, ref Projectile p)
        {
            p.X += p.DirX * WeaponConfig.RocketSpeed * ctx.DeltaTime;
            p.Y += p.DirY * WeaponConfig.RocketSpeed * ctx.DeltaTime;
            p.Z += p.DirZ * WeaponConfig.RocketSpeed * ctx.DeltaTime;
            ctx.World.Add(e, p);

            uint direct = 0;
            var best = WeaponConfig.RocketCollisionRadius;
            foreach (var en in GetAllEnemies())
            {
                if (!en.Alive) continue;
                var d = XZDistance(p.X, p.Z, en.X, en.Z);
                if (d <= best) { best = d; direct = en.Id; }
            }
            if (direct == 0) return;

            ExplodeRocket(direct, p.X, p.Z, p.OwnerId); // 直击 + 内圈/外圈 AOE 各调一次 enemy:damage（契约 §3 序 4）
            WeaponMod.NotifyProjectileDestroyed(e.Id, p.Kind, hitBurst: true); // 命中→视图播原 prefab 爆炸组
            ctx.World.Destroy(e);
            _tickTimers.Remove(e.Id);
        }

        /// <summary>爆炸（契约 §9 行 2 / §3 序 4）：直击目标 100 → 内圈（≤3m）80 → 外圈（3-6m）40，sourceKind=Blast。</summary>
        private void ExplodeRocket(uint directHit, float ex, float ez, uint owner)
        {
            FireSystem.CallEnemyDamage(_context, directHit, WeaponConfig.RocketDirectDamage, owner, SourceKind.Blast);
            foreach (var en in GetAllEnemies())
            {
                if (en.Id == directHit || !en.Alive) continue;
                var d = XZDistance(ex, ez, en.X, en.Z);
                if (d <= WeaponConfig.RocketInnerRadius)
                    FireSystem.CallEnemyDamage(_context, en.Id, WeaponConfig.RocketInnerDamage, owner, SourceKind.Blast);
                else if (d <= WeaponConfig.RocketOuterRadius)
                    FireSystem.CallEnemyDamage(_context, en.Id, WeaponConfig.RocketOuterDamage, owner, SourceKind.Blast);
            }
        }

        /// <summary>冰弹：追踪（enemy:get_all 选最近存活目标，契约 §3 序 4）→ 转向 → 命中（距离 &lt; 命中半径）
        /// 调 enemy:damage（sourceKind=Ice）+ enemy:apply_slow（缓速 0.6/1.5s，契约 §9 行 3）。</summary>
        private void UpdateIceBullet(SystemContext ctx, Entity e, ref Projectile p)
        {
            // 选最近存活目标
            uint target = 0;
            var best = float.MaxValue;
            foreach (var en in GetAllEnemies())
            {
                if (!en.Alive) continue;
                var d = Distance3D(p.X, p.Y, p.Z, en.X, en.Y, en.Z);
                if (d < best) { best = d; target = en.Id; }
            }

            if (target != 0)
            {
                // 转向目标（追踪）
                var row = FindEnemy(target);
                if (row is not null)
                {
                    var dx = row.Value.X - p.X;
                    var dy = row.Value.Y - p.Y;
                    var dz = row.Value.Z - p.Z;
                    var len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (len > 0.001f) { p.DirX = dx / len; p.DirY = dy / len; p.DirZ = dz / len; }
                }
            }

            p.X += p.DirX * WeaponConfig.IceSpeed * ctx.DeltaTime;
            p.Y += p.DirY * WeaponConfig.IceSpeed * ctx.DeltaTime;
            p.Z += p.DirZ * WeaponConfig.IceSpeed * ctx.DeltaTime;
            ctx.World.Add(e, p);

            if (target != 0 && best <= WeaponConfig.IceHitRadius)
            {
                // 命中：伤害（sourceKind=Ice）+ 缓速（enemy:apply_slow，契约 §3 序 4 / §7）
                FireSystem.CallEnemyDamage(_context, target, (int)p.Damage, p.OwnerId, SourceKind.Ice);
                CallEnemySlow(target, WeaponConfig.IceSlowMul, WeaponConfig.IceSlowDuration);
                WeaponMod.NotifyProjectileDestroyed(e.Id, p.Kind, hitBurst: false); // 命中即销毁（无爆发，原版 Destroy(gameObject)）
                ctx.World.Destroy(e);
            }
        }

        /// <summary>龙卷风：直线前进 + 持续伤害 tick（0.2s：内圈 ≤4m 8 / 外圈 4-10m 2，sourceKind=Default，契约 §9 行 4）+ 生命倒计时。</summary>
        private void UpdateTornado(SystemContext ctx, Entity e, ref Projectile p)
        {
            p.X += p.DirX * WeaponConfig.TornadoSpeed * ctx.DeltaTime;
            p.Z += p.DirZ * WeaponConfig.TornadoSpeed * ctx.DeltaTime;
            ctx.World.Add(e, p);

            // 固定步长累加器（避免倒计时减法在浮点下产生正残差导致 tick 抖动）
            if (!_tickTimers.TryGetValue(e.Id, out var acc)) acc = 0f;
            acc += ctx.DeltaTime;
            if (acc >= WeaponConfig.TornadoTickInterval)
            {
                acc -= WeaponConfig.TornadoTickInterval;
                TickTornado(p.X, p.Z, p.OwnerId);
            }
            _tickTimers[e.Id] = acc;
        }

        /// <summary>龙卷风 tick 伤害（契约 §9 行 4：内圈 8 / 外圈 2）。</summary>
        private void TickTornado(float x, float z, uint owner)
        {
            foreach (var en in GetAllEnemies())
            {
                if (!en.Alive) continue;
                var d = XZDistance(x, z, en.X, en.Z);
                if (d <= WeaponConfig.TornadoInnerRadius)
                    FireSystem.CallEnemyDamage(_context, en.Id, WeaponConfig.TornadoTickDamage, owner, SourceKind.Default);
                else if (d <= WeaponConfig.TornadoOuterRadius)
                    FireSystem.CallEnemyDamage(_context, en.Id, WeaponConfig.TornadoOuterDamage, owner, SourceKind.Default);
            }
        }

        /// <summary>生成 Tornado 投射物实体（契约 §8：TornadoRequest → ProjectileSystem 生成）。</summary>
        private void SpawnTornado(SystemContext ctx, in TornadoRequest treq)
        {
            var e = _context.Ecs.CreateEntity();
            ctx.World.Add(e, new Projectile
            {
                Kind = (byte)WeaponKind.Tornado,
                OwnerId = WeaponMod.PlayerEntityId,
                X = treq.X, Y = treq.Y, Z = treq.Z,
                DirX = treq.DirX, DirY = 0f, DirZ = treq.DirZ,
                Damage = WeaponConfig.TornadoTickDamage,
                Life = WeaponConfig.TornadoLifespan,
                State = 0,
            });
            WeaponMod.NotifyProjectileSpawned(e.Id, (byte)WeaponKind.Tornado); // 视图桥（CONTRACT.md §8）
        }

        // ---- 对端能力调用（enemy:get_all / enemy:apply_slow，二进制 ModCall，Rule 14） ----

        /// <summary>敌人快照（enemy:get_all 行解析，Rule 19 各自重复定义）。</summary>
        private readonly struct EnemyRow
        {
            public readonly uint Id;
            public readonly float X, Y, Z;
            public readonly bool Alive;
            public EnemyRow(uint id, float x, float y, float z, bool alive)
            {
                Id = id; X = x; Y = y; Z = z; Alive = alive;
            }
        }

        private EnemyRow[] GetAllEnemies()
        {
            var list = new List<EnemyRow>();
            try
            {
                var buf = _context.Mods.Call(WeaponMod.EnemyModId, WeaponMod.EnemyGetAllCap, DataCodec.Write(null));
                var rows = DataCodec.Read(new PayloadReader(buf));
                foreach (var row in rows)
                {
                    if (CapabilityShapes.TryReadGetAllRow(row, out var id, out var x, out var y, out var z,
                            out _, out var alive, out _, out _))
                        list.Add(new EnemyRow(id, x, y, z, alive));
                }
            }
            catch (Exception)
            {
                // enemy 未加载（卸载竞态）：本次无目标（飞行/落空），不中断系统
            }
            return list.ToArray();
        }

        private EnemyRow? FindEnemy(uint id)
        {
            foreach (var en in GetAllEnemies())
                if (en.Id == id) return en;
            return null;
        }

        private void CallEnemySlow(uint target, float speedMul, float duration)
        {
            try
            {
                _context.Mods.Call(WeaponMod.EnemyModId, WeaponMod.EnemyApplySlowCap,
                    DataCodec.Write(new object?[] { target, speedMul, duration }));
            }
            catch (Exception)
            {
                // enemy 未加载（卸载竞态）：缓速落空，不中断系统
            }
        }

        // ---- 几何 ----

        private static float XZDistance(float ax, float az, float bx, float bz)
        {
            var dx = ax - bx;
            var dz = az - bz;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        private static float Distance3D(float ax, float ay, float az, float bx, float by, float bz)
        {
            var dx = ax - bx;
            var dy = ay - by;
            var dz = az - bz;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
