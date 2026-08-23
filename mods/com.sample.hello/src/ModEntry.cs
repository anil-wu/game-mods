using Game.ECS;
using Game.ModLoader;

namespace Com.Sample.Hello
{
    // ---- 组件：纯数据 ----
    public struct Position : IComponent { public float X; public float Y; }
    public struct Velocity : IComponent { public float X; public float Y; }

    // ---- 系统：逻辑 ----
    public sealed class MoveSystem : ISystem
    {
        public void Update(SystemContext ctx)
        {
            // 查询同时拥有 Velocity + Position 的实体
            foreach (var (e, vel, pos) in ctx.Query<Velocity, Position>())
            {
                var p = pos; // foreach 解构变量只读，复制到可变局部变量
                p.X += vel.X * ctx.DeltaTime;
                p.Y += vel.Y * ctx.DeltaTime;
                ctx.World.Add(e, p); // 写回修改后的组件（struct 值语义）
            }
        }
    }

    // ---- 入口 ----
    public sealed class ModEntry : IMod
    {
        public void OnLoad(IModContext context)
        {
            context.World.RegisterComponent<Position>();
            context.World.RegisterComponent<Velocity>();
            context.Systems.Add(new MoveSystem(), SystemSide.Shared);

            // 示例：创建一个带移动组件的实体
            var entity = context.World.CreateEntity();
            context.World.Add(entity, new Position { X = 0, Y = 0 });
            context.World.Add(entity, new Velocity { X = 1, Y = 0 });

            context.Log.Info($"Mod '{context.ModId}' v{context.Version} 已加载");
        }
    }
}
