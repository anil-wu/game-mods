using System;
using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Score
{
    /// <summary>
    /// 能力 ABI 形状（Rule 19 进程内务实形态）：跨 Mod 参数/返回只用 BCL 纯数据——基元 + object?[] 元组
    /// （字段布局即契约，CONTRACT.md §3/§7），双方各自按文档解析；跨边界经 DataCodec 编解码（Rule 14）。
    /// 本类内的构建/解析方法供 owner 自校验、能力实现与测试参考；消费方（game.GameFlow 调 reset、
    /// ui.ResultView 调 get_state / 解析 ScoreChanged/GameOver）各自重复定义（Rule 19）。
    /// </summary>
    public static class CapabilityShapes
    {
        // ---- 本 Mod 导出能力入参构建（owner 编码，测试向量/内部调用） ----

        /// <summary>score:reset 入参：[]（无参，契约 §3）。</summary>
        public static object?[] ResetArgs() => Array.Empty<object?>();

        /// <summary>score:get_state 返回行：[0]=score(i32) [1]=highScore(i32) [2]=kills(i32) [3]=isNewHigh(bool)（契约 §3）。</summary>
        public static object?[] GetStateRow(int score, int highScore, int kills, bool isNewHigh)
            => new object?[] { score, highScore, kills, isNewHigh };

        // ---- 本 Mod 能力返回行解析（owner 自校验 / 消费方 ui.ResultView 参考，Rule 19） ----

        /// <summary>score:get_state 返回行解析：[score, highScore, kills, isNewHigh]。</summary>
        public static bool TryReadGetStateRow(object? row, out int score, out int highScore, out int kills, out bool isNewHigh)
        {
            score = 0; highScore = 0; kills = 0; isNewHigh = false;
            if (row is not object[] a || a.Length < 4) return false;
            if (a[0] is not int s || a[1] is not int h || a[2] is not int k || a[3] is not bool b) return false;
            score = s; highScore = h; kills = k; isNewHigh = b;
            return true;
        }
    }

    /// <summary>
    /// 本 Mod 发布消息的载荷编码器（字段编号线格式，CONTRACT.md §4；summary §0.3）。
    /// owner 唯一事实来源：能力实现发布与测试向量（ScoreTestVectors）共用，
    /// 保证"向量样例字节 = 线上字节"（§14.11.3）。
    /// </summary>
    public static class ScoreMessagePayload
    {
        /// <summary>score:ScoreChanged v1：[1]=current(i32 varint) [2]=highScore(i32 varint) [3]=kills(i32 varint)。
        /// 订阅者：ui（HUD 分数/最高分，契约 §4）。</summary>
        public static PayloadBuffer ScoreChanged(int current, int highScore, int kills)
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, current);
            w.WriteInt32(2, highScore);
            w.WriteInt32(3, kills);
            return w.ToBuffer();
        }

        /// <summary>score:GameOver v1：[1]=finalScore(i32 varint) [2]=kills(i32 varint) [3]=isNewHigh(bool varint)。
        /// 订阅者：game（停 enemy/item 生成、发 game:StateChanged(GameOver)）、ui（打开 Result 窗口，契约 §4）。</summary>
        public static PayloadBuffer GameOver(int finalScore, int kills, bool isNewHigh)
        {
            var w = new PayloadWriter();
            w.WriteInt32(1, finalScore);
            w.WriteInt32(2, kills);
            w.WriteBool(3, isNewHigh);
            return w.ToBuffer();
        }
    }
}
