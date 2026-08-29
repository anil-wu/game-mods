using System;
using Game.ECS;

namespace Com.Sample.PushBox
{
    /// <summary>
    /// 游戏参数。完整实现应从 data/arena.json 加载（数据驱动），此处为演示常量。
    /// </summary>
    public static class GameConfig
    {
        /// <summary>场地半宽 [-10, 10]。</summary>
        public const float ArenaHalf = 10f;

        /// <summary>推入此距离判胜。</summary>
        public const float WinDistance = 7f;

        /// <summary>单方推力加速度。</summary>
        public const float PushForce = 8f;

        /// <summary>阻尼（无推力时减速）。</summary>
        public const float Damping = 1.5f;

        /// <summary>限速。</summary>
        public const float MaxSpeed = 5f;
    }

    /// <summary>推力系统：汇总双方推力 → 更新箱子速度与位置。服务端权威。</summary>
    public sealed class PushSystem : ISystem
    {
        public void Update(SystemContext ctx)
        {
            foreach (var (_, session) in ctx.Query<Session>())
            {
                if (session.Phase != GamePhase.Running) continue;

                var box = new Entity(session.Box);
                var pos = ctx.World.Get<Position>(box);
                var vel = ctx.World.Get<Velocity>(box);

                // 汇总推力：A(Left) 推 +1 向右，B(Right) 推 -1 向左
                float net = 0;
                net += ReadPush(ctx.World, new Entity(session.Left));
                net += ReadPush(ctx.World, new Entity(session.Right));

                vel.V += net * GameConfig.PushForce * ctx.DeltaTime;
                vel.V *= 1f - GameConfig.Damping * ctx.DeltaTime;
                vel.V = Math.Max(-GameConfig.MaxSpeed, Math.Min(GameConfig.MaxSpeed, vel.V));

                pos.X += vel.V * ctx.DeltaTime;
                pos.X = Math.Max(-GameConfig.ArenaHalf, Math.Min(GameConfig.ArenaHalf, pos.X));

                ctx.World.Add(box, pos);
                ctx.World.Add(box, vel);
            }
        }

        private static float ReadPush(World world, Entity player)
            => world.Has<PushIntent>(player) ? world.Get<PushIntent>(player).Direction : 0f;
    }

    /// <summary>
    /// 胜负判定系统：箱子进入对方区域即判胜。服务端权威。
    /// 网络广播 / 事件通知交给 Mod 注入的 Sink（系统不直接持有网络/消息上下文）。
    /// </summary>
    public sealed class WinCheckSystem : ISystem
    {
        /// <summary>判胜回调（由 PushBoxMod 在 Register 时注入：网络广播 + 本地事件）。</summary>
        public Action<PlayerSide>? OnGameWon;

        public void Update(SystemContext ctx)
        {
            foreach (var (sessionEntity, session) in ctx.Query<Session>())
            {
                if (session.Phase != GamePhase.Running) continue;

                // foreach 解构变量只读，复制到可变局部变量再改
                var s = session;
                var pos = ctx.World.Get<Position>(new Entity(s.Box));
                if (pos.X >= GameConfig.WinDistance)
                {
                    s.Phase = GamePhase.Won;
                    s.Winner = PlayerSide.Left;   // 箱子进入右侧(B)区域，A 胜
                }
                else if (pos.X <= -GameConfig.WinDistance)
                {
                    s.Phase = GamePhase.Won;
                    s.Winner = PlayerSide.Right;  // 箱子进入左侧(A)区域，B 胜
                }

                ctx.World.Add(sessionEntity, s);

                if (s.Phase == GamePhase.Won)
                    OnGameWon?.Invoke(s.Winner);
            }
        }
    }
}
