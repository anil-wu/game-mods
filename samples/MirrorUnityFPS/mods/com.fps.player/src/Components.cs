using Game.ECS;

namespace Com.Fps.Player
{
    /// <summary>3D 位置（复制，§11.10）。</summary>
    public struct Position3 : IComponent { public float X, Y, Z; }

    /// <summary>3D 速度（服务端内部，不复制）。</summary>
    public struct Velocity3 : IComponent { public float X, Y, Z; }

    /// <summary>最新输入状态（服务端内部：由 player:input 协议写入）。</summary>
    public struct PlayerInput : IComponent
    {
        public float MoveX;   // 左右 [-1,1]
        public float MoveZ;   // 前后 [-1,1]
        public float Yaw;     // 朝向（度，客户端上报，合作原型信任级）
        public bool Jump;
    }

    /// <summary>生命（复制）。Current<=0 即死亡。</summary>
    public struct Health : IComponent { public int Current; public int Max; }

    /// <summary>玩家标记（复制）：Dummy=静态靶标；否则为可操作玩家。</summary>
    public struct PlayerTag : IComponent { public bool IsDummy; }

    /// <summary>朝向（复制，度）：角色模型面朝方向（由服务端输入 yaw 写入）。</summary>
    public struct Facing : IComponent { public float Yaw; }

    /// <summary>重生计时（服务端内部）：死亡后挂上，计时到即重生。</summary>
    public struct RespawnTimer : IComponent { public float T; }

    /// <summary>游戏参数（后续应迁移 data/ 配置驱动）。</summary>
    public static class PlayerConfig
    {
        public const float MoveSpeed = 4.5f;
        public const float JumpSpeed = 4.5f;
        public const float Gravity = 9.8f;
        public const float GroundY = 0.5f;   // 胶囊体中心离地高度
        public const int MaxHealth = 100;
        public const float RespawnDelay = 3f;
    }
}
