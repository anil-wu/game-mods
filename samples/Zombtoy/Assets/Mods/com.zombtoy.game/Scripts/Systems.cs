using System;
using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Game
{
    /// <summary>
    /// 相位状态机编排器（契约 §3：无周期 System——能力实现 = 编排器，可无头测试；不注册 ECS System）。
    /// 职责：
    /// - 开局编排（game:start）：依次调组件 Mod reset + set_spawning(true) → 写 Phase=Playing → 发 game:StateChanged；
    /// - 相位迁移（game:pause / resume / to_menu）；
    /// - 订阅 score:GameOver 收尾（OnScoreGameOver：停 enemy/item 生成 + 写 GameOver 相位 + 发 StateChanged）。
    /// 方向约定（契约头部，避免依赖环）：只调用组件 Mod 能力、只发布 game:StateChanged、只订阅 score:GameOver；
    /// 不订阅组件 Mod 的其他消息（score:GameOver 除外）。
    /// 跨 Mod 一律二进制（Rule 14）：调能力用 DataCodec；消费 score:GameOver 用 PayloadReader 字段解析，
    /// 不引用 score 程序集（Rule 12/19）。
    /// </summary>
    public static class GameFlowSystem
    {
        /// <summary>
        /// score:GameOver 消费方解析器（契约 score §4：[1]=finalScore(i32) [2]=kills(i32) [3]=isNewHigh(bool)）。
        /// game 只作收尾触发信号，不消费字段（契约 §3 OnScoreGameOver；Result 窗口由 ui 自行打开）。
        /// 三字段齐全才视为合法载荷——owner 改了布局（缺字段）应被检出（§14.11.3 漂移语义）。
        /// </summary>
        public static bool TryReadScoreGameOver(in PayloadReader reader)
        {
            return reader.TryReadInt32(1, out _)
                && reader.TryReadInt32(2, out _)
                && reader.TryReadBool(3, out _);
        }

        /// <summary>
        /// 订阅处理：score:GameOver 收尾（契约 §3 OnScoreGameOver）。
        /// enemy:set_spawning(false) → item:set_spawning(false) → 写 Phase=GameOver → 发 game:StateChanged(GameOver, PlayerDied)。
        /// （Result 相位由 ui 收到 GameOver 后自行打开 Result 窗口，本 Mod 不设 Result 相位转移，契约 §5 备注。）
        /// </summary>
        public static void OnScoreGameOver(in MessageEnvelope envelope, PayloadReader reader)
        {
            if (!TryReadScoreGameOver(in reader)) return; // 载荷异常 → 忽略（防御）
            var mod = GameMod.Context;
            var world = GameMod.World;
            if (mod is null || world is null || GameMod.GameEntityId == 0) return; // 已卸载（防御竞态）
            var e = new Entity(GameMod.GameEntityId);
            if (!world.TryGet<GameState>(e, out var state)) return;
            if (state.Phase == (byte)GamePhase.GameOver) return; // 防重复收尾（消息重放/重复订阅）

            SetSpawning(mod, GameMod.EnemySetSpawningCap, false);
            SetSpawning(mod, GameMod.ItemSetSpawningCap, false);
            WritePhase(world, e, (byte)GamePhase.GameOver, (byte)GameReason.PlayerDied);
            PublishStateChanged(mod);
        }

        /// <summary>
        /// game:start：开局编排（契约 §3 顺序）：
        /// score:reset → player:reset(出生点) → enemy:reset → enemy:set_spawning(true) →
        /// item:reset → item:set_spawning(true) → weapon:reset →
        /// 写 Phase=Playing → 发 game:StateChanged(Playing, Started) → [ok bool]。
        /// 任一编排步骤失败（组件 Mod 未就绪/返回拒绝/异常）→ [false] 且不改相位（防御）。
        /// </summary>
        public static object?[] StartGame()
        {
            var mod = GameMod.Context;
            var world = GameMod.World;
            if (mod is null || world is null || GameMod.GameEntityId == 0) return new object?[] { false };
            var e = new Entity(GameMod.GameEntityId);
            if (!world.TryGet<GameState>(e, out _)) return new object?[] { false };

            // 开局编排（&= 不短路：依次全部调用，任一失败即本次开局失败，契约 §3）
            var ok = true;
            ok &= Call(mod, GameMod.ScoreModId, GameMod.ScoreResetCap, CapabilityShapes.NoArgs());       // 1. score:reset
            ok &= Call(mod, GameMod.PlayerModId, GameMod.PlayerResetCap,
                new object?[] { GameConfig.SpawnX, GameConfig.SpawnY, GameConfig.SpawnZ });               // 2. player:reset(出生点)
            ok &= Call(mod, GameMod.EnemyModId, GameMod.EnemyResetCap, CapabilityShapes.NoArgs());        // 3. enemy:reset
            ok &= Call(mod, GameMod.EnemyModId, GameMod.EnemySetSpawningCap, new object?[] { true });     // 4. enemy:set_spawning(true)
            ok &= Call(mod, GameMod.ItemModId, GameMod.ItemResetCap, CapabilityShapes.NoArgs());          // 5. item:reset
            ok &= Call(mod, GameMod.ItemModId, GameMod.ItemSetSpawningCap, new object?[] { true });       // 6. item:set_spawning(true)
            ok &= Call(mod, GameMod.WeaponModId, GameMod.WeaponResetCap, CapabilityShapes.NoArgs());      // 7. weapon:reset
            if (!ok)
            {
                mod.Log.Warn("[game] game:start 编排失败（组件 Mod 未就绪），保持当前相位");
                return new object?[] { false };
            }

            WritePhase(world, e, (byte)GamePhase.Playing, (byte)GameReason.Started);
            PublishStateChanged(mod);
            return new object?[] { true };
        }

        /// <summary>
        /// game:to_menu：写 Phase=Menu → 发 game:StateChanged(Menu, ToMenu) → [ok bool]（契约 §3）。
        /// 任意相位可回主菜单（Result/Pause 的"再玩一次/退出"按钮回调侧）。
        /// </summary>
        public static object?[] ToMenu()
        {
            var mod = GameMod.Context;
            var world = GameMod.World;
            if (mod is null || world is null || GameMod.GameEntityId == 0) return new object?[] { false };
            var e = new Entity(GameMod.GameEntityId);
            if (!world.TryGet<GameState>(e, out _)) return new object?[] { false };

            WritePhase(world, e, (byte)GamePhase.Menu, (byte)GameReason.ToMenu);
            PublishStateChanged(mod);
            return new object?[] { true };
        }

        /// <summary>
        /// game:pause：enemy:set_spawning(false) → item:set_spawning(false) → 写 Phase=Paused →
        /// 发 game:StateChanged(Paused, Paused) → [ok bool]。
        /// （Time.timeScale=0 由 ui PauseView 视图层处理，逻辑相位与 Unity 时间缩放解耦，契约 §3 注。）
        /// 仅 Playing 相位可暂停（防御：Menu/GameOver 下暂停无意义）。
        /// </summary>
        public static object?[] Pause()
        {
            var mod = GameMod.Context;
            var world = GameMod.World;
            if (mod is null || world is null || GameMod.GameEntityId == 0) return new object?[] { false };
            var e = new Entity(GameMod.GameEntityId);
            if (!world.TryGet<GameState>(e, out var state)) return new object?[] { false };
            if (state.Phase != (byte)GamePhase.Playing) return new object?[] { false }; // 仅 Playing 可暂停

            SetSpawning(mod, GameMod.EnemySetSpawningCap, false);
            SetSpawning(mod, GameMod.ItemSetSpawningCap, false);
            WritePhase(world, e, (byte)GamePhase.Paused, (byte)GameReason.Paused);
            PublishStateChanged(mod);
            return new object?[] { true };
        }

        /// <summary>
        /// game:resume：enemy:set_spawning(true) → item:set_spawning(true) → 写 Phase=Playing →
        /// 发 game:StateChanged(Playing, Resumed) → [ok bool]（契约 §3）。仅 Paused 相位可恢复。
        /// </summary>
        public static object?[] Resume()
        {
            var mod = GameMod.Context;
            var world = GameMod.World;
            if (mod is null || world is null || GameMod.GameEntityId == 0) return new object?[] { false };
            var e = new Entity(GameMod.GameEntityId);
            if (!world.TryGet<GameState>(e, out var state)) return new object?[] { false };
            if (state.Phase != (byte)GamePhase.Paused) return new object?[] { false }; // 仅 Paused 可恢复

            SetSpawning(mod, GameMod.EnemySetSpawningCap, true);
            SetSpawning(mod, GameMod.ItemSetSpawningCap, true);
            WritePhase(world, e, (byte)GamePhase.Playing, (byte)GameReason.Resumed);
            PublishStateChanged(mod);
            return new object?[] { true };
        }

        /// <summary>game:status：[] → [phase u32]（QA/调试，契约 §4）。</summary>
        public static object?[] Status()
        {
            var world = GameMod.World;
            if (world is null || GameMod.GameEntityId == 0)
                return CapabilityShapes.StatusRow(GameConfig.InitialPhase); // 未就绪（防御）
            var e = new Entity(GameMod.GameEntityId);
            if (!world.TryGet<GameState>(e, out var state))
                return CapabilityShapes.StatusRow(GameConfig.InitialPhase);
            return CapabilityShapes.StatusRow(state.Phase);
        }

        // ---- 内部 ----

        /// <summary>调组件 Mod 能力（二进制 ModCall，Rule 14）；返回行的 [0]=bool 即 ok。</summary>
        private static bool Call(IModContext mod, ModId target, CapabilityId cap, object?[] args)
        {
            try
            {
                var buf = mod.Mods.Call(target, cap, DataCodec.Write(args));
                var row = DataCodec.Read(new PayloadReader(buf));
                return row.Length > 0 && row[0] is bool b && b;
            }
            catch (Exception e)
            {
                mod.Log.Warn($"[game] {target.Value}:{cap.Path} 调用异常: {e.Message}");
                return false;
            }
        }

        /// <summary>enemy/item:set_spawning 开关（相位控制，契约 §3）；失败静默记日志、不中断相位迁移。</summary>
        private static void SetSpawning(IModContext mod, CapabilityId cap, bool enabled)
        {
            try
            {
                var buf = mod.Mods.Call(cap.Mod, cap, DataCodec.Write(new object?[] { enabled }));
                var row = DataCodec.Read(new PayloadReader(buf));
                if (row.Length == 0 || row[0] is not bool b || !b)
                    mod.Log.Warn($"[game] {cap.Mod.Value}:{cap.Path}({enabled}) 被拒绝，静默忽略");
            }
            catch (Exception e)
            {
                mod.Log.Warn($"[game] {cap.Mod.Value}:{cap.Path}({enabled}) 调用异常: {e.Message}");
            }
        }

        private static void WritePhase(World world, Entity e, byte phase, byte reason)
            => world.Add(e, new GameState { Phase = phase, Reason = reason });

        /// <summary>发布 game:StateChanged（phase/reason，契约 §5；与 GameTestVectors 共用编码器）。</summary>
        private static void PublishStateChanged(IModContext mod)
        {
            var world = GameMod.World;
            if (world is null || GameMod.GameEntityId == 0) return;
            var e = new Entity(GameMod.GameEntityId);
            if (!world.TryGet<GameState>(e, out var state)) return;
            var buffer = GameMessagePayload.StateChanged(state.Phase, state.Reason);
            mod.Messages.Publish(GameMod.StateChangedEvent, 1, in buffer);
        }
    }
}
