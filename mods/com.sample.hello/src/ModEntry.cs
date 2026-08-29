using Game.ECS;
using Game.Mod.Runtime;

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

    // ---- 入口（IMod v2：仅 Register / Unregister，Rule 1） ----
    public sealed class HelloMod : IMod
    {
        public void Register(IModContext context)
        {
            context.Ecs.RegisterComponent(typeof(Position));
            context.Ecs.RegisterComponent(typeof(Velocity));
            context.Ecs.RegisterSystem(new MoveSystem(), SystemSide.Shared);

            context.Log.Info(
                $"Mod '{context.Info.Id}' v{context.Info.Version} 已注册" +
                $"（HasClient={context.HasClient}, HasServer={context.HasServer}）");
        }

        public void Unregister(IModContext context)
        {
            context.Log.Info($"Mod '{context.Info.Id}' 已注销");
        }
    }
}
