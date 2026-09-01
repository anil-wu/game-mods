namespace Com.Zombtoy.Item
{
    /// <summary>
    /// 道具行为参数（契约 §9 配置常量表；结构固定、数值可调，改动需升 CONTRACT 版本与测试向量，summary §0.8）。
    /// 数值来源（只读机制数值，不搬代码）：原版 ItemManager（InvokeRepeating("Spawn", 10, ...) /
    /// MaximumItemNumber=3 / spawnTime 默认 5）、HealthPotion（heal 值）、AmmoItem（每槽 +1 满弹匣）。
    /// 默认配置在资源迁移时按 prefab 校准（summary §0.8）。
    /// </summary>
    public static class ItemConfig
    {
        /// <summary>首刷延迟（契约 §9：ItemManager InvokeRepeating("Spawn", 10, ...)）。</summary>
        public const float FirstDelay = 10f;

        /// <summary>后续生成间隔随机区间 [MinInterval, MaxInterval]（契约 §9：spawnTime 默认 5，随机 [5,15]s）。</summary>
        public const float MinInterval = 5f;
        public const float MaxInterval = 15f;

        /// <summary>场上道具上限（契约 §9：MaximumItemNumber，prefab 默认 3）。</summary>
        public const int MaxItems = 3;

        /// <summary>血瓶治疗量（契约 §9：heal 值，prefab 默认 30）。</summary>
        public const int HealthPotionAmount = 30;

        /// <summary>弹药箱补弹（契约 §9：每槽 +1 满弹匣，AmmoItem 行为）。</summary>
        public const int AmmoBoxAmount = 1;

        // ---- 出生点（Rule 3 本 Mod 自持；与 game Mod Level 场景标记保持一致，契约 §9 同步约束，同 enemy §9） ----
        /// <summary>道具轮转出生点（契约 §3：ItemSpawnSystem 按 NextSpawnIndex 轮转取点）。</summary>
        public static readonly (float X, float Y, float Z)[] SpawnPoints =
        {
            (8f, 0f, 8f), (-8f, 0f, 8f), (8f, 0f, -8f), (-8f, 0f, -8f),
            (0f, 0f, 12f), (12f, 0f, 0f), (0f, 0f, -12f), (-12f, 0f, 0f),
        };
    }
}
