using Game.Mod.Contract;
using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Player
{
    /// <summary>
    /// 能力 ABI 形状（Rule 19 进程内务实形态）：跨 Mod 参数/返回只用 BCL 纯数据——
    /// 基元 + object?[] 元组（字段布局即契约，见 CONTRACT.md §3），双方各自按文档解析，
    /// 零自定义类型共享；跨边界经 DataCodec 编解码（Rule 14，bytes 不藏对象引用）。
    /// 消费方解析器（enemy.EnemyAttackSystem / game.GameFlow / ui.HudBinders 等）各自重复定义（Rule 19），
    /// 本类内的 TryRead 仅供 owner 自校验与测试参考。
    /// </summary>
    public static class CapabilityShapes
    {
        /// <summary>player:damage 入参：[0]=amount(i32) [1]=source(u32)。</summary>
        public static object?[] DamageArgs(int amount, uint source) => new object?[] { amount, source };

        /// <summary>player:heal 入参：[0]=amount(i32)。</summary>
        public static object?[] HealArgs(int amount) => new object?[] { amount };

        /// <summary>player:reset 入参：[0]=x(f32) [1]=y(f32) [2]=z(f32)。</summary>
        public static object?[] ResetArgs(float x, float y, float z) => new object?[] { x, y, z };

        /// <summary>player:get_state 行：[0]=health(i32) [1]=max(i32) [2]=stamina(f32) [3]=maxStamina(f32) [4]=alive(bool)。</summary>
        public static object?[] StateRow(int health, int max, float stamina, float maxStamina, bool alive)
            => new object?[] { health, max, stamina, maxStamina, alive };

        /// <summary>player:get_position 行：[0]=x(f32) [1]=y(f32) [2]=z(f32) [3]=alive(bool)。</summary>
        public static object?[] PositionRow(float x, float y, float z, bool alive)
            => new object?[] { x, y, z, alive };

        /// <summary>damage 入参解析（消费方：enemy.EnemyAttackSystem.TryReadDamage）。</summary>
        public static bool TryReadDamage(object? args, out int amount, out uint source)
        {
            amount = 0; source = 0;
            if (args is not object[] a || a.Length < 2) return false;
            if (a[0] is not int amt || a[1] is not uint src) return false;
            amount = amt; source = src;
            return true;
        }

        /// <summary>heal 入参解析（消费方：item.ItemPickup.TryReadHeal）。</summary>
        public static bool TryReadHeal(object? args, out int amount)
        {
            amount = 0;
            if (args is not object[] a || a.Length < 1) return false;
            if (a[0] is not int amt) return false;
            amount = amt;
            return true;
        }

        /// <summary>reset 入参解析（消费方：game.GameFlow）。</summary>
        public static bool TryReadReset(object? args, out float x, out float y, out float z)
        {
            x = 0; y = 0; z = 0;
            if (args is not object[] a || a.Length < 3) return false;
            if (a[0] is not float fx || a[1] is not float fy || a[2] is not float fz) return false;
            x = fx; y = fy; z = fz;
            return true;
        }

        /// <summary>get_state 行解析（消费方：ui.HudBinders / enemy.EnemyChaseSystem）。</summary>
        public static bool TryReadStateRow(object? row, out int health, out int max,
            out float stamina, out float maxStamina, out bool alive)
        {
            health = 0; max = 0; stamina = 0; maxStamina = 0; alive = false;
            if (row is not object[] a || a.Length < 5) return false;
            if (a[0] is not int h || a[1] is not int m || a[2] is not float s ||
                a[3] is not float ms || a[4] is not bool al) return false;
            health = h; max = m; stamina = s; maxStamina = ms; alive = al;
            return true;
        }

        /// <summary>get_position 行解析（消费方：enemy.EnemyChaseSystem）。</summary>
        public static bool TryReadPositionRow(object? row, out float x, out float y, out float z, out bool alive)
        {
            x = 0; y = 0; z = 0; alive = false;
            if (row is not object[] a || a.Length < 4) return false;
            if (a[0] is not float fx || a[1] is not float fy || a[2] is not float fz || a[3] is not bool al) return false;
            x = fx; y = fy; z = fz; alive = al;
            return true;
        }
    }

    /// <summary>
    /// 本 Mod 发布消息的载荷编码器（字段编号线格式，CONTRACT.md §4；summary §0.3）。
    /// owner 唯一事实来源：能力实现发布与测试向量（PlayerTestVectors）共用，
    /// 保证"向量样例字节 = 线上字节"（§14.11.3）。
    /// </summary>
    public static class PlayerMessagePayload
    {
        /// <summary>player:HealthChanged v1：[1]=current(i32 varint) [2]=max(i32 varint)。</summary>
        public static PayloadBuffer HealthChanged(int current, int max)
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, current);
            w.WriteInt32(2, max);
            return w.ToBuffer();
        }

        /// <summary>player:StaminaChanged v1：[1]=current(f32 fixed32) [2]=max(f32 fixed32)。</summary>
        public static PayloadBuffer StaminaChanged(float current, float max)
        {
            var w = new PayloadWriter();
            w.WriteFloat(1, current);
            w.WriteFloat(2, max);
            return w.ToBuffer();
        }

        /// <summary>player:Died v1：[1]=source(u32 varint)。</summary>
        public static PayloadBuffer Died(uint source)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, source);
            return w.ToBuffer();
        }
    }
}
