using System;
using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Item
{
    /// <summary>
    /// 道具 Mod（com.zombtoy.item）：血瓶/弹药箱定时生成 + 拾取。
    /// 单机 Host（HasClient+HasServer）：逻辑全部注册为 SystemSide.Server（summary §0.1），
    /// 视图（ItemView）经静态桥与逻辑共享同一 World（同 player/enemy Mod 模式）。
    /// 拾取链路（契约头部）：ItemView OnTriggerEnter(玩家) → 静态桥调本 Mod item:pickup 能力 →
    /// 能力内 ModCall player:heal / weapon:add_ammo（二进制 DataCodec，Rule 14）→ 消耗并发 item:PickedUp。
    /// 生成在 ItemSpawnSystem（契约 §3）；拾取无独立系统：item:pickup 能力内同步完成（契约 §3 注）。
    /// 本 Mod 不引用 player/weapon 程序集，能力 ID 与行形状各自重复定义（Rule 12/19）。
    /// </summary>
    public sealed class ItemMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.zombtoy.item");

        // 消息（谁定义谁发布，Rule 11；载荷字段布局见 CONTRACT.md §5）
        public static readonly MessageId PickedUpEvent = new(ModIdValue, "PickedUp");

        // 导出能力（契约 §4；item:pickup 为视图触发拾取入口，item:reset/set_spawning 为 game 相位控制）
        public static readonly CapabilityId PickupCap = new(ModIdValue, "pickup");
        public static readonly CapabilityId SetSpawningCap = new(ModIdValue, "set_spawning");
        public static readonly CapabilityId ResetCap = new(ModIdValue, "reset");

        // 消费的对端能力（契约 §7；不引用 player/weapon 程序集，Rule 12）
        public static readonly ModId PlayerModId = new("com.zombtoy.player");
        public static readonly CapabilityId PlayerHealCap = new(PlayerModId, "heal");
        public static readonly CapabilityId PlayerGetStateCap = new(PlayerModId, "get_state");
        public static readonly ModId WeaponModId = new("com.zombtoy.weapon");
        public static readonly CapabilityId WeaponAddAmmoCap = new(WeaponModId, "add_ammo");

        // 静态桥（视图访问 / 测试，Mod 内部状态，FPS 先例）
        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;

        /// <summary>道具生成器实体（ItemSpawner 单实体状态机，Register 创建常驻，契约 §2）。</summary>
        public static uint SpawnerEntityId { get; private set; }

        /// <summary>视图静态桥（契约 §8）：宿主 ItemSpawnerView 订阅实例化 prefab；ItemView 拾取表现（禁 Collider/音效）。</summary>
        public static event Action<uint>? OnItemSpawned;
        public static event Action<uint>? OnItemPicked;

        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;

            // 1. 组件注册（归属本 ModObject，卸载强制回收 §9.2；契约 §2）
            context.Ecs.RegisterComponent(typeof(ItemData));
            context.Ecs.RegisterComponent(typeof(ItemState));
            context.Ecs.RegisterComponent(typeof(ItemPosition));
            context.Ecs.RegisterComponent(typeof(ItemSpawner));

            // 2. 能力导出（§12.11；二进制 ABI Rule 14：DataCodec 编解码纯数据，bytes 不藏引用）
            context.Mods.Export(PickupCap, reader => DataCodec.Write(Pickup(DataCodec.Read(reader))));
            context.Mods.Export(SetSpawningCap, reader => DataCodec.Write(SetSpawning(DataCodec.Read(reader))));
            context.Mods.Export(ResetCap, _ => DataCodec.Write(Reset()));

            if (context.HasServer)
            {
                // 3. 系统（契约 §3：ItemSpawnSystem 定时生成）
                context.Ecs.RegisterSystem(new ItemSpawnSystem(context), SystemSide.Server);

                // 4. 生成器实体（单实体状态机，契约 §2；Enabled 初始开：对齐 enemy 生成器 StartSpawning，
                //    game Mod 经 item:set_spawning 做相位开关，见 game CONTRACT 补充通道表）
                var spawner = context.Ecs.CreateEntity();
                context.Ecs.World.Add(spawner, new ItemSpawner
                {
                    Enabled = true,
                    Timer = ItemConfig.FirstDelay,
                    ActiveCount = 0,
                    MaxItems = ItemConfig.MaxItems,
                    NextSpawnIndex = 0,
                });
                SpawnerEntityId = spawner.Id;
            }

            context.Log.Info($"道具 Mod '{context.Info.Id}' v{context.Info.Version} 已注册");
        }

        public void Unregister(IModContext context)
        {
            // 对称撤销（Rule 20）：系统/实体/能力/订阅由 Core 按 ModObject 归属强制回收（§9.2/§13.5），
            // 本 Mod 只清静态桥与本地状态。
            OnItemSpawned = null;
            OnItemPicked = null;
            World = null!;
            Context = null!;
            SpawnerEntityId = 0;
            context.Log.Info($"道具 Mod '{context.Info.Id}' 已注销");
        }

        // ---- 能力实现（契约 §4；同步执行 + 当场发消息，§0.5） ----

        /// <summary>item:pickup：args=[itemEntityId u32] → [applied bool, kind u32]。
        /// 实体不存在/已 Picked/玩家死亡 → [false, 0]；按 Kind 分派：HealthPotion(0) → player:heal([Amount])、
        /// AmmoBox(1) → weapon:add_ammo([Amount])；成功后置 Picked、销毁实体+视图、发 item:PickedUp（契约 §4）。</summary>
        private static object?[] Pickup(object? args)
        {
            if (!CapabilityShapes.TryReadPickupArgs(args, out var entityId))
                return CapabilityShapes.PickupResult(false, 0u);
            var e = new Entity(entityId);
            if (!World.Exists(e) || !World.TryGet<ItemData>(e, out var data))
                return CapabilityShapes.PickupResult(false, 0u); // 实体不存在 → [false, 0]
            if (World.TryGet<ItemState>(e, out var st) && st.Picked)
                return CapabilityShapes.PickupResult(false, 0u); // 已拾取（防重复触发，契约 §4）
            if (!TryGetPlayerAlive(Context))
                return CapabilityShapes.PickupResult(false, 0u); // 玩家死亡不拾取（契约 §7：拾取前存活判定）

            var applied = false;
            switch (data.Kind)
            {
                case (byte)ItemKind.HealthPotion:
                    applied = CallPlayerHeal(data.Amount); // 血瓶治疗（player:heal，契约 §7）
                    break;
                case (byte)ItemKind.AmmoBox:
                    applied = CallWeaponAddAmmo(data.Amount); // 弹药箱补弹（weapon:add_ammo，契约 §7）
                    break;
            }
            if (!applied)
                return CapabilityShapes.PickupResult(false, 0u); // 对端能力拒绝 → 不消耗道具

            // 成功后：置 Picked、销毁实体+视图、发 item:PickedUp（契约 §4）
            if (World.TryGet<ItemState>(e, out var s)) { s.Picked = true; World.Add(e, s); }
            World.Destroy(e);
            NotifyItemPicked(entityId); // 视图：禁 Collider + 拾取音效（契约 §8）；实体已销毁，ItemView 下一帧自毁
            PublishPickedUp(data.Kind, data.Amount);
            DecrementActiveCount();
            return CapabilityShapes.PickupResult(true, data.Kind);
        }

        /// <summary>item:set_spawning：args=[enabled bool] → [ok bool]（game 相位控制，契约 §4）。
        /// 关闭：Timer 清零（暂停）；恢复：Timer 置首延迟（对齐"恢复后满首延迟首刷"语义，同 enemy 恢复满间隔）。</summary>
        private static object?[] SetSpawning(object? args)
        {
            if (!CapabilityShapes.TryReadSetSpawningArgs(args, out var enabled))
                return new object?[] { false };
            var se = new Entity(SpawnerEntityId);
            if (!World.TryGet<ItemSpawner>(se, out var spawner))
                return new object?[] { false };
            spawner.Enabled = enabled;
            if (!enabled)
            {
                spawner.Timer = 0f; // 清空 Timer（暂停，契约 §4）
            }
            else if (spawner.Timer <= 0f)
            {
                spawner.Timer = ItemConfig.FirstDelay; // 恢复：满首延迟后首刷（对齐原版 StartSpawning）
            }
            World.Add(se, spawner);
            return new object?[] { true };
        }

        /// <summary>item:reset：args=[] → [ok bool]（game:start 调用，契约 §4：销毁全部道具实体+视图、
        /// ActiveCount=0、NextSpawnIndex=0、Timer=首延迟）。</summary>
        private static object?[] Reset()
        {
            if (World is null || SpawnerEntityId == 0) return new object?[] { false };

            // 销毁全部道具实体（含已拾取残留；视图经 ItemView.Update 的 Exists 检查自毁）
            var stale = new List<uint>();
            foreach (var (id, _) in World.Store<ItemData>().All()) stale.Add(id);
            foreach (var id in stale) World.Destroy(new Entity(id));

            var se = new Entity(SpawnerEntityId);
            if (World.TryGet<ItemSpawner>(se, out var spawner))
            {
                spawner.ActiveCount = 0;
                spawner.NextSpawnIndex = 0;
                spawner.Timer = ItemConfig.FirstDelay;
                World.Add(se, spawner);
            }
            return new object?[] { true };
        }

        // ---- 内部 ----

        /// <summary>视图静态桥通知（事件只能在声明类内调用，C# 事件语义）：ItemSpawnerView / ItemView 表现。</summary>
        internal static void NotifyItemSpawned(uint id) => OnItemSpawned?.Invoke(id);
        internal static void NotifyItemPicked(uint id) => OnItemPicked?.Invoke(id);

        /// <summary>调用 player:get_state 取存活判定（生成/拾取前判定）；玩家未加载/解析失败 → false（不生成/不拾取）。</summary>
        public static bool TryGetPlayerAlive(IModContext ctx)
        {
            try
            {
                var buf = ctx.Mods.Call(PlayerModId, PlayerGetStateCap, DataCodec.Write(null));
                var row = DataCodec.Read(new PayloadReader(buf));
                if (CapabilityShapes.TryReadPlayerState(row, out _, out _, out _, out _, out var alive))
                    return alive;
                return false;
            }
            catch (Exception)
            {
                return false; // 玩家未加载/卸载后：调用方降级（不生成/不拾取）
            }
        }

        /// <summary>调用 player:heal（血瓶治疗，二进制 ModCall，Rule 14；契约 §7）。</summary>
        private static bool CallPlayerHeal(int amount)
        {
            try
            {
                var buf = Context.Mods.Call(PlayerModId, PlayerHealCap, DataCodec.Write(new object?[] { amount }));
                var row = DataCodec.Read(new PayloadReader(buf));
                return row.Length > 0 && row[0] is bool b && b;
            }
            catch (Exception)
            {
                return false; // player 未加载（卸载竞态）：本次治疗落空，不消耗道具
            }
        }

        /// <summary>调用 weapon:add_ammo（弹药箱补弹，二进制 ModCall，Rule 14；契约 §7）。</summary>
        private static bool CallWeaponAddAmmo(int magazines)
        {
            try
            {
                var buf = Context.Mods.Call(WeaponModId, WeaponAddAmmoCap, DataCodec.Write(new object?[] { magazines }));
                var row = DataCodec.Read(new PayloadReader(buf));
                return row.Length > 0 && row[0] is bool b && b;
            }
            catch (Exception)
            {
                return false; // weapon 未加载（卸载竞态）：本次补弹落空，不消耗道具
            }
        }

        /// <summary>拾取后存活计数 -1（拾取消耗道具，生成器腾位，契约 §3 ActiveCount 语义）。</summary>
        private static void DecrementActiveCount()
        {
            if (SpawnerEntityId == 0) return;
            var se = new Entity(SpawnerEntityId);
            if (!World.TryGet<ItemSpawner>(se, out var spawner)) return;
            if (spawner.ActiveCount > 0) spawner.ActiveCount--;
            World.Add(se, spawner);
        }

        /// <summary>发布 item:PickedUp（kind/amount，契约 §5；ui 可选拾取提示订阅）。</summary>
        private static void PublishPickedUp(byte kind, int amount)
        {
            var buffer = ItemMessagePayload.PickedUp(kind, amount);
            Context.Messages.Publish(PickedUpEvent, 1, in buffer);
        }
    }
}
