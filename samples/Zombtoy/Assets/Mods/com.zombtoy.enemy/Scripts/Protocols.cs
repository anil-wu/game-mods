using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 能力 ABI 形状（Rule 19 进程内务实形态）：跨 Mod 参数/返回只用 BCL 纯数据——
    /// 基元 + object?[] 元组（字段布局即契约，见 CONTRACT.md §4/§10），双方各自按文档解析，
    /// 零自定义类型共享；跨边界经 DataCodec 编解码（Rule 14，bytes 不藏对象引用）。
    /// 本类内的 TryRead 供 owner 自校验、系统（追击/攻击/生成）与测试参考；
    /// 消费方（weapon.FireSystem / game.GameFlow / ui.HudZombies / score.ScoreSystem 等）各自重复定义（Rule 19）。
    /// </summary>
    public static class CapabilityShapes
    {
        // ---- 本 Mod 导出能力入参构建（owner 编码，测试向量/内部调用） ----

        /// <summary>enemy:damage 入参：[0]=target(u32) [1]=amount(i32) [2]=source(u32) [3]=sourceKind(u32)。</summary>
        public static object?[] DamageArgs(uint target, int amount, uint source, uint sourceKind)
            => new object?[] { target, amount, source, sourceKind };

        /// <summary>enemy:apply_slow 入参：[0]=target(u32) [1]=speedMul(f32) [2]=duration(f32)。</summary>
        public static object?[] SlowArgs(uint target, float speedMul, float duration)
            => new object?[] { target, speedMul, duration };

        /// <summary>enemy:set_spawning 入参：[0]=enabled(bool)。</summary>
        public static object?[] SetSpawningArgs(bool enabled) => new object?[] { enabled };

        /// <summary>enemy:get_count 行：[0]=count(i32)。</summary>
        public static object?[] GetCountRow(int count) => new object?[] { count };

        /// <summary>enemy:get_all 行：[0]=id(u32) [1]=x(f32) [2]=y(f32) [3]=z(f32) [4]=kind(u32) [5]=alive(bool) [6]=hp(i32) [7]=maxHp(i32)。</summary>
        public static object?[] GetAllRow(uint id, float x, float y, float z, uint kind, bool alive, int hp, int maxHp)
            => new object?[] { id, x, y, z, kind, alive, hp, maxHp };

        // ---- 本 Mod 能力入参解析（owner 自校验） ----

        /// <summary>enemy:damage 入参解析。sourceKind 为 v1.1 追加：老调用方不传即默认 0（契约 §4 append-only）。</summary>
        public static bool TryReadDamageArgs(object? args, out uint target, out int amount, out uint source, out uint sourceKind)
        {
            target = 0; amount = 0; source = 0; sourceKind = 0;
            if (args is not object[] a || a.Length < 3) return false;
            if (a[0] is not uint t || a[1] is not int amt || a[2] is not uint src) return false;
            target = t; amount = amt; source = src;
            if (a.Length >= 4 && a[3] is uint sk) sourceKind = sk;
            return true;
        }

        /// <summary>enemy:apply_slow 入参解析。</summary>
        public static bool TryReadSlowArgs(object? args, out uint target, out float speedMul, out float duration)
        {
            target = 0; speedMul = 1f; duration = 0f;
            if (args is not object[] a || a.Length < 3) return false;
            if (a[0] is not uint t || a[1] is not float sm || a[2] is not float d) return false;
            target = t; speedMul = sm; duration = d;
            return true;
        }

        /// <summary>enemy:set_spawning 入参解析。</summary>
        public static bool TryReadSetSpawningArgs(object? args, out bool enabled)
        {
            enabled = false;
            if (args is not object[] a || a.Length < 1) return false;
            if (a[0] is not bool e) return false;
            enabled = e;
            return true;
        }

        /// <summary>enemy:get_all 行解析（消费方：weapon.IceBullet，本 Mod 测试参考）。</summary>
        public static bool TryReadGetAllRow(object? row, out uint id, out float x, out float y, out float z,
            out uint kind, out bool alive, out int hp, out int maxHp)
        {
            id = 0; x = 0; y = 0; z = 0; kind = 0; alive = false; hp = 0; maxHp = 0;
            if (row is not object[] a || a.Length < 8) return false;
            if (a[0] is not uint i || a[1] is not float fx || a[2] is not float fy || a[3] is not float fz ||
                a[4] is not uint k || a[5] is not bool al || a[6] is not int h || a[7] is not int m) return false;
            id = i; x = fx; y = fy; z = fz; kind = k; alive = al; hp = h; maxHp = m;
            return true;
        }

        // ---- 消费的对端能力行解析（Rule 19：各自重复定义，不引用 player 程序集，契约 §7） ----

        /// <summary>player:get_position 行：[0]=x(f32) [1]=y(f32) [2]=z(f32) [3]=alive(bool)（消费方：EnemyChaseSystem / EnemySpawnSystem）。</summary>
        public static bool TryReadPlayerPosition(object? row, out float x, out float y, out float z, out bool alive)
        {
            x = 0; y = 0; z = 0; alive = false;
            if (row is not object[] a || a.Length < 4) return false;
            if (a[0] is not float fx || a[1] is not float fy || a[2] is not float fz || a[3] is not bool al) return false;
            x = fx; y = fy; z = fz; alive = al;
            return true;
        }

        /// <summary>player:get_entity 行：[0]=entityId(u32)（消费方：EnemyMod.Register，契约 §7）。</summary>
        public static bool TryReadPlayerEntity(object? row, out uint entityId)
        {
            entityId = 0;
            if (row is not object[] a || a.Length < 1) return false;
            if (a[0] is not uint id) return false;
            entityId = id;
            return true;
        }
    }

    /// <summary>
    /// 本 Mod 发布消息的载荷编码器（字段编号线格式，CONTRACT.md §5；summary §0.3）。
    /// owner 唯一事实来源：能力实现发布与测试向量（EnemyTestVectors）共用，
    /// 保证"向量样例字节 = 线上字节"（§14.11.3）。
    /// </summary>
    public static class EnemyMessagePayload
    {
        /// <summary>enemy:EnemyKilled v1：[1]=enemyType(u32 varint) [2]=scoreValue(i32 varint) [3..5]=xyz(f32 fixed32)。</summary>
        public static PayloadBuffer EnemyKilled(byte enemyType, int scoreValue, float x, float y, float z)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, enemyType);
            w.WriteInt32(2, scoreValue);
            w.WriteFloat(3, x);
            w.WriteFloat(4, y);
            w.WriteFloat(5, z);
            return w.ToBuffer();
        }

        /// <summary>enemy:ZombieCountChanged v1：[1]=count(i32 varint)。</summary>
        public static PayloadBuffer ZombieCountChanged(int count)
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, count);
            return w.ToBuffer();
        }
    }
}
