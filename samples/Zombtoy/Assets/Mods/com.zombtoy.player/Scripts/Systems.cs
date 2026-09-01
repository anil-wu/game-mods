using System;
using Game.ECS;

namespace Com.Zombtoy.Player
{
    /// <summary>
    /// 玩家周期系统（契约 §2，全部 SystemSide.Server：Host 单机 = 全逻辑端，无头测试用 UpdateAll）。
    /// 伤害/治疗不走系统：player:damage / player:heal 在能力实现内同步执行并当场发消息（契约 §0.5）；
    /// 系统只做周期行为（移动/体力）。
    /// 更新顺序（按注册序）：InputApply → Movement → Stamina。
    /// </summary>

    /// <summary>
    /// 输入合成速度（序 1）：MoveX/Z 归一化 × 速度；Sprint 且体力&gt;0 → ×SprintMultiplier。
    /// 死亡不产生速度（Velocity3 清零，逻辑位置停止）。
    /// </summary>
    public sealed class PlayerInputApplySystem : ISystem
    {
        public void Update(SystemContext ctx)
        {
            foreach (var (e, input, flags, stamina) in ctx.Query<PlayerInput, PlayerFlags, Stamina>())
            {
                if (flags.IsDead)
                {
                    ctx.World.Add(e, new Velocity3()); // 死亡：零速度
                    continue;
                }

                // 归一化：斜向移动（两轴同按）长度 &gt;1 时缩放，不超速
                var mx = input.MoveX;
                var mz = input.MoveZ;
                var len = (float)Math.Sqrt(mx * mx + mz * mz);
                if (len > 1f) { mx /= len; mz /= len; }

                // 冲刺：体力 >0 才生效（体力由 PlayerStaminaSystem 每帧结算）
                var speed = PlayerConfig.MoveSpeed;
                if (input.Sprint && stamina.Current > 0f)
                    speed *= PlayerConfig.SprintMultiplier;

                ctx.World.Add(e, new Velocity3 { X = mx * speed, Z = mz * speed });
            }
        }
    }

    /// <summary>
    /// 位移积分（序 2）：Position += Velocity * dt（逻辑权威；视图每帧读 Position3 同步 Transform）。
    /// </summary>
    public sealed class PlayerMovementSystem : ISystem
    {
        public void Update(SystemContext ctx)
        {
            foreach (var (e, vel) in ctx.Query<Velocity3>())
            {
                if (!ctx.World.TryGet<Position3>(e, out var pos)) continue;
                pos.X += vel.X * ctx.DeltaTime;
                pos.Y += vel.Y * ctx.DeltaTime;
                pos.Z += vel.Z * ctx.DeltaTime;
                ctx.World.Add(e, pos);
            }
        }
    }

    /// <summary>
    /// 体力结算（序 3）：冲刺耗 0.5/s，非冲刺恢复 0.15/s（≤Max 封顶）；
    /// 体力耗尽强制取消冲刺（写回 PlayerInput.Sprint=false）；
    /// 数值变化时经 Sink 通知（由 PlayerMod.Register 注入：发布 player:StaminaChanged，契约 §4）。
    /// </summary>
    public sealed class PlayerStaminaSystem : ISystem
    {
        /// <summary>体力耗尽判定阈值（消除 float 累减误差：1 - 40*0.025 残留 ~4e-7）。</summary>
        private const float StaminaEpsilon = 1e-4f;

        /// <summary>体力变化通知（entityId, current, max）。</summary>
        public Action<uint, float, float>? OnStaminaChanged;

        public void Update(SystemContext ctx)
        {
            foreach (var (e, input, flags) in ctx.Query<PlayerInput, PlayerFlags>())
            {
                if (flags.IsDead) continue; // 死亡：体力冻结
                if (!ctx.World.TryGet<Stamina>(e, out var st)) continue;

                var changed = false;
                if (input.Sprint && st.Current > 0f)
                {
                    st.Current -= PlayerConfig.StaminaDrainPerSec * ctx.DeltaTime;
                    if (st.Current <= StaminaEpsilon)
                    {
                        st.Current = 0f;
                        var i = input;
                        i.Sprint = false; // 体力耗尽强制取消冲刺
                        ctx.World.Add(e, i);
                    }
                    changed = true;
                }
                else if (st.Current < st.Max)
                {
                    st.Current += PlayerConfig.StaminaRegenPerSec * ctx.DeltaTime;
                    if (st.Current > st.Max) st.Current = st.Max;
                    changed = true;
                }

                ctx.World.Add(e, st);
                if (changed) OnStaminaChanged?.Invoke(e.Id, st.Current, st.Max);
            }
        }
    }
}
