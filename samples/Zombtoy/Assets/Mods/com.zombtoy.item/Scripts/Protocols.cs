using Game.Mod.Contract;
using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Item
{
    /// <summary>
    /// 能力 ABI 形状（Rule 19 进程内务实形态）：跨 Mod 参数/返回只用 BCL 纯数据——
    /// 基元 + object?[] 元组（字段布局即契约，见 CONTRACT.md §4/§10），双方各自按文档解析，
    /// 零自定义类型共享；跨边界经 DataCodec 编解码（Rule 14，bytes 不藏对象引用）。
    /// 本类内的 TryRead 供 owner 自校验、系统（ItemSpawnSystem）与测试参考；
    /// 消费方（game.GameFlow / ui.HudPickupHint 等）各自重复定义（Rule 19）。
    /// </summary>
    public static class CapabilityShapes
    {
        // ---- 本 Mod 导出能力入参构建（owner 编码，测试向量/内部调用） ----

        /// <summary>item:pickup 入参：[0]=itemEntityId(u32)。</summary>
        public static object?[] PickupArgs(uint itemEntityId) => new object?[] { itemEntityId };

        /// <summary>item:set_spawning 入参：[0]=enabled(bool)。</summary>
        public static object?[] SetSpawningArgs(bool enabled) => new object?[] { enabled };

        /// <summary>item:pickup 返回：[0]=applied(bool) [1]=kind(u32)（契约 §4）。</summary>
        public static object?[] PickupResult(bool applied, uint kind) => new object?[] { applied, kind };

        // ---- 本 Mod 能力入参解析（owner 自校验） ----

        /// <summary>item:pickup 入参解析。</summary>
        public static bool TryReadPickupArgs(object? args, out uint itemEntityId)
        {
            itemEntityId = 0;
            if (args is not object[] a || a.Length < 1) return false;
            if (a[0] is not uint id) return false;
            itemEntityId = id;
            return true;
        }

        /// <summary>item:set_spawning 入参解析。</summary>
        public static bool TryReadSetSpawningArgs(object? args, out bool enabled)
        {
            enabled = false;
            if (args is not object[] a || a.Length < 1) return false;
            if (a[0] is not bool b) return false;
            enabled = b;
            return true;
        }

        // ---- 消费的对端能力行解析（Rule 19：各自重复定义，不引用 player/weapon 程序集，契约 §7） ----

        /// <summary>player:get_state 行：[0]=health(i32) [1]=max(i32) [2]=stamina(f32) [3]=maxStamina(f32) [4]=alive(bool)
        /// （消费方：ItemSpawnSystem 生成判定 + item:pickup 存活判定，契约 §7：玩家死亡不生成/不拾取）。</summary>
        public static bool TryReadPlayerState(object? row, out int health, out int max,
            out float stamina, out float maxStamina, out bool alive)
        {
            health = 0; max = 0; stamina = 0; maxStamina = 0; alive = false;
            if (row is not object[] a || a.Length < 5) return false;
            if (a[0] is not int h || a[1] is not int m || a[2] is not float s ||
                a[3] is not float ms || a[4] is not bool al) return false;
            health = h; max = m; stamina = s; maxStamina = ms; alive = al;
            return true;
        }
    }

    /// <summary>
    /// 本 Mod 发布消息的载荷编码器（字段编号线格式，CONTRACT.md §5；summary §0.3）。
    /// owner 唯一事实来源：能力实现发布与测试向量（ItemTestVectors）共用，
    /// 保证"向量样例字节 = 线上字节"（§14.11.3）。
    /// </summary>
    public static class ItemMessagePayload
    {
        /// <summary>item:PickedUp v1：[1]=kind(u32 varint) [2]=amount(i32 varint)（订阅者：ui 可选拾取提示，契约 §5）。</summary>
        public static PayloadBuffer PickedUp(uint kind, int amount)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, kind);
            w.WriteInt32(2, amount);
            return w.ToBuffer();
        }
    }
}
