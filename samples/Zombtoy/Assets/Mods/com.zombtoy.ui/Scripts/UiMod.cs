using System;
using Game.Messaging;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Ui
{
    /// <summary>
    /// UI Mod（com.zombtoy.ui）：菜单/HUD/结算/设置/排行榜 5 窗口。**纯消费方**（契约头部）——
    /// 订阅 player/score/enemy/weapon/game/leaderboard 消息 + 调 game/leaderboard 能力，无 ECS 逻辑。
    /// 无周期系统（契约 §4：ui | 无 | 无）：窗口开/关由本 Mod 订阅 game:StateChanged / score:GameOver 自行驱动。
    ///
    /// 本 Mod 的能力边界（契约 §3）：**不发布任何跨 Mod 消息、不导出任何跨 Mod 能力**；
    /// 向 UI.Mod（com.game.ui）"导出"的是窗口声明（context.Ui.RegisterWindow，Rule 9 / §10.6）。
    /// 方向约定（summary §2）：ui → game 单向调用（start/to_menu/pause/resume），game 不调 ui。
    ///
    /// 跨 Mod 一律二进制（Rule 14）：订阅消息用 PayloadReader 字段解析（Protocols.cs 消费方解析器，
    /// Rule 19 各自重复定义）；调能力用 DataCodec。不引用任何对端程序集（Rule 12/19）。
    ///
    /// 视图桥（FPS 先例静态桥，Rule 12 不跨 Mod）：UiBindings 保存各订阅消息的最近快照，
    /// 视图（MenuView/HudView/ResultView/...）读它渲染；订阅 handler 解析消息后写入快照并触发 Changed。
    /// </summary>
    public sealed class UiMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.zombtoy.ui");

        // ---- 窗口（WindowId = com.zombtoy.ui:{name}，契约 §1；注册给 UI.Mod，Rule 9） ----
        public static readonly WindowId MenuWindow = new(ModIdValue, "menu");
        public static readonly WindowId HudWindow = new(ModIdValue, "hud");
        public static readonly WindowId ResultWindow = new(ModIdValue, "result");
        public static readonly WindowId SettingsWindow = new(ModIdValue, "settings");
        public static readonly WindowId LeaderboardWindow = new(ModIdValue, "leaderboard");

        // ---- 消费的对端消息（契约 §3 订阅表；不引用对端程序集，Rule 12/19：MessageId 各自重复定义） ----
        public static readonly MessageId PlayerHealthEvent = new(new ModId("com.zombtoy.player"), "HealthChanged");
        public static readonly MessageId PlayerStaminaEvent = new(new ModId("com.zombtoy.player"), "StaminaChanged");
        public static readonly MessageId EnemyCountEvent = new(new ModId("com.zombtoy.enemy"), "ZombieCountChanged");
        public static readonly MessageId ScoreChangedEvent = new(new ModId("com.zombtoy.score"), "ScoreChanged");
        public static readonly MessageId ScoreGameOverEvent = new(new ModId("com.zombtoy.score"), "GameOver");
        public static readonly MessageId WeaponAmmoEvent = new(new ModId("com.zombtoy.weapon"), "AmmoChanged");
        public static readonly MessageId WeaponSwitchedEvent = new(new ModId("com.zombtoy.weapon"), "WeaponSwitched");
        public static readonly MessageId GameStateEvent = new(new ModId("com.zombtoy.game"), "StateChanged");
        public static readonly MessageId LeaderboardTopEvent = new(new ModId("com.zombtoy.leaderboard"), "TopChanged");

        // ---- 消费的对端能力（契约 §3；不引用对端程序集，Rule 12/19：CapabilityId 各自重复定义） ----
        public static readonly ModId GameModId = new("com.zombtoy.game");
        public static readonly CapabilityId GameStartCap = new(GameModId, "start");
        public static readonly CapabilityId GameToMenuCap = new(GameModId, "to_menu");
        public static readonly CapabilityId GamePauseCap = new(GameModId, "pause");
        public static readonly CapabilityId GameResumeCap = new(GameModId, "resume");
        public static readonly CapabilityId GameStatusCap = new(GameModId, "status"); // QA/调试（契约 §4）

        public static readonly ModId PlayerModId = new("com.zombtoy.player");
        public static readonly CapabilityId PlayerGetStateCap = new(PlayerModId, "get_state");     // HUD 初始填充
        public static readonly ModId EnemyModId = new("com.zombtoy.enemy");
        public static readonly CapabilityId EnemyGetCountCap = new(EnemyModId, "get_count");       // HUD 初始填充
        public static readonly ModId WeaponModId = new("com.zombtoy.weapon");
        public static readonly CapabilityId WeaponGetStateCap = new(WeaponModId, "get_state");     // HUD 初始填充
        public static readonly ModId ScoreModId = new("com.zombtoy.score");
        public static readonly CapabilityId ScoreGetStateCap = new(ScoreModId, "get_state");       // Result 初始填充
        public static readonly ModId LeaderboardModId = new("com.zombtoy.leaderboard");
        public static readonly CapabilityId LeaderboardRefreshCap = new(LeaderboardModId, "refresh"); // Result 打开时刷新榜单
        public static readonly CapabilityId LeaderboardGetTopCap = new(LeaderboardModId, "get_top");   // 榜单缓存行集

        // ---- 静态桥（视图访问 / 测试，Mod 内部状态，FPS 先例；Rule 12 不跨 Mod） ----
        public static IModContext? Context { get; private set; }
        public static bool Registered { get; private set; }

        public void Register(IModContext context)
        {
            Context = context;
            Registered = true;
            UiBindings.Reset();

            if (context.HasClient)
            {
                // 1. 窗口声明（契约 §1 MVP：prefab 名 → 本 Mod 资源域；实例归本 Mod，§10.6/§10.8）
                context.Ui.RegisterWindow(new WindowDescriptor { Id = MenuWindow, Layer = UiLayer.Normal, Prefab = "ui_menu", Poolable = true });
                context.Ui.RegisterWindow(new WindowDescriptor { Id = HudWindow, Layer = UiLayer.Normal, Prefab = "ui_hud", Poolable = true });
                context.Ui.RegisterWindow(new WindowDescriptor { Id = ResultWindow, Layer = UiLayer.Popup, Prefab = "ui_result", Poolable = true });
                context.Ui.RegisterWindow(new WindowDescriptor { Id = SettingsWindow, Layer = UiLayer.Popup, Prefab = "ui_settings", Poolable = true });
                context.Ui.RegisterWindow(new WindowDescriptor { Id = LeaderboardWindow, Layer = UiLayer.Popup, Prefab = "ui_leaderboard", Poolable = true });

                // 2. 消息订阅（契约 §2 订阅表；归属本 ModObject，卸载强制退订 §13.5）
                context.Messages.Subscribe(PlayerHealthEvent, UiHandlers.OnHealthChanged);
                context.Messages.Subscribe(PlayerStaminaEvent, UiHandlers.OnStaminaChanged);
                context.Messages.Subscribe(EnemyCountEvent, UiHandlers.OnZombieCountChanged);
                context.Messages.Subscribe(ScoreChangedEvent, UiHandlers.OnScoreChanged);
                context.Messages.Subscribe(ScoreGameOverEvent, UiHandlers.OnScoreGameOver);
                context.Messages.Subscribe(WeaponAmmoEvent, UiHandlers.OnAmmoChanged);
                context.Messages.Subscribe(WeaponSwitchedEvent, UiHandlers.OnWeaponSwitched);
                context.Messages.Subscribe(GameStateEvent, UiHandlers.OnStateChanged);
                context.Messages.Subscribe(LeaderboardTopEvent, UiHandlers.OnTopChanged);
            }

            context.Log.Info($"UI Mod '{context.Info.Id}' v{context.Info.Version} 已注册（纯消费方：{9} 条消息订阅 + {5} 窗口声明）");
        }

        public void Unregister(IModContext context)
        {
            // 对称撤销（Rule 20）：窗口/订阅由 Core 按 ModObject 归属强制回收（§10.8 窗口 CloseAll / §13.5 退订），
            // 本 Mod 只清静态桥与本地快照（视图实例由 WindowManager 卸载时回收，§10.8）。
            UiBindings.Reset();
            Context = null;
            Registered = false;
            context.Log.Info($"UI Mod '{context.Info.Id}' 已注销");
        }
    }

    /// <summary>
    /// 视图数据桥（契约 §2 视图数据绑定）：各订阅消息解析后的最近快照，视图读此渲染。
    /// 归属本 Mod（Rule 12 不跨 Mod，同 FPS PlayerMod.World 静态桥先例）；Changed 供视图挂接重绘。
    /// </summary>
    public static class UiBindings
    {
        // HUD（契约 §2：player/score/enemy/weapon 消息）
        public static int Health;
        public static int MaxHealth;
        public static float Stamina;
        public static float MaxStamina = 1f;
        public static int Score;
        public static int HighScore;
        public static int Kills;
        public static int ZombieCount;
        public static uint AmmoSlot;
        public static int InMag;
        public static int Reserve;
        public static int MaxMag;
        public static uint WeaponSlot;

        // Result（契约 §2：score:GameOver）
        public static int FinalScore;
        public static int GameOverKills;
        public static bool IsNewHigh;
        public static bool HasGameOver; // 已结算标记（Result 打开时初始填充用）

        // 榜单（契约 §2：leaderboard:TopChanged rows 嵌套数组）
        public static object?[] Rows = Array.Empty<object?[]>();

        // 相位（契约 §2：game:StateChanged）
        public static byte Phase;
        public static byte Reason;

        /// <summary>视图挂接刷新事件（本 Mod 内部，Rule 12 不跨 Mod）。</summary>
        public static event Action? Changed;

        public static void Reset()
        {
            Health = 0; MaxHealth = 0;
            Stamina = 0f; MaxStamina = 1f;
            Score = 0; HighScore = 0; Kills = 0;
            ZombieCount = 0;
            AmmoSlot = 0; InMag = 0; Reserve = 0; MaxMag = 0;
            WeaponSlot = 0;
            FinalScore = 0; GameOverKills = 0; IsNewHigh = false; HasGameOver = false;
            Rows = Array.Empty<object?[]>();
            Phase = 0; Reason = 0;
            Changed = null;
        }

        internal static void Notify() => Changed?.Invoke();

        internal static void SetHealth(int current, int max) { Health = current; MaxHealth = max; Notify(); }
        internal static void SetStamina(float current, float max) { Stamina = current; MaxStamina = max; Notify(); }
        internal static void SetScore(int current, int high, int kills) { Score = current; HighScore = high; Kills = kills; Notify(); }
        internal static void SetZombieCount(int count) { ZombieCount = count; Notify(); }
        internal static void SetAmmo(uint slot, int inMag, int reserve, int maxMag)
        {
            AmmoSlot = slot; InMag = inMag; Reserve = reserve; MaxMag = maxMag; Notify();
        }
        internal static void SetWeapon(uint slot) { WeaponSlot = slot; Notify(); }
        internal static void SetGameOver(int finalScore, int kills, bool isNewHigh)
        {
            FinalScore = finalScore; GameOverKills = kills; IsNewHigh = isNewHigh; HasGameOver = true; Notify();
        }
        internal static void SetLeaderboard(object?[] rows) { Rows = rows; Notify(); }
        internal static void SetPhase(byte phase, byte reason) { Phase = phase; Reason = reason; Notify(); }
    }

    /// <summary>
    /// 订阅 handler（契约 §2 消费方解析器 → 快照 → 窗口路由）：
    /// 解析失败（载荷异常/owner 漂移）静默忽略（防御，不中断派发）。
    /// </summary>
    public static class UiHandlers
    {
        public static void OnHealthChanged(in MessageEnvelope env, PayloadReader reader)
        {
            if (!HudHealth.TryRead(in reader, out var cur, out var max)) return;
            UiBindings.SetHealth(cur, max);
        }

        public static void OnStaminaChanged(in MessageEnvelope env, PayloadReader reader)
        {
            if (!HudStamina.TryRead(in reader, out var cur, out var max)) return;
            UiBindings.SetStamina(cur, max);
        }

        public static void OnZombieCountChanged(in MessageEnvelope env, PayloadReader reader)
        {
            if (!HudZombies.TryRead(in reader, out var count)) return;
            UiBindings.SetZombieCount(count);
        }

        public static void OnScoreChanged(in MessageEnvelope env, PayloadReader reader)
        {
            if (!HudScore.TryRead(in reader, out var cur, out var high, out var kills)) return;
            UiBindings.SetScore(cur, high, kills);
        }

        /// <summary>score:GameOver：写结算快照 + 打开 Result 窗口 + 刷新榜单（summary §5.2 链路）。</summary>
        public static void OnScoreGameOver(in MessageEnvelope env, PayloadReader reader)
        {
            if (!ResultSummary.TryRead(in reader, out var score, out var kills, out var isNew)) return;
            UiBindings.SetGameOver(score, kills, isNew);

            var ctx = UiMod.Context;
            if (ctx is null) return; // 已卸载（防御竞态）
            ctx.Ui.Open(UiMod.ResultWindow); // 契约 §1：ui:result 打开时机 = score:GameOver
            UiCommands.RefreshLeaderboard(); // 契约 §3：Result 打开时刷新榜单（leaderboard:refresh）
        }

        public static void OnAmmoChanged(in MessageEnvelope env, PayloadReader reader)
        {
            if (!HudAmmo.TryRead(in reader, out var slot, out var inMag, out var reserve, out var maxMag)) return;
            UiBindings.SetAmmo(slot, inMag, reserve, maxMag);
        }

        public static void OnWeaponSwitched(in MessageEnvelope env, PayloadReader reader)
        {
            if (!HudWeapon.TryRead(in reader, out var slot)) return;
            UiBindings.SetWeapon(slot);
        }

        /// <summary>game:StateChanged：写相位快照 + 窗口开/关路由（契约 §2 表"按相位开/关窗口"）。</summary>
        public static void OnStateChanged(in MessageEnvelope env, PayloadReader reader)
        {
            if (!GameStateChanged.TryRead(in reader, out var phase, out var reason)) return;
            UiBindings.SetPhase((byte)phase, (byte)reason);
            WindowRouter.ApplyPhase((byte)phase);
        }

        /// <summary>leaderboard:TopChanged：写榜单快照（Result 榜单刷新，契约 §2）。</summary>
        public static void OnTopChanged(in MessageEnvelope env, PayloadReader reader)
        {
            if (!LeaderboardRows.TryRead(in reader, out var count, out var rows)) return;
            UiBindings.SetLeaderboard(rows);
        }
    }

    /// <summary>
    /// 窗口路由（契约 §1 打开时机表）：按 game:StateChanged 相位开/关窗口。
    /// ui:result 由 score:GameOver 直接打开（UiHandlers.OnScoreGameOver）；GameOver 相位只关 HUD。
    /// ui:pause 不在本任务 5 窗口范围（任务约束；暂停覆盖层由 HudView 视图层处理，Esc → game:pause）。
    /// </summary>
    public static class WindowRouter
    {
        public static void ApplyPhase(byte phase)
        {
            var ctx = UiMod.Context;
            if (ctx is null) return; // 已卸载（防御竞态）

            switch ((GamePhase)phase)
            {
                case GamePhase.Menu: // 初始相位：只留主菜单
                    ctx.Ui.Close(UiMod.HudWindow);
                    ctx.Ui.Close(UiMod.ResultWindow);
                    ctx.Ui.Close(UiMod.SettingsWindow);
                    ctx.Ui.Close(UiMod.LeaderboardWindow);
                    ctx.Ui.Open(UiMod.MenuWindow);
                    break;
                case GamePhase.Playing: // 开局：关菜单/结算，开 HUD
                    ctx.Ui.Close(UiMod.MenuWindow);
                    ctx.Ui.Close(UiMod.ResultWindow);
                    ctx.Ui.Close(UiMod.SettingsWindow);
                    ctx.Ui.Close(UiMod.LeaderboardWindow);
                    ctx.Ui.Open(UiMod.HudWindow);
                    break;
                case GamePhase.Paused: // 保持 HUD 打开（暂停覆盖层由视图层处理，时间缩放解耦，game 契约 §3 注）
                    break;
                case GamePhase.GameOver: // 关 HUD（Result 已由 score:GameOver 打开）
                    ctx.Ui.Close(UiMod.HudWindow);
                    break;
                case GamePhase.Result: // v2 预留（game 契约 §5 备注）
                    ctx.Ui.Close(UiMod.HudWindow);
                    break;
                default: // 未知相位：防御，不动窗口
                    break;
            }
        }
    }

    /// <summary>
    /// 能力调用命令（契约 §3/§2）：ui → game（start/to_menu/pause/resume/status）+ leaderboard（refresh/get_top）
    /// + HUD/Result 初始填充（player:get_state / enemy:get_count / weapon:get_state / score:get_state）。
    /// 二进制 ModCall（Rule 14：DataCodec 编解码纯数据）。未就绪/异常 → 静默记日志并返回 false（防御）。
    /// </summary>
    public static class UiCommands
    {
        /// <summary>菜单"开始游戏"→ game:start（契约 §3）。</summary>
        public static bool StartGame() => CallBool(UiMod.GameStartCap);

        /// <summary>Result/暂停"回主菜单"→ game:to_menu（契约 §3）。</summary>
        public static bool ToMenu() => CallBool(UiMod.GameToMenuCap);

        /// <summary>Esc 暂停 → game:pause（契约 §3）。</summary>
        public static bool Pause() => CallBool(UiMod.GamePauseCap);

        /// <summary>暂停"继续"→ game:resume（契约 §3）。</summary>
        public static bool Resume() => CallBool(UiMod.GameResumeCap);

        /// <summary>QA/调试：game:status → [phase u32]（game 能力行解析，契约 §4）。</summary>
        public static bool TryReadStatus(out uint phase)
        {
            phase = 0;
            var ctx = UiMod.Context;
            if (ctx is null) return false;
            try
            {
                var buf = ctx.Mods.Call(UiMod.GameModId, UiMod.GameStatusCap, DataCodec.Write(Array.Empty<object?>()));
                var row = DataCodec.Read(new PayloadReader(buf));
                return UiCapabilityShapes.TryReadStatusRow(row, out phase);
            }
            catch (Exception e)
            {
                ctx.Log.Warn($"[ui] game:status 调用异常: {e.Message}");
                return false;
            }
        }

        /// <summary>HUD 初始填充（契约 §2：窗口打开时拉一次）：player:get_state / enemy:get_count / weapon:get_state。</summary>
        public static void RefreshHud()
        {
            var ctx = UiMod.Context;
            if (ctx is null) return;
            // player:get_state（health/max/stamina/maxStamina 一并填充，契约 player §3）
            var row = CallRow(ctx, UiMod.PlayerGetStateCap);
            if (UiCapabilityShapes.TryReadPlayerState(row, out var hp, out var max, out var st, out var maxSt, out _))
            {
                UiBindings.SetHealth(hp, max);
                UiBindings.SetStamina(st, maxSt);
            }
            // enemy:get_count
            var countRow = CallRow(ctx, UiMod.EnemyGetCountCap);
            if (UiCapabilityShapes.TryReadEnemyCount(countRow, out var count))
                UiBindings.SetZombieCount(count);
            // weapon:get_state
            var wRow = CallRow(ctx, UiMod.WeaponGetStateCap);
            if (UiCapabilityShapes.TryReadWeaponState(wRow, out var slot, out _, out var inMag, out var reserve, out var maxMag))
            {
                UiBindings.SetAmmo(slot, inMag, reserve, maxMag);
                UiBindings.SetWeapon(slot);
            }
        }

        /// <summary>Result 初始填充（契约 §2）：score:get_state + 刷新榜单（leaderboard:refresh，异步）。</summary>
        public static void RefreshResult()
        {
            var ctx = UiMod.Context;
            if (ctx is null) return;
            var row = CallRow(ctx, UiMod.ScoreGetStateCap);
            if (UiCapabilityShapes.TryReadScoreState(row, out var score, out var high, out var kills, out var isNew))
            {
                UiBindings.SetScore(score, high, kills);
                // 已结算时 GameOver 快照已由 score:GameOver 写入；未结算（异常打开）用 get_state 兜底
                if (!UiBindings.HasGameOver)
                    UiBindings.SetGameOver(score, kills, isNew);
            }
            RefreshLeaderboard();
        }

        /// <summary>刷新榜单：leaderboard:refresh（入队异步拉取 → 发布 TopChanged → 本 Mod 订阅刷新快照，契约 §3）。</summary>
        public static void RefreshLeaderboard() => CallBool(UiMod.LeaderboardRefreshCap);

        /// <summary>取榜单缓存：leaderboard:get_top(n)（Result 榜单初始填充；返回行集或空，契约 leaderboard §2）。</summary>
        public static object?[] GetTop(int n)
        {
            var ctx = UiMod.Context;
            if (ctx is null) return Array.Empty<object?[]>();
            try
            {
                var buf = ctx.Mods.Call(UiMod.LeaderboardModId, UiMod.LeaderboardGetTopCap,
                    DataCodec.Write(new object?[] { n }));
                return DataCodec.Read(new PayloadReader(buf));
            }
            catch (Exception e)
            {
                ctx.Log.Warn($"[ui] leaderboard:get_top 调用异常: {e.Message}");
                return Array.Empty<object?[]>();
            }
        }

        // ---- 内部 ----

        private static bool CallBool(CapabilityId cap)
        {
            var ctx = UiMod.Context;
            if (ctx is null) return false;
            try
            {
                var buf = ctx.Mods.Call(cap.Mod, cap, DataCodec.Write(Array.Empty<object?>()));
                var row = DataCodec.Read(new PayloadReader(buf));
                return row.Length > 0 && row[0] is bool b && b;
            }
            catch (Exception e)
            {
                ctx.Log.Warn($"[ui] {cap.Mod.Value}:{cap.Path} 调用异常: {e.Message}");
                return false;
            }
        }

        private static object?[] CallRow(IModContext ctx, CapabilityId cap)
        {
            try
            {
                var buf = ctx.Mods.Call(cap.Mod, cap, DataCodec.Write(Array.Empty<object?>()));
                return DataCodec.Read(new PayloadReader(buf));
            }
            catch (Exception e)
            {
                ctx.Log.Warn($"[ui] {cap.Mod.Value}:{cap.Path} 调用异常: {e.Message}");
                return Array.Empty<object?>();
            }
        }
    }
}
