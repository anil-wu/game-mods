using Game.ECS;

namespace Com.Sample.PushBox
{
    /// <summary>玩家所属方：左(A) / 右(B)。</summary>
    public enum PlayerSide : byte
    {
        Left = 0,
        Right = 1,
    }

    /// <summary>游戏阶段。</summary>
    public enum GamePhase : byte
    {
        Running = 0,
        Won = 1,
    }

    // ---- 组件（状态只在这里） ----

    /// <summary>标记：箱子实体。</summary>
    public struct Box : IComponent { }

    /// <summary>1D 位置。</summary>
    public struct Position : IComponent { public float X; }

    /// <summary>1D 速度。</summary>
    public struct Velocity : IComponent { public float V; }

    /// <summary>玩家。</summary>
    public struct Player : IComponent { public PlayerSide Side; }

    /// <summary>推力意图：方向（+1 向右 / -1 向左）。</summary>
    public struct PushIntent : IComponent { public float Direction; }

    /// <summary>游戏会话（单例，持有实体引用与胜负状态）。</summary>
    public struct Session : IComponent
    {
        public uint Box;
        public uint Left;
        public uint Right;
        public GamePhase Phase;
        public PlayerSide Winner;
    }
}
