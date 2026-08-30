using Game.ECS;

namespace Com.Fps.Weapon
{
    /// <summary>武器状态（挂在玩家实体上，由本 Mod 懒初始化；服务端内部，不经 ECS 复制）。</summary>
    public struct WeaponState : IComponent
    {
        public int Ammo;
        public int MaxAmmo;
        public float Cooldown;
        public float ReloadT;
    }

    /// <summary>开火意图（由 weapon:fire 协议写入，系统消费后移除）。</summary>
    public struct FireIntent : IComponent { public float Yaw; }

    /// <summary>武器参数。</summary>
    public static class WeaponConfig
    {
        public const int MaxAmmo = 30;
        public const float FireInterval = 0.25f;
        public const float ReloadDuration = 1.0f;
        public const int Damage = 34;
        public const float HitRadius = 0.7f;
        public const float ChestHeight = 0.4f;   // 命中球中心（胸口）相对胶囊体中心的偏移
        public const float Range = 30f;
        public const float EyeHeight = 0.6f;
    }
}
