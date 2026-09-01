using System;
using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Game
{
    /// <summary>
    /// 能力 ABI 形状（Rule 19 进程内务实形态）：跨 Mod 参数/返回只用 BCL 纯数据——基元 + object?[] 元组
    /// （字段布局即契约，CONTRACT.md §4/§9），双方各自按文档解析；跨边界经 DataCodec 编解码（Rule 14）。
    /// 本类内的构建/解析方法供 owner 自校验、能力实现与测试参考；消费方（ui.MenuView 调 start、
    /// ui.ResultView/PauseView 调 to_menu/pause/resume、QA/调试调 status、各 View 解析 StateChanged）
    /// 各自重复定义（Rule 19）。
    /// </summary>
    public static class CapabilityShapes
    {
        // ---- 本 Mod 导出能力入参构建（owner 编码，测试向量/内部调用） ----

        /// <summary>game:start / to_menu / pause / resume 入参：[]（无参，契约 §4）。</summary>
        public static object?[] NoArgs() => Array.Empty<object?>();

        /// <summary>game:status 返回行：[0]=phase(u32)（契约 §4，QA/调试）。</summary>
        public static object?[] StatusRow(byte phase) => new object?[] { (uint)phase };

        // ---- 本 Mod 能力返回行解析（owner 自校验 / 消费方 QA 参考，Rule 19） ----

        /// <summary>game:status 返回行解析：[phase u32]。</summary>
        public static bool TryReadStatusRow(object? row, out uint phase)
        {
            phase = 0;
            if (row is not object[] a || a.Length < 1) return false;
            if (a[0] is not uint p) return false;
            phase = p;
            return true;
        }
    }

    /// <summary>
    /// 本 Mod 发布消息的载荷编码器（字段编号线格式，CONTRACT.md §5；summary §0.3）。
    /// owner 唯一事实来源：GameFlowSystem 发布与测试向量（GameTestVectors）共用，
    /// 保证"向量样例字节 = 线上字节"（§14.11.3）。
    /// </summary>
    public static class GameMessagePayload
    {
        /// <summary>game:StateChanged v1：[1]=phase(u32 varint) [2]=reason(u32 varint)（契约 §5）。
        /// 订阅者：ui（窗口开/关路由）、调试。</summary>
        public static PayloadBuffer StateChanged(byte phase, byte reason)
        {
            var w = new PayloadWriter();
            w.WriteUInt32(1, phase);
            w.WriteUInt32(2, reason);
            return w.ToBuffer();
        }
    }
}
