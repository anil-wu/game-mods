using System;
using Game.ECS;

namespace Com.Fps.Player
{
    /// <summary>移动系统（服务端权威）：读取输入 → 积分位置/速度 → 结果经 Replication 回客户端。</summary>
    public sealed class MovementSystem : ISystem
    {
        public void Update(SystemContext ctx)
        {
            foreach (var (e, input, pos, vel, health) in ctx.Query<PlayerInput, Position3, Velocity3, Health>())
            {
                if (health.Current <= 0) continue; // 死亡不移动

                var p = pos;
                var v = vel;

                // 偏航系移动（前进/横移）
                var yawRad = input.Yaw * (float)(Math.PI / 180.0);
                var fx = (float)Math.Sin(yawRad);
                var fz = (float)Math.Cos(yawRad);
                var rx = fz;   // right = forward × up 的 2D 形式
                var rz = -fx;
                var mx = input.MoveX;
                var mz = input.MoveZ;
                var len = (float)Math.Sqrt(mx * mx + mz * mz);
                if (len > 1f) { mx /= len; mz /= len; }

                v.X = (fx * mz + rx * mx) * PlayerConfig.MoveSpeed;
                v.Z = (fz * mz + rz * mx) * PlayerConfig.MoveSpeed;

                // 重力 + 跳跃 + 地面钳制（平面游乐场）
                v.Y -= PlayerConfig.Gravity * ctx.DeltaTime;
                var grounded = p.Y <= PlayerConfig.GroundY + 0.001f;
                if (grounded && input.Jump) v.Y = PlayerConfig.JumpSpeed;

                p.X += v.X * ctx.DeltaTime;
                p.Y += v.Y * ctx.DeltaTime;
                p.Z += v.Z * ctx.DeltaTime;
                if (p.Y < PlayerConfig.GroundY) { p.Y = PlayerConfig.GroundY; v.Y = 0f; }

                ctx.World.Add(e, p);
                ctx.World.Add(e, v);
                // 朝向随输入 yaw（复制给客户端，角色面朝移动方向）
                if (ctx.World.Has<Facing>(e))
                    ctx.World.Add(e, new Facing { Yaw = input.Yaw });
            }
        }
    }

    /// <summary>重生系统（服务端权威）：死亡挂 RespawnTimer，计时到即满血回出生点。</summary>
    public sealed class RespawnSystem : ISystem
    {
        private readonly Func<uint, (float X, float Y, float Z)> _spawnPointOf;

        public RespawnSystem(Func<uint, (float, float, float)> spawnPointOf)
            => _spawnPointOf = spawnPointOf;

        public void Update(SystemContext ctx)
        {
            foreach (var (e, timer) in ctx.Query<RespawnTimer>())
            {
                var t = timer;
                t.T -= ctx.DeltaTime;
                if (t.T > 0f)
                {
                    ctx.World.Add(e, t);
                    continue;
                }

                // 重生：满血 + 回出生点 + 清计时
                var health = ctx.World.Get<Health>(e);
                health.Current = health.Max;
                ctx.World.Add(e, health);

                var (x, y, z) = _spawnPointOf(e.Id);
                ctx.World.Add(e, new Position3 { X = x, Y = y, Z = z });
                if (ctx.World.Has<Velocity3>(e))
                    ctx.World.Add(e, new Velocity3());
                ctx.World.Remove<RespawnTimer>(e);
            }
        }
    }

    /// <summary>本地玩家指派（S2C 广播 player:local）：网络就绪后发送一次。</summary>
    public sealed class AssignLocalSystem : ISystem
    {
        private readonly global::Game.Mod.Runtime.IModContext _context;
        private readonly uint _networkId;
        private bool _sent;

        public AssignLocalSystem(global::Game.Mod.Runtime.IModContext context, uint networkId)
        {
            _context = context;
            _networkId = networkId;
        }

        public void Update(SystemContext context)
        {
            if (_sent || !_context.Network.IsActive) return;
            _sent = true;
            _context.Network.Broadcast(new LocalProtocol().Id, new LocalAssigned(_networkId));
        }
    }
}
