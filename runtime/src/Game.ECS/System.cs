using System.Collections.Generic;

namespace Game.ECS
{
    /// <summary>系统端侧：Server 权威逻辑，Client 表现逻辑，Shared 双端确定性逻辑。</summary>
    public enum SystemSide
    {
        Server,
        Client,
        Shared,
    }

    /// <summary>系统的统一入口：逻辑只在这里发生。</summary>
    public interface ISystem
    {
        void Update(SystemContext context);
    }

    /// <summary>系统执行上下文，提供世界访问与查询。</summary>
    public sealed class SystemContext
    {
        public World World { get; }
        public float DeltaTime { get; }

        public SystemContext(World world, float deltaTime)
        {
            World = world;
            DeltaTime = deltaTime;
        }

        public IEnumerable<(Entity, A)> Query<A>() where A : struct, IComponent
        {
            foreach (var (e, a) in World.Store<A>().All())
                yield return (new Entity(e), a);
        }

        public IEnumerable<(Entity, A, B)> Query<A, B>() where A : struct, IComponent where B : struct, IComponent
        {
            foreach (var (e, a) in World.Store<A>().All())
            {
                if (World.Store<B>().TryGet(e, out var b))
                    yield return (new Entity(e), a, b);
            }
        }

        public IEnumerable<(Entity, A, B, C)> Query<A, B, C>() where A : struct, IComponent where B : struct, IComponent where C : struct, IComponent
        {
            foreach (var (e, a) in World.Store<A>().All())
            {
                if (World.Store<B>().TryGet(e, out var b) && World.Store<C>().TryGet(e, out var c))
                    yield return (new Entity(e), a, b, c);
            }
        }
    }

    /// <summary>
    /// 系统组：按注册顺序执行。Server 端执行 Server+Shared，Client 端执行 Client+Shared。
    /// </summary>
    public sealed class SystemGroup
    {
        private readonly List<(ISystem System, SystemSide Side)> _systems = new();

        public void Add(ISystem system, SystemSide side) => _systems.Add((system, side));

        public int Count => _systems.Count;

        public void Update(World world, float deltaTime, SystemSide side)
        {
            var ctx = new SystemContext(world, deltaTime);
            foreach (var (system, s) in _systems)
            {
                if (s == SystemSide.Shared || s == side)
                    system.Update(ctx);
            }
        }

        /// <summary>单进程（Host 模式）tick：所有系统各执行一次。</summary>
        public void UpdateAll(World world, float deltaTime)
        {
            var ctx = new SystemContext(world, deltaTime);
            foreach (var (system, _) in _systems)
                system.Update(ctx);
        }
    }

}
