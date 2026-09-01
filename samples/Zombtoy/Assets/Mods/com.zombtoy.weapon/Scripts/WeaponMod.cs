using System;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Weapon
{
    /// <summary>
    /// 武器 Mod（com.zombtoy.weapon）：4 槽（机关枪/霰弹/火箭筒/冰手枪）+ 副手龙卷风 + 弹药/换弹/切换 + 投射物（M2）。
    /// 单机 Host（HasClient+HasServer）：逻辑全部注册为 SystemSide.Server（summary §0.1），
    /// 视图（FireView/WeaponView）经静态桥与逻辑共享同一 World（同 player/enemy Mod 模式）。
    /// 伤害只在系统内发生（FireSystem/ProjectileSystem 调 enemy:damage，契约 §3 注），能力层只做
    /// 补弹/重置/查询/射击请求入口（契约 §4）；跨 Mod 能力调用一律二进制 DataCodec（Rule 14），
    /// 本 Mod 不引用 player/enemy 程序集，能力 ID 与行形状各自重复定义（Rule 12/19）。
    /// </summary>
    public sealed class WeaponMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.zombtoy.weapon");

        // 消息（谁定义谁发布，Rule 11；载荷字段布局见 CONTRACT.md §5）
        public static readonly MessageId AmmoChangedEvent = new(ModIdValue, "AmmoChanged");
        public static readonly MessageId WeaponSwitchedEvent = new(ModIdValue, "WeaponSwitched");

        // 导出能力（契约 §4；weapon:fire 为可选程序化射击入口，形状 = weapon_shot_request）
        public static readonly CapabilityId AddAmmoCap = new(ModIdValue, "add_ammo");
        public static readonly CapabilityId ResetCap = new(ModIdValue, "reset");
        public static readonly CapabilityId GetStateCap = new(ModIdValue, "get_state");
        public static readonly CapabilityId FireCap = new(ModIdValue, "fire");

        // 消费的对端能力（契约 §7；不引用 player/enemy 程序集，Rule 12）
        public static readonly ModId PlayerModId = new("com.zombtoy.player");
        public static readonly CapabilityId PlayerGetEntityCap = new(PlayerModId, "get_entity");
        public static readonly CapabilityId PlayerGetPositionCap = new(PlayerModId, "get_position");

        public static readonly ModId EnemyModId = new("com.zombtoy.enemy");
        public static readonly CapabilityId EnemyDamageCap = new(EnemyModId, "damage");
        public static readonly CapabilityId EnemyApplySlowCap = new(EnemyModId, "apply_slow");
        public static readonly CapabilityId EnemyGetAllCap = new(EnemyModId, "get_all");

        // 静态桥（视图访问 / 测试，Mod 内部状态，FPS 先例）
        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;

        /// <summary>武器域实体（WeaponActive/WeaponOwner/ShotRequest/SwitchRequest/TornadoRequest 所在，常驻）。</summary>
        public static uint ActiveEntityId { get; private set; }

        /// <summary>武器域 → 玩家（Register 时经 player:get_entity 关联，契约 §2/§7）。</summary>
        public static uint PlayerEntityId { get; private set; }

        /// <summary>4 个槽位实体（契约 §2：WeaponSlot + WeaponAmmo，Register 创建常驻）。</summary>
        public static readonly uint[] SlotEntityIds = new uint[WeaponConfig.SlotCount];

        private static ProjectileSystem? _projectileSystem;

        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;

            // 1. 组件注册（归属本 ModObject，卸载强制回收 §9.2；契约 §2）
            context.Ecs.RegisterComponent(typeof(WeaponSlot));
            context.Ecs.RegisterComponent(typeof(WeaponAmmo));
            context.Ecs.RegisterComponent(typeof(WeaponActive));
            context.Ecs.RegisterComponent(typeof(WeaponOwner));
            context.Ecs.RegisterComponent(typeof(ShotRequest));
            context.Ecs.RegisterComponent(typeof(SwitchRequest));
            context.Ecs.RegisterComponent(typeof(Projectile));
            context.Ecs.RegisterComponent(typeof(TornadoRequest)); // M2 视图请求（契约 §8，仅本 Mod 读写）

            // 2. 能力导出（§12.11；二进制 ABI Rule 14：DataCodec 编解码纯数据，bytes 不藏引用）
            context.Mods.Export(AddAmmoCap, reader => DataCodec.Write(AddAmmo(DataCodec.Read(reader))));
            context.Mods.Export(ResetCap, _ => DataCodec.Write(Reset()));
            context.Mods.Export(GetStateCap, _ => DataCodec.Write(GetState()));
            context.Mods.Export(FireCap, reader => DataCodec.Write(Fire(DataCodec.Read(reader))));

            if (context.HasServer)
            {
                // 3. 玩家定位（契约 §7：player:get_entity，Register 时取一次）
                PlayerEntityId = FetchPlayerEntity();

                // 4. 武器域实体 + 4 槽位实体（契约 §2：Register 创建，常驻）
                var domain = context.Ecs.CreateEntity();
                context.Ecs.World.Add(domain, new WeaponActive { CurrentIndex = 0, Count = (byte)WeaponConfig.SlotCount });
                context.Ecs.World.Add(domain, new WeaponOwner { PlayerEntityId = PlayerEntityId });
                ActiveEntityId = domain.Id;

                for (byte i = 0; i < WeaponConfig.SlotCount; i++)
                {
                    var def = WeaponConfig.Slots[i];
                    var slot = context.Ecs.CreateEntity();
                    context.Ecs.World.Add(slot, new WeaponSlot { Index = i, Kind = (byte)def.Kind });
                    context.Ecs.World.Add(slot, new WeaponAmmo
                    {
                        InMag = def.MagSize,
                        Reserve = def.StartReserve,
                        MaxMag = def.MagSize,
                    });
                    SlotEntityIds[i] = slot.Id;
                }

                // 5. 系统（任务约定顺序 + 契约 §3：Cooldown → Fire → Reload → Switch → Projectile(M2)）
                context.Ecs.RegisterSystem(new CooldownSystem(), SystemSide.Server);
                context.Ecs.RegisterSystem(new FireSystem(context), SystemSide.Server);
                context.Ecs.RegisterSystem(new ReloadSystem(context), SystemSide.Server);
                context.Ecs.RegisterSystem(new WeaponSwitchSystem(context), SystemSide.Server);
                _projectileSystem = new ProjectileSystem(context);
                context.Ecs.RegisterSystem(_projectileSystem, SystemSide.Server);
            }

            context.Log.Info($"武器 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            // 对称撤销（Rule 20）：系统/实体/能力/订阅由 Core 按 ModObject 归属强制回收（§9.2/§13.5），
            // 本 Mod 只清静态桥与本地状态。
            for (var i = 0; i < SlotEntityIds.Length; i++) SlotEntityIds[i] = 0;
            _projectileSystem = null;
            World = null!;
            Context = null!;
            ActiveEntityId = 0;
            PlayerEntityId = 0;
            context.Log.Info($"武器 Mod '{context.Info.Id}' 已注销");
        }

        // ---- 能力实现（契约 §4；同步执行 + 当场发消息，§0.5） ----

        /// <summary>weapon:add_ammo：args=[magazines i32]（≤0/缺省 = 每槽 +1 满弹匣）→ [applied bool]。
        /// 遍历 4 槽 Reserve=min(MaxReserve, Reserve+MaxMag*magazines)；发 AmmoChanged×槽（契约 §4）。</summary>
        private static object?[] AddAmmo(object? args)
        {
            if (!CapabilityShapes.TryReadAddAmmoArgs(args, out var magazines) || magazines <= 0)
                magazines = 1; // ≤0 或缺省 = 每槽 +1 满弹匣（契约 §4）

            var applied = false;
            for (byte i = 0; i < WeaponConfig.SlotCount; i++)
            {
                var e = new Entity(SlotEntityIds[i]);
                if (!World.TryGet<WeaponAmmo>(e, out var ammo)) continue;
                var def = WeaponConfig.Slots[i];
                ammo.Reserve = Math.Min(def.MaxReserve, ammo.Reserve + def.MagSize * magazines);
                World.Add(e, ammo);
                PublishAmmoChanged(i, in ammo);
                applied = true;
            }
            return new object?[] { applied };
        }

        /// <summary>weapon:reset：args=[] → [ok bool]（game:start 调用，契约 §4）：
        /// 全部槽位弹药/冷却/换弹复位默认值、CurrentIndex=0、清 ShotRequest/SwitchRequest/TornadoRequest、
        /// 副手 CD 复位；发 AmmoChanged×4 + WeaponSwitched(0)。</summary>
        private static object?[] Reset()
        {
            if (World is null || ActiveEntityId == 0) return new object?[] { false };
            for (byte i = 0; i < WeaponConfig.SlotCount; i++)
            {
                var e = new Entity(SlotEntityIds[i]);
                if (!World.TryGet<WeaponAmmo>(e, out var ammo)) continue;
                var def = WeaponConfig.Slots[i];
                ammo.InMag = def.MagSize;
                ammo.Reserve = def.StartReserve;
                ammo.IsReloading = false;
                ammo.ReloadTimer = 0f;
                ammo.FireCooldown = 0f;
                World.Add(e, ammo);
                PublishAmmoChanged(i, in ammo);
            }
            var domain = new Entity(ActiveEntityId);
            World.Remove<ShotRequest>(domain);
            World.Remove<SwitchRequest>(domain);
            World.Remove<TornadoRequest>(domain);
            if (World.TryGet<WeaponActive>(domain, out var active))
            {
                active.CurrentIndex = 0;
                World.Add(domain, active);
            }
            _projectileSystem?.ResetSession();
            PublishWeaponSwitched(0);
            return new object?[] { true };
        }

        /// <summary>weapon:get_state：args=[] → [slot u32, slotCount u32, inMag i32, reserve i32, maxMag i32]
        /// （HUD 弹药初始填充，契约 §4）。</summary>
        private static object?[] GetState()
        {
            var domain = new Entity(ActiveEntityId);
            if (!World.TryGet<WeaponActive>(domain, out var active))
                return CapabilityShapes.GetStateRow(0u, 0u, 0, 0, 0);
            var slotEntity = new Entity(SlotEntityIds[active.CurrentIndex]);
            var ammo = World.TryGet<WeaponAmmo>(slotEntity, out var a) ? a : default;
            return CapabilityShapes.GetStateRow(active.CurrentIndex, active.Count, ammo.InMag, ammo.Reserve, ammo.MaxMag);
        }

        /// <summary>weapon:fire：args=weapon_shot_request 形状 → [ok bool]（可选，经输入触发；写 ShotRequest 供 FireSystem 消费）。</summary>
        private static object?[] Fire(object? args)
        {
            if (!CapabilityShapes.TryReadShotRequestArgs(args, out var shooter, out var hit, out var isHit,
                    out var hitX, out var hitY, out var hitZ, out var kind))
                return new object?[] { false };
            var domain = new Entity(ActiveEntityId);
            if (!World.Exists(domain)) return new object?[] { false };
            World.Add(domain, new ShotRequest
            {
                ShooterId = shooter,
                HitEntityId = hit,
                IsHit = isHit,
                HitX = hitX, HitY = hitY, HitZ = hitZ,
                Kind = (byte)kind,
            });
            return new object?[] { true };
        }

        // ---- 内部 ----

        /// <summary>Register 时取一次玩家实体（契约 §7）。</summary>
        private static uint FetchPlayerEntity()
        {
            try
            {
                var buf = Context.Mods.Call(PlayerModId, PlayerGetEntityCap, DataCodec.Write(null));
                var row = DataCodec.Read(new PayloadReader(buf));
                if (CapabilityShapes.TryReadPlayerEntity(row, out var id)) return id;
                return 0;
            }
            catch (Exception)
            {
                return 0; // 依赖已声明，正常不会发生；取不到则伤害来源/存活判定降级
            }
        }

        /// <summary>调用 player:get_position（存活判定 + 投射物原点）；玩家未加载/解析失败 → null。</summary>
        public static (float X, float Y, float Z, bool Alive)? TryGetPlayerPosition(IModContext ctx)
        {
            try
            {
                var buf = ctx.Mods.Call(PlayerModId, PlayerGetPositionCap, DataCodec.Write(null));
                var row = DataCodec.Read(new PayloadReader(buf));
                if (CapabilityShapes.TryReadPlayerPosition(row, out var x, out var y, out var z, out var alive))
                    return (x, y, z, alive);
                return null;
            }
            catch (Exception)
            {
                return null; // 玩家未加载/卸载后：调用方降级（不开火/不生成投射物）
            }
        }

        /// <summary>发布 weapon:AmmoChanged（契约 §5）。</summary>
        private static void PublishAmmoChanged(uint slot, in WeaponAmmo ammo)
        {
            var buffer = WeaponMessagePayload.AmmoChanged(slot, ammo.InMag, ammo.Reserve, ammo.MaxMag);
            Context.Messages.Publish(AmmoChangedEvent, 1, in buffer);
        }

        /// <summary>发布 weapon:WeaponSwitched（契约 §5）。</summary>
        private static void PublishWeaponSwitched(uint slot)
        {
            var buffer = WeaponMessagePayload.WeaponSwitched(slot);
            Context.Messages.Publish(WeaponSwitchedEvent, 1, in buffer);
        }
    }
}
