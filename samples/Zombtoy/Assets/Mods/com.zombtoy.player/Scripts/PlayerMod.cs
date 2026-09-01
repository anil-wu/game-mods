using System;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Player
{
    /// <summary>
    /// 玩家 Mod（com.zombtoy.player）：移动 / 转身 / 生命 / 体力 / 冲刺 / 死亡 / 相机。
    /// 单机 Host（HasClient+HasServer）：逻辑全部注册为 SystemSide.Server（summary §0.1），
    /// 视图（MonoBehaviour）与逻辑共享同一 World（静态桥），无网络协议（Rule 16 面向网络协议，本 Mod 不涉及）。
    /// 伤害/治疗/重置在能力内同步执行并当场发消息（契约 §0.5）；系统只做周期行为（移动/体力，契约 §2）。
    /// 本 Mod 是叶子 Mod：不消费任何跨 Mod 能力/消息，只被调用/订阅（依赖最少，契约头部）。
    /// </summary>
    public sealed class PlayerMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.zombtoy.player");

        // 消息（谁定义谁发布，Rule 11；载荷字段布局见 CONTRACT.md §4）
        public static readonly MessageId HealthChangedEvent = new(ModIdValue, "HealthChanged");
        public static readonly MessageId StaminaChangedEvent = new(ModIdValue, "StaminaChanged");
        public static readonly MessageId DiedEvent = new(ModIdValue, "Died");

        // 能力（§12.11，契约见 CONTRACT.md §3）
        public static readonly CapabilityId DamageCap = new(ModIdValue, "damage");
        public static readonly CapabilityId HealCap = new(ModIdValue, "heal");
        public static readonly CapabilityId ResetCap = new(ModIdValue, "reset");
        public static readonly CapabilityId GetEntityCap = new(ModIdValue, "get_entity");
        public static readonly CapabilityId GetStateCap = new(ModIdValue, "get_state");
        public static readonly CapabilityId GetPositionCap = new(ModIdValue, "get_position");

        // 静态桥（视图访问，Mod 内部状态，FPS 先例）
        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;
        public static uint LocalPlayerEntityId { get; private set; }

        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;
            LocalPlayerEntityId = 0;

            // 1. 组件注册（归属本 ModObject，卸载强制回收 §9.2）
            context.Ecs.RegisterComponent(typeof(Position3));
            context.Ecs.RegisterComponent(typeof(Velocity3));
            context.Ecs.RegisterComponent(typeof(Facing));
            context.Ecs.RegisterComponent(typeof(PlayerHealth));
            context.Ecs.RegisterComponent(typeof(Stamina));
            context.Ecs.RegisterComponent(typeof(PlayerInput));
            context.Ecs.RegisterComponent(typeof(PlayerFlags));

            // 2. 能力导出（§12.11；二进制 ABI Rule 14：DataCodec 编解码纯数据，bytes 不藏引用）
            context.Mods.Export(DamageCap, reader => DataCodec.Write(ApplyDamage(DataCodec.Read(reader))));
            context.Mods.Export(HealCap, reader => DataCodec.Write(Heal(DataCodec.Read(reader))));
            context.Mods.Export(ResetCap, reader => DataCodec.Write(ResetPlayer(DataCodec.Read(reader))));
            context.Mods.Export(GetEntityCap, _ => DataCodec.Write(new object?[] { LocalPlayerEntityId }));
            context.Mods.Export(GetStateCap, _ => DataCodec.Write(GetState()));
            context.Mods.Export(GetPositionCap, _ => DataCodec.Write(GetPosition()));

            if (context.HasServer)
            {
                // 3. 系统（契约 §2 顺序：InputApply → Movement → Stamina）
                context.Ecs.RegisterSystem(new PlayerInputApplySystem(), SystemSide.Server);
                context.Ecs.RegisterSystem(new PlayerMovementSystem(), SystemSide.Server);

                // 体力变化 → 发布 StaminaChanged（Sink 注入，PushBox 先例：系统不直接持有消息上下文）
                var staminaSystem = new PlayerStaminaSystem();
                staminaSystem.OnStaminaChanged = (_, current, max) =>
                {
                    var buffer = PlayerMessagePayload.StaminaChanged(current, max);
                    Context.Messages.Publish(StaminaChangedEvent, 1, in buffer);
                };
                context.Ecs.RegisterSystem(staminaSystem, SystemSide.Server);

                // 4. 玩家主实体（Register 时创建，常驻；game:start 经 player:reset 重置，契约 §1/§3）
                SpawnPlayer(context);
            }

            context.Log.Info($"玩家 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            // 对称撤销（Rule 20）：系统/实体/能力/订阅由 Core 按 ModObject 归属强制回收（§9.2/§13.5），
            // 本 Mod 只清静态桥与本地状态。
            World = null!;
            Context = null!;
            LocalPlayerEntityId = 0;
            context.Log.Info($"玩家 Mod '{context.Info.Id}' 已注销");
        }

        // ---- 生成 ----

        private static uint SpawnPlayer(IModContext context)
        {
            var e = context.Ecs.CreateEntity(); // 归属本 ModObject，卸载销毁（§9.2）
            context.Ecs.World.Add(e, new Position3 { X = PlayerConfig.SpawnX, Y = PlayerConfig.SpawnY, Z = PlayerConfig.SpawnZ });
            context.Ecs.World.Add(e, new Velocity3());
            context.Ecs.World.Add(e, new Facing { Yaw = 0f });
            context.Ecs.World.Add(e, new PlayerHealth { Current = PlayerConfig.MaxHealth, Max = PlayerConfig.MaxHealth });
            context.Ecs.World.Add(e, new Stamina { Current = PlayerConfig.MaxStamina, Max = PlayerConfig.MaxStamina });
            context.Ecs.World.Add(e, new PlayerInput());
            context.Ecs.World.Add(e, new PlayerFlags { IsDead = false });
            LocalPlayerEntityId = e.Id;
            return e.Id;
        }

        // ---- 能力实现（伤害/治疗/重置同步执行 + 当场发消息，契约 §3/§0.5） ----

        /// <summary>player:damage：args=[amount int, source uint] → [killed bool]。</summary>
        private static object?[] ApplyDamage(object? args)
        {
            if (!CapabilityShapes.TryReadDamage(args, out var amount, out var source))
                return new object?[] { false };
            var e = new Entity(LocalPlayerEntityId);
            if (!World.TryGet<PlayerHealth>(e, out var hp)) return new object?[] { false };
            if (IsDead(e) || amount <= 0) return new object?[] { false };

            hp.Current = Math.Max(0, hp.Current - amount);
            World.Add(e, hp);
            PublishHealthChanged(hp.Current, hp.Max);

            if (hp.Current > 0) return new object?[] { false };

            // 死亡：置 IsDead + 发布 player:Died（score 结算 / ui 死亡表现订阅）
            if (World.TryGet<PlayerFlags>(e, out var flags)) { flags.IsDead = true; World.Add(e, flags); }
            var died = PlayerMessagePayload.Died(source);
            Context.Messages.Publish(DiedEvent, 1, in died);
            return new object?[] { true };
        }

        /// <summary>player:heal：args=[amount int] → [applied bool]。</summary>
        private static object?[] Heal(object? args)
        {
            if (!CapabilityShapes.TryReadHeal(args, out var amount))
                return new object?[] { false };
            var e = new Entity(LocalPlayerEntityId);
            if (!World.TryGet<PlayerHealth>(e, out var hp)) return new object?[] { false };
            if (IsDead(e) || amount <= 0) return new object?[] { false };
            if (hp.Current >= hp.Max)
                return new object?[] { true }; // 满血：仍返回 true，治疗量 0 溢出丢弃（对齐原版血瓶照常消耗）

            hp.Current = Math.Min(hp.Max, hp.Current + amount);
            World.Add(e, hp);
            PublishHealthChanged(hp.Current, hp.Max);
            return new object?[] { true };
        }

        /// <summary>player:reset：args=[x float, y float, z float] → [ok bool]（game:start 新对局调用）。</summary>
        private static object?[] ResetPlayer(object? args)
        {
            if (!CapabilityShapes.TryReadReset(args, out var x, out var y, out var z))
                return new object?[] { false };
            var e = new Entity(LocalPlayerEntityId);
            if (!World.Exists(e)) return new object?[] { false };

            // 满血 / 满体力 / 清 IsDead / 重置位置朝向速度
            if (World.TryGet<PlayerHealth>(e, out var hp))
            {
                hp.Current = hp.Max;
                World.Add(e, hp);
                PublishHealthChanged(hp.Current, hp.Max);
            }
            if (World.TryGet<Stamina>(e, out var st))
            {
                st.Current = st.Max;
                World.Add(e, st);
                PublishStaminaChanged(st.Current, st.Max);
            }
            if (World.TryGet<PlayerFlags>(e, out var flags)) { flags.IsDead = false; World.Add(e, flags); }
            World.Add(e, new Position3 { X = x, Y = y, Z = z });
            World.Add(e, new Velocity3());
            World.Add(e, new Facing { Yaw = 0f });
            return new object?[] { true };
        }

        /// <summary>player:get_entity：[] → [entityId uint]（0 = 未生成）。</summary>
        private static object?[] GetEntity() => new object?[] { LocalPlayerEntityId };

        /// <summary>player:get_state：[] → [health, max, stamina, maxStamina, alive]（HUD 初始填充 / 敌人判定）。</summary>
        private static object?[] GetState()
        {
            var e = new Entity(LocalPlayerEntityId);
            var hp = World.TryGet<PlayerHealth>(e, out var h) ? h : default;
            var st = World.TryGet<Stamina>(e, out var s) ? s : default;
            return CapabilityShapes.StateRow(hp.Current, hp.Max, st.Current, st.Max, !IsDead(e));
        }

        /// <summary>player:get_position：[] → [x, y, z, alive]（敌人追击目标点）。</summary>
        private static object?[] GetPosition()
        {
            var e = new Entity(LocalPlayerEntityId);
            var pos = World.TryGet<Position3>(e, out var p) ? p : default;
            return CapabilityShapes.PositionRow(pos.X, pos.Y, pos.Z, !IsDead(e));
        }

        // ---- 消息发布（字段编号线格式，CONTRACT.md §4） ----

        private static bool IsDead(Entity e)
            => World.TryGet<PlayerFlags>(e, out var flags) && flags.IsDead;

        private static void PublishHealthChanged(int current, int max)
        {
            var buffer = PlayerMessagePayload.HealthChanged(current, max);
            Context.Messages.Publish(HealthChangedEvent, 1, in buffer);
        }

        private static void PublishStaminaChanged(float current, float max)
        {
            var buffer = PlayerMessagePayload.StaminaChanged(current, max);
            Context.Messages.Publish(StaminaChangedEvent, 1, in buffer);
        }
    }
}
