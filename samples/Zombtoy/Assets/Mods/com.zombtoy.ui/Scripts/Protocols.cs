using System;
using Game.Mod.Contract.Wire;

namespace Com.Zombtoy.Ui
{
    /// <summary>
    /// 相位枚举（game CONTRACT.md §1；编号即契约 u32，各 Mod 各自重复定义，Rule 19）。
    /// ui 只消费 StateChanged 做窗口开/关路由（CONTRACT.md §2），不迁移相位。
    /// </summary>
    public enum GamePhase : byte
    {
        Menu = 0,
        Playing = 1,
        Paused = 2,
        GameOver = 3,
        Result = 4, // v2 预留（game 契约 §5 备注）
    }

    /// <summary>相位迁移原因（game CONTRACT.md §1；编号即契约，Rule 19）。</summary>
    public enum GameReason : byte
    {
        Manual = 0,
        Started = 1,
        Paused = 2,
        Resumed = 3,
        PlayerDied = 4,
        ToMenu = 5,
    }

    /// <summary>
    /// 消息消费方解析器（Rule 19：各自写自己的解析器，结构相同、定义独立，不引用 owner 程序集）。
    /// 全部严格解析（缺字段/类型不符返回 false）——owner 改了布局（append-only 之外）应被检出（§14.11.3 漂移语义）。
    /// 输入 = 各 owner CONTRACT.md 定义的字段编号线格式（summary §0.3）。
    /// 命名对齐 ui CONTRACT.md §5：HudHealth/HudStamina/HudScore/HudZombies/HudAmmo/ResultSummary/LeaderboardRows。
    /// </summary>
    public static class HudHealth
    {
        /// <summary>player:HealthChanged v1：[1]=current(i32) [2]=max(i32)（player CONTRACT §4）。</summary>
        public static bool TryRead(in PayloadReader reader, out int current, out int max)
        {
            current = 0; max = 0;
            if (!reader.TryReadInt32(1, out current)) return false;
            if (!reader.TryReadInt32(2, out max)) return false;
            return true;
        }
    }

    /// <summary>player:StaminaChanged v1：[1]=current(f32) [2]=max(f32)（player CONTRACT §4）。</summary>
    public static class HudStamina
    {
        public static bool TryRead(in PayloadReader reader, out float current, out float max)
        {
            current = 0f; max = 0f;
            if (!reader.TryReadFloat(1, out current)) return false;
            if (!reader.TryReadFloat(2, out max)) return false;
            return true;
        }
    }

    /// <summary>score:ScoreChanged v1：[1]=current(i32) [2]=highScore(i32) [3]=kills(i32)（score CONTRACT §4）。</summary>
    public static class HudScore
    {
        public static bool TryRead(in PayloadReader reader, out int current, out int highScore, out int kills)
        {
            current = 0; highScore = 0; kills = 0;
            if (!reader.TryReadInt32(1, out current)) return false;
            if (!reader.TryReadInt32(2, out highScore)) return false;
            if (!reader.TryReadInt32(3, out kills)) return false;
            return true;
        }
    }

    /// <summary>enemy:ZombieCountChanged v1：[1]=count(i32)（enemy CONTRACT §5）。</summary>
    public static class HudZombies
    {
        public static bool TryRead(in PayloadReader reader, out int count)
        {
            count = 0;
            return reader.TryReadInt32(1, out count);
        }
    }

    /// <summary>weapon:AmmoChanged v1：[1]=slot(u32) [2]=inMag(i32) [3]=reserve(i32) [4]=maxMag(i32)（weapon CONTRACT §5）。</summary>
    public static class HudAmmo
    {
        public static bool TryRead(in PayloadReader reader, out uint slot, out int inMag, out int reserve, out int maxMag)
        {
            slot = 0; inMag = 0; reserve = 0; maxMag = 0;
            if (!reader.TryReadUInt32(1, out slot)) return false;
            if (!reader.TryReadInt32(2, out inMag)) return false;
            if (!reader.TryReadInt32(3, out reserve)) return false;
            if (!reader.TryReadInt32(4, out maxMag)) return false;
            return true;
        }
    }

