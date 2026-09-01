using Game.ECS;

namespace Com.Zombtoy.Weapon
{
    /// <summary>
    /// 武器 ECS 组件（契约 §2，仅本 Mod 读写，其他 Mod 禁止触碰 World）。
    /// 单机 Host：无 [NetworkReplicated] 标签（summary §0.1）。
    /// 组件类型在 Register 里经 context.Ecs.RegisterComponent 注册；
    /// 武器槽位实体（×4）+ 武器域实体在 Register 创建（常驻，契约 §2 注），
    /// 玩家实体经 player:get_entity 关联（写 WeaponOwner）。
    /// 视图（FireView/WeaponView）经静态桥写 ShotRequest/SwitchRequest/TornadoRequest，
    /// 系统消费后帧末清空（契约 §3 序 1）。
    /// </summary>

    /// <summary>武器类别（契约 §1：编号即契约，各 Mod 重复定义；跨边界传 u32 varint，数字即契约 §0.4）。</summary>
    public enum WeaponKind : byte
    {
        MachineGun = 0,
        Shotgun = 1,
        RocketLauncher = 2,
        CryoPistol = 3,
        Tornado = 4, // 副手特殊能力，非槽位
    }

    /// <summary>伤害来源分类（与 enemy CONTRACT §0.7 一致，重复定义）：0=Default（射线/接触）1=Blast（爆炸）2=Ice（冰）。</summary>
    public enum SourceKind : byte
    {
        Default = 0,
        Blast = 1,
        Ice = 2,
    }

    /// <summary>武器槽位（槽位实体 ×4，契约 §2）：Index 槽位号 / Kind 武器类别。</summary>
    public struct WeaponSlot : IComponent
    {
        public byte Index;
        public byte Kind;
    }

    /// <summary>弹药状态（每槽位实体一份，契约 §2）：InMag 弹匣内 / Reserve 备弹 / MaxMag 弹匣容量 /
    /// IsReloading 换弹中 / ReloadTimer 换弹剩余 / FireCooldown 开火冷却剩余。</summary>
    public struct WeaponAmmo : IComponent
    {
        public int InMag;
        public int Reserve;
        public int MaxMag;
        public bool IsReloading;
        public float ReloadTimer;
        public float FireCooldown;
    }

    /// <summary>当前选中槽（单实体 = 武器域实体，契约 §2）：CurrentIndex 当前槽 / Count 槽位数。</summary>
    public struct WeaponActive : IComponent
    {
        public byte CurrentIndex;
        public byte Count;
    }

    /// <summary>武器域 → 玩家（Register 时经 player:get_entity 填充，契约 §2/§7）。</summary>
    public struct WeaponOwner : IComponent
    {
        public uint PlayerEntityId;
    }

    /// <summary>射击请求（视图写入、FireSystem 消费，单槽帧末清空，契约 §2）：
    /// ShooterId 射手 / HitEntityId 命中敌人（0=未命中）/ IsHit 是否命中 / HitX,Y,Z 命中点（或射程终点）/
    /// Kind 开火武器类别（视图写入，系统按当前槽位权威分发）。</summary>
    public struct ShotRequest : IComponent
    {
        public uint ShooterId;
        public uint HitEntityId;
        public bool IsHit;
        public float HitX, HitY, HitZ;
        public byte Kind;
    }

    /// <summary>切换请求（视图写入 1-4 键，WeaponSwitchSystem 消费，契约 §2）。</summary>
    public struct SwitchRequest : IComponent
    {
        public byte Index;
    }

    /// <summary>投射物逻辑实体（M2：Rocket/IceBullet/Tornado，契约 §2）：Kind 类别 / OwnerId 射手 /
    /// X,Y,Z 位置 / DirX,DirY,DirZ 朝向 / Damage 伤害 / Life 剩余生命 / State 状态（预留）。</summary>
    public struct Projectile : IComponent
    {
        public byte Kind;
        public uint OwnerId;
        public float X, Y, Z;
        public float DirX, DirY, DirZ;
        public float Damage;
        public float Life;
        public byte State;
    }

    /// <summary>龙卷风请求（M2，契约 §8 视图约定：Fire2 → 写 TornadoRequest → ProjectileSystem 生成 Tornado 实体）。
    /// 契约 §2 组件表之外的本 Mod 内部请求组件（仅本 Mod 读写，不涉及任何线格式）。</summary>
    public struct TornadoRequest : IComponent
    {
        public float X, Y, Z;     // 生成点（玩家面前）
        public float DirX, DirZ;  // 移动方向（视图相机朝向，水平面）
    }
}
