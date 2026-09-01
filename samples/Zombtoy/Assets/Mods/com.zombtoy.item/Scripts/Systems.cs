using System;
using Game.ECS;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Item
{
    /// <summary>
    /// 道具周期系统（契约 §3，SystemSide.Server：Host 单机 = 全逻辑端，无头测试用 UpdateAll）。
    /// 拾取不走系统：item:pickup 在能力实现内同步完成（调用方需要 applied 结果，ModCall 语义，契约 §3 注）；
    /// 系统只做周期行为（定时生成）。
    /// </summary>

    /// <summary>
    /// 定时生成（序 1，契约 §3）：Enabled 且 ActiveCount&lt;MaxItems 且玩家存活 →
    /// 首延迟 10s、之后随机 [5,15]s 间隔 → 按 NextSpawnIndex 轮转出生点（ItemConfig.SpawnPoints 自持配置，
    /// ItemPosition 随实体写入，契约 §3"ItemPosition 自持配置"）→ 创建实体 + 通知视图实例化（契约 §8）。
    /// 道具类型随机（血瓶/弹药箱，对齐原版 ItemManager 随机抽）。
    /// </summary>
    public sealed class ItemSpawnSystem : ISystem
    {
        private readonly IModContext _context;
        private readonly Random _rng = new();

        public ItemSpawnSystem(IModContext context) => _context = context;

        public void Update(SystemContext ctx)
        {
            if (ItemMod.SpawnerEntityId == 0) return;
            var se = new Entity(ItemMod.SpawnerEntityId);
            if (!ctx.World.TryGet<ItemSpawner>(se, out var spawner)) return;

            // 开关（set_spawning，契约 §4）：关闭时冻结 = 暂停
            if (!spawner.Enabled) return;

            // 仅在玩家存活时生成（契约 §3 序 1：player:get_state 的 alive，契约 §7）
            if (!ItemMod.TryGetPlayerAlive(_context)) return;

            // 场上上限（契约 §3 序 1：ActiveCount < MaxItems 才生成）
            if (spawner.ActiveCount >= spawner.MaxItems) return;

            spawner.Timer -= ctx.DeltaTime;
            if (spawner.Timer > 0f)
            {
                ctx.World.Add(se, spawner);
                return;
            }

            // 轮转出生点（契约 §3：按 NextSpawnIndex 轮转；ItemConfig.SpawnPoints 自持配置，Rule 3 / 契约 §9）
            var points = ItemConfig.SpawnPoints;
            var point = points[spawner.NextSpawnIndex % points.Length];
            spawner.NextSpawnIndex = (spawner.NextSpawnIndex + 1) % points.Length;

            // 随机道具类型（血瓶/弹药箱，对齐原版 ItemManager 随机抽；Amount 取契约 §9 配置）
            var kind = _rng.Next(2) == 0 ? ItemKind.HealthPotion : ItemKind.AmmoBox;
            var amount = kind == ItemKind.HealthPotion ? ItemConfig.HealthPotionAmount : ItemConfig.AmmoBoxAmount;

            // 创建实体（归属本 ModObject，卸载强制回收 §9.2）+ 视图通知（契约 §8：ItemSpawnerView 实例化 prefab）
            var e = _context.Ecs.CreateEntity();
            ctx.World.Add(e, new ItemData { Kind = (byte)kind, Amount = amount });
            ctx.World.Add(e, new ItemState { Picked = false });
            ctx.World.Add(e, new ItemPosition { X = point.X, Y = point.Y, Z = point.Z });
            spawner.ActiveCount++;
            ItemMod.NotifyItemSpawned(e.Id);

            // 下次间隔随机 [5,15]s（契约 §9：spawnTime 默认 5）
            spawner.Timer = ItemConfig.MinInterval
                + (float)(_rng.NextDouble() * (ItemConfig.MaxInterval - ItemConfig.MinInterval));
            ctx.World.Add(se, spawner);
        }
    }
}