    /// <summary>weapon:WeaponSwitched v1：[1]=slot(u32)（weapon CONTRACT §5；HUD GunText 武器名）。</summary>
    public static class HudWeapon
    {
        public static bool TryRead(in PayloadReader reader, out uint slot)
        {
            slot = 0;
            return reader.TryReadUInt32(1, out slot);
        }
    }

    /// <summary>score:GameOver v1：[1]=finalScore(i32) [2]=kills(i32) [3]=isNewHigh(bool)（score CONTRACT §4；Result 结算）。</summary>
    public static class ResultSummary
    {
        public static bool TryRead(in PayloadReader reader, out int finalScore, out int kills, out bool isNewHigh)
        {
            finalScore = 0; kills = 0; isNewHigh = false;
            if (!reader.TryReadInt32(1, out finalScore)) return false;
            if (!reader.TryReadInt32(2, out kills)) return false;
            if (!reader.TryReadBool(3, out isNewHigh)) return false;
            return true;
        }
    }

    /// <summary>leaderboard:TopChanged v1：[1]=count(u32) [2]=rows(bytes 嵌套)（leaderboard CONTRACT §3；Result 榜单）。
    /// rows = DataCodec 编码的嵌套数组，每行 [rank u32, score i32, name string]。</summary>
    public static class LeaderboardRows
    {
        public static bool TryRead(in PayloadReader reader, out uint count, out object?[] rows)
        {
            count = 0; rows = Array.Empty<object?[]>();
            if (!reader.TryReadUInt32(1, out count)) return false;
            if (!reader.TryReadView(2, out var view)) return false;
            rows = DataCodec.Read(new PayloadReader(in view));
            return true;
        }

        /// <summary>单行解析：[0]=rank(u32) [1]=score(i32) [2]=name(string)（leaderboard CONTRACT §2 Row）。</summary>
        public static bool TryReadRow(object? row, out uint rank, out int score, out string name)
        {
            rank = 0; score = 0; name = "";
            if (row is not object?[] a || a.Length < 3) return false;
            if (a[0] is not uint r) return false;
            if (a[1] is not int s) return false;
            if (a[2] is not string n) return false;
            rank = r; score = s; name = n;
            return true;
        }
    }

    /// <summary>game:StateChanged v1：[1]=phase(u32) [2]=reason(u32)（game CONTRACT §5；窗口开/关路由）。</summary>
    public static class GameStateChanged
    {
        public static bool TryRead(in PayloadReader reader, out uint phase, out uint reason)
        {
            phase = 0; reason = 0;
            if (!reader.TryReadUInt32(1, out phase)) return false;
            if (!reader.TryReadUInt32(2, out reason)) return false;
            return true;
        }
    }

    /// <summary>
    /// 能力行解析（Rule 19：消费方各自重复定义；CONTRACT.md §3/§2）。
    /// 消费的 game 能力：start/to_menu/pause/resume（args=[] → 返回 [ok bool]）、status（返回 [phase u32]，QA/调试）。
    /// HUD/Result 初始填充行：player:get_state / enemy:get_count / weapon:get_state / score:get_state /
    /// leaderboard:get_top（契约 §2"窗口打开时拉一次"）。
    /// </summary>
    public static class UiCapabilityShapes
    {
        /// <summary>game:status 返回行：[0]=phase(u32)（game CONTRACT §4）。</summary>
        public static bool TryReadStatusRow(object? row, out uint phase)
        {
            phase = 0;
            if (row is not object?[] a || a.Length < 1 || a[0] is not uint p) return false;
            phase = p;
            return true;
        }

