using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Weapon
{
    /// <summary>
    /// 能力 ABI 形状（Rule 19 进程内务实形态）：跨 Mod 参数/返回只用 BCL 纯数据——
    /// 基元 + object?[] 元组（字段布局即契约，见 CONTRACT.md §4/§10），双方各自按文档解析，
    /// 零自定义类型共享；跨边界经 DataCodec 编解码（Rule 14，bytes 不藏对象引用）。
    /// 本类内的 TryRead 供 owner 自校验、系统（FireSystem/ProjectileSystem）与测试参考；
    /// 消费方（item.ItemPickup / game.GameFlow / ui.HudBinders 等）各自重复定义（Rule 19）。
    /// </summary>
    public static class CapabilityShapes
    {
        // ---- 本 Mod 导出能力入参构建（owner 编码，测试向量/内部调用） ----

        /// <summary>weapon:add_ammo 入参：[0]=magazines(i32)。</summary>
        public static object?[] AddAmmoArgs(int magazines) => new object?[] { magazines };

        /// <summary>weapon:get_state 行：[0]=slot(u32) [1]=slotCount(u32) [2]=inMag(i32) [3]=reserve(i32) [4]=maxMag(i32)。</summary>
        public static object?[] GetStateRow(uint slot, uint slotCount, int inMag, int reserve, int maxMag)
            => new object?[] { slot, slotCount, inMag, reserve, maxMag };

        /// <summary>weapon:fire 入参（= weapon_shot_request 形状，契约 §10）：[0]=shooter(u32) [1]=hit(u32)
        /// [2]=isHit(bool) [3]=hitX(f32) [4]=hitY(f32) [5]=hitZ(f32) [6]=kind(u32)。</summary>
        public static object?[] ShotRequestArgs(uint shooter, uint hit, bool isHit, float hitX, float hitY, float hitZ, uint kind)
            => new object?[] { shooter, hit, isHit, hitX, hitY, hitZ, kind };

        // ---- 本 Mod 能力入参解析（owner 自校验） ----

        /// <summary>weapon:add_ammo 入参解析；缺省（null）/ 解析失败由调用方按契约 §4 视为 magazines=1。</summary>
        public static bool TryReadAddAmmoArgs(object? args, out int magazines)
        {
            magazines = 0;
            if (args is null) return false; // 缺省 = 每槽 +1 满弹匣（契约 §4）
            if (args is not object[] a || a.Length < 1) return false;
            if (a[0] is not int m) return false;
            magazines = m;
            return true;
        }

        /// <summary>weapon:fire 入参解析（weapon_shot_request 形状）。</summary>
        public static bool TryReadShotRequestArgs(object? args, out uint shooter, out uint hit, out bool isHit,
            out float hitX, out float hitY, out float hitZ, out uint kind)
        {
            shooter = 0; hit = 0; isHit = false; hitX = 0; hitY = 0; hitZ = 0; kind = 0;
            if (args is not object[] a || a.Length < 7) return false;
            if (a[0] is not uint s || a[1] is not uint h || a[2] is not bool ih ||
                a[3] is not float x || a[4] is not float y || a[5] is not float z || a[6] is not uint k) return false;
            shooter = s; hit = h; isHit = ih; hitX = x; hitY = y; hitZ = z; kind = k;
            return true;
        }

        // ---- 消费的对端能力行解析（Rule 19：各自重复定义，不引用 player/enemy 程序集，契约 §7） ----

        /// <summary>player:get_entity 行：[0]=entityId(u32)（消费方：WeaponMod.Register，契约 §7）。</summary>
        public static bool TryReadPlayerEntity(object? row, out uint entityId)
        {
            entityId = 0;
            if (row is not object[] a || a.Length < 1) return false;
            if (a[0] is not uint id) return false;
            entityId = id;
            return true;
        }

        /// <summary>player:get_position 行：[0]=x(f32) [1]=y(f32) [2]=z(f32) [3]=alive(bool)（消费方：FireSystem 存活判定/投射物原点）。</summary>
        public static bool TryReadPlayerPosition(object? row, out float x, out float y, out float z, out bool alive)
        {
            x = 0; y = 0; z = 0; alive = false;
            if (row is not object[] a || a.Length < 4) return false;
            if (a[0] is not float fx || a[1] is not float fy || a[2] is not float fz || a[3] is not bool al) return false;
            x = fx; y = fy; z = fz; alive = al;
            return true;
        }

        /// <summary>enemy:get_all 行：[0]=id(u32) [1]=x(f32) [2]=y(f32) [3]=z(f32) [4]=kind(u32) [5]=alive(bool)
        /// [6]=hp(i32) [7]=maxHp(i32)（消费方：ProjectileSystem 火箭爆炸/冰弹追踪/龙卷风 tick）。</summary>
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
    }

    /// <summary>
    /// 本 Mod 发布消息的载荷编码器（字段编号线格式，CONTRACT.md §5；summary §0.3）。
    /// owner 唯一事实来源：能力实现/系统发布与测试向量（WeaponTestVectors）共用，
    /// 保证"向量样例字节 = 线上字节"（§14.11.3）。
    /// </summary>
    public static class WeaponMessagePayload
    {
        /// <summary>weapon:AmmoChanged v1：[1]=slot(u32 varint) [2]=inMag(i32 varint) [3]=reserve(i32 varint) [4]=maxMag(i32 varint)。</summary>
        public static PayloadBuffer AmmoChanged(uint slot, int inMag, int reserve, int maxMag)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, slot);
            w.WriteInt32(2, inMag);
            w.WriteInt32(3, reserve);
            w.WriteInt32(4, maxMag);
            return w.ToBuffer();
        }

        /// <summary>weapon:WeaponSwitched v1：[1]=slot(u32 varint)。</summary>
        public static PayloadBuffer WeaponSwitched(uint slot)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, slot);
            return w.ToBuffer();
        }
    }
}
