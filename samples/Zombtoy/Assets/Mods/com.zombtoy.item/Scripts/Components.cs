using Game.ECS;

namespace Com.Zombtoy.Item
{
    /// <summary>
    /// 道具 ECS 组件（契约 §2，仅本 Mod 读写，其他 Mod 禁止触碰 World）。
    /// 单机 Host：无 [NetworkReplicated] 标签（summary §0.1）。
    /// 组件类型在 Register 里经 context.Ecs.RegisterComponent 注册；
    /// 道具实体由本 Mod 创建/销毁（ItemSpawnSystem 生成、item:pickup 能力消耗），
    /// 视图（ItemView）挂 EntityId（summary §0.6），玩家接触 → 静态桥调 item:pickup（契约 §8）。
    /// 拾取无独立系统：item:pickup 在能力实现内同步完成（调用方需要 applied 结果，ModCall 语义，契约 §3 注）。
    /// </summary>

    /// <summary>道具类别（契约 §1：编号即契约，各 Mod 重复定义；跨边界传 u32 varint，数字即契约 §0.4）。</summary>
    public enum ItemKind : byte
    {
        HealthPotion = 0, // 血瓶：Amount=治疗量
        AmmoBox = 1,      // 弹药箱：Amount=每槽 +1 满弹匣数
    }

    /// <summary>道具数据（契约 §2）：Kind 类别 / Amount 数量（血瓶=治疗量；弹药箱=每槽 +1 满弹匣数）。</summary>
    public struct ItemData : IComponent
    {
        public byte Kind;
        public int Amount;
    }

    /// <summary>拾取状态（契约 §2）：Picked 已拾取（防重复触发，item:pickup 成功后置位）。</summary>
    public struct ItemState : IComponent
    {
        public bool Picked;
    }

    /// <summary>生成点/逻辑位置（契约 §2）：ItemSpawnSystem 轮转出生点写入（ItemConfig.SpawnPoints 自持配置，
    /// Rule 3；与 game Mod Level 场景标记同步，契约 §9），视图按此放置。本组件即"ItemPosition 自持配置"（契约 §3 表）。</summary>
    public struct ItemPosition : IComponent
    {
        public float X, Y, Z;
    }

    /// <summary>生成器状态机（单实体 = 道具生成器实体，契约 §2）：Enabled 开关 / Timer 下次生成倒计时 /
    /// ActiveCount 存活道具计数 / MaxItems 场上上限 / NextSpawnIndex 轮转出生点游标。</summary>
    public struct ItemSpawner : IComponent
    {
        public bool Enabled;
        public float Timer;
        public int ActiveCount;
        public int MaxItems;
        public int NextSpawnIndex;
    }
}