        /// <summary>game:start/to_menu/pause/resume 返回行：[0]=ok(bool)（game CONTRACT §4）。</summary>
        public static bool TryReadOk(object? row, out bool ok)
        {
            ok = false;
            if (row is not object?[] a || a.Length < 1 || a[0] is not bool b) return false;
            ok = b;
            return true;
        }

        /// <summary>player:get_state 返回行：[0]=health(i32) [1]=max(i32) [2]=stamina(f32) [3]=maxStamina(f32) [4]=alive(bool)（player CONTRACT §3）。</summary>
        public static bool TryReadPlayerState(object? row, out int health, out int max, out float stamina, out float maxStamina, out bool alive)
        {
            health = 0; max = 0; stamina = 0f; maxStamina = 0f; alive = false;
            if (row is not object?[] a || a.Length < 5) return false;
            if (a[0] is not int h || a[1] is not int m || a[2] is not float s ||
                a[3] is not float ms || a[4] is not bool al) return false;
            health = h; max = m; stamina = s; maxStamina = ms; alive = al;
            return true;
        }

        /// <summary>enemy:get_count 返回行：[0]=count(i32)（enemy CONTRACT §4）。</summary>
        public static bool TryReadEnemyCount(object? row, out int count)
        {
            count = 0;
            if (row is not object?[] a || a.Length < 1 || a[0] is not int c) return false;
            count = c;
            return true;
        }

        /// <summary>weapon:get_state 返回行：[0]=slot(u32) [1]=slotCount(u32) [2]=inMag(i32) [3]=reserve(i32) [4]=maxMag(i32)（weapon CONTRACT §4）。</summary>
        public static bool TryReadWeaponState(object? row, out uint slot, out uint slotCount, out int inMag, out int reserve, out int maxMag)
        {
            slot = 0; slotCount = 0; inMag = 0; reserve = 0; maxMag = 0;
            if (row is not object?[] a || a.Length < 5) return false;
            if (a[0] is not uint sl || a[1] is not uint sc || a[2] is not int im ||
                a[3] is not int rs || a[4] is not int mm) return false;
            slot = sl; slotCount = sc; inMag = im; reserve = rs; maxMag = mm;
            return true;
        }

        /// <summary>score:get_state 返回行：[0]=score(i32) [1]=highScore(i32) [2]=kills(i32) [3]=isNewHigh(bool)（score CONTRACT §3）。</summary>
        public static bool TryReadScoreState(object? row, out int score, out int highScore, out int kills, out bool isNewHigh)
        {
            score = 0; highScore = 0; kills = 0; isNewHigh = false;
            if (row is not object?[] a || a.Length < 4) return false;
            if (a[0] is not int sc || a[1] is not int hi || a[2] is not int k || a[3] is not bool nh) return false;
            score = sc; highScore = hi; kills = k; isNewHigh = nh;
            return true;
        }

        /// <summary>leaderboard:get_top 返回行集，单行：[0]=rank(u32) [1]=score(i32) [2]=name(string)（leaderboard CONTRACT §2）。</summary>
        public static bool TryReadGetTopRow(object? row, out uint rank, out int score, out string name)
            => LeaderboardRows.TryReadRow(row, out rank, out score, out name);
    }

    /// <summary>
    /// 本 Mod 的本地设置（契约 §6 MVP 最小集：灵敏度 → PlayerPrefs 本地持久化）。
    /// 纯数据（无 UnityEngine 依赖），SettingsView 负责 PlayerPrefs 存取；无头测试只校验默认值与读写。
    /// </summary>
    public static class UiSettings
    {
        /// <summary>PlayerPrefs 键（对齐原版 MouseSensitivity；PlayerPrefs 是 Unity API，视图层读写）。</summary>
        public const string SensitivityKey = "ZombtoyMouseSensitivity";

        /// <summary>鼠标灵敏度系数（默认 1.0；0.5..2.0 范围由 SettingsView 滑动条钳制）。</summary>
        public static float Sensitivity = 1f;
    }
}
