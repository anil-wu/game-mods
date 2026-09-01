using System;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Zombtoy.Game
{
    /// <summary>
    /// 主 Mod / 引导 Mod（com.zombtoy.game）：装它一个 = 装整个游戏（依赖闭包，契约头部）。
    /// 职责：
    /// - 相位状态机（单实体 GameState：Menu/Playing/Paused/GameOver；Result 相位由 ui 打开 Result 窗口承载，契约 §5 备注）；
    /// - 对局编排（game:start/to_menu/pause/resume，能力实现 = 编排器，契约 §3）；
    /// - 订阅 score:GameOver 收尾（停生成 + 写 GameOver 相位）。
    /// 无周期系统：逻辑全在能力实现与订阅 handler 内（契约 §3，全部可无头测试）。
    /// 方向约定（契约头部，避免依赖环）：只调用组件 Mod 能力、只发布 game:StateChanged、只订阅 score:GameOver；
    /// 不订阅组件 Mod 的其他消息。UI 按钮回调由 ui Mod 调本 Mod 能力（ui → game 单向依赖）。
    /// 不引用任何组件 Mod 程序集：能力/消息 ID 与行形状各自重复定义（Rule 12/19）。
    /// 跨 Mod 一律二进制（Rule 14）：ModCall 用 DataCodec，消息用 PayloadWriter 字段编号格式。
    /// </summary>
    public sealed class GameMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.zombtoy.game");

        // 发布消息（谁定义谁发布，Rule 11；载荷字段布局见 CONTRACT.md §5）
        public static readonly MessageId StateChangedEvent = new(ModIdValue, "StateChanged");

        // 导出能力（契约 §4；owner = com.zombtoy.game）
        public static readonly CapabilityId StatusCap = new(ModIdValue, "status");
        public static readonly CapabilityId StartCap = new(ModIdValue, "start");
        public static readonly CapabilityId ToMenuCap = new(ModIdValue, "to_menu");
        public static readonly CapabilityId PauseCap = new(ModIdValue, "pause");
        public static readonly CapabilityId ResumeCap = new(ModIdValue, "resume");

        // 消费的对端消息（契约 §6；不引用 score 程序集，Rule 12/19：MessageId 各自重复定义）
        public static readonly MessageId ScoreGameOverEvent = new(new ModId("com.zombtoy.score"), "GameOver");

        // 消费的对端能力（契约 §7；不引用组件 Mod 程序集，Rule 12/19：CapabilityId 各自重复定义）
        public static readonly ModId PlayerModId = new("com.zombtoy.player");
        public static readonly CapabilityId PlayerResetCap = new(PlayerModId, "reset");
        public static readonly ModId EnemyModId = new("com.zombtoy.enemy");
        public static readonly CapabilityId EnemyResetCap = new(EnemyModId, "reset");
        public static readonly CapabilityId EnemySetSpawningCap = new(EnemyModId, "set_spawning");
        public static readonly ModId ItemModId = new("com.zombtoy.item");
        public static readonly CapabilityId ItemResetCap = new(ItemModId, "reset");
        public static readonly CapabilityId ItemSetSpawningCap = new(ItemModId, "set_spawning");
        public static readonly ModId WeaponModId = new("com.zombtoy.weapon");
        public static readonly CapabilityId WeaponResetCap = new(WeaponModId, "reset");
        public static readonly ModId ScoreModId = new("com.zombtoy.score");
        public static readonly CapabilityId ScoreResetCap = new(ScoreModId, "reset");

        // 静态桥（视图访问 / 测试，Mod 内部状态，FPS 先例；Rule 12 不跨 Mod）
        public static World World { get; private set; } = null!;
        public static IModContext Context { get; private set; } = null!;
        public static uint GameEntityId { get; private set; }

        public void Register(IModContext context)
        {
            Context = context;
            World = context.Ecs.World;
            GameEntityId = 0;

            // 1. 组件注册（归属本 ModObject，卸载强制回收 §9.2；契约 §2）
            context.Ecs.RegisterComponent(typeof(GameState));

            // 2. 能力导出（§12.11；二进制 ABI Rule 14：DataCodec 编解码纯数据，bytes 不藏引用）
            context.Mods.Export(StatusCap, _ => DataCodec.Write(GameFlowSystem.Status()));
            context.Mods.Export(StartCap, _ => DataCodec.Write(GameFlowSystem.StartGame()));
            context.Mods.Export(ToMenuCap, _ => DataCodec.Write(GameFlowSystem.ToMenu()));
            context.Mods.Export(PauseCap, _ => DataCodec.Write(GameFlowSystem.Pause()));
            context.Mods.Export(ResumeCap, _ => DataCodec.Write(GameFlowSystem.Resume()));

            if (context.HasServer)
            {
                // 3. 消息订阅（归属本 ModObject，卸载强制退订 §13.5；契约 §3 OnScoreGameOver）
                context.Messages.Subscribe(ScoreGameOverEvent, GameFlowSystem.OnScoreGameOver);

                // 4. 单实体状态（Register 创建常驻；相位迁移只改字段不销毁，契约 §2 初始 Menu）
                var e = context.Ecs.CreateEntity();
                context.Ecs.World.Add(e, new GameState
                {
                    Phase = GameConfig.InitialPhase,
                    Reason = GameConfig.InitialReason,
                });
                GameEntityId = e.Id;
            }

            context.Log.Info($"主 Mod '{context.Info.Id}' v{context.Info.Version} 已注册（相位状态机就绪，依赖组件 Mod 已就绪）");
        }

        public void Unregister(IModContext context)
        {
            // 对称撤销（Rule 20）：系统/实体/能力/订阅由 Core 按 ModObject 归属强制回收（§9.2/§13.5），
            // 本 Mod 只清静态桥与本地状态。
            World = null!;
            Context = null!;
            GameEntityId = 0;
            context.Log.Info($"主 Mod '{context.Info.Id}' 已注销");
        }
    }
}
