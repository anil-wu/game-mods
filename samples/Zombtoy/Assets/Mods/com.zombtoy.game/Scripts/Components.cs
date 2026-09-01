using Game.ECS;

namespace Com.Zombtoy.Game
{
    /// <summary>
    /// 相位枚举（CONTRACT.md §1；编号即契约 u32，各 Mod 各自重复定义，Rule 19）。
    /// 本 Mod 只实际迁移到 Menu/Playing/Paused/GameOver；Result(4) 相位由 ui 收到 score:GameOver
    /// 后自行打开 Result 窗口承载（契约 §5 备注：本 Mod 不设 Result 相位转移，v2 append-only 预留）。
    /// </summary>
    public enum GamePhase : byte
    {
        Menu = 0,
        Playing = 1,
        Paused = 2,
        GameOver = 3,
        Result = 4, // v2 预留（契约 §5：如需"Result 专属相位"追加）
    }

    /// <summary>相位迁移原因（CONTRACT.md §1；编号即契约 u32，各 Mod 各自重复定义，Rule 19）。</summary>
    public enum GameReason : byte
    {
        Manual = 0,     // 初始/默认
        Started = 1,    // game:start 新对局
        Paused = 2,     // game:pause
        Resumed = 3,    // game:resume
        PlayerDied = 4, // score:GameOver 收尾
        ToMenu = 5,     // game:to_menu
    }

    /// <summary>
    /// 相位状态组件（CONTRACT.md §2；仅本 Mod 读写，其他 Mod 禁止触碰 World）。
    /// 单实体（Register 创建常驻，初始 Menu/Manual；相位迁移只改字段不销毁，契约 §2）。
    /// 组件类型在 Register 里经 context.Ecs.RegisterComponent 注册，卸载由 Core 强制回收（§9.2）。
    /// </summary>
    public struct GameState : IComponent
    {
        public byte Phase;  // GamePhase 编号
        public byte Reason; // GameReason 编号
    }
}
