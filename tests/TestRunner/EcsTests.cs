using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;

namespace TestRunner
{
    /// <summary>ECS 测试：组件存储 / 查询 / 销毁 / 系统归属。</summary>
    public static class EcsTests
    {
        private struct Pos : IComponent { public float X; }
        private struct Vel : IComponent { public float X; }

        private sealed class MoveSystem : ISystem
        {
            public void Update(SystemContext ctx)
            {
                foreach (var (e, vel, pos) in ctx.Query<Vel, Pos>())
                {
                    var p = pos;
                    p.X += vel.X * ctx.DeltaTime;
                    ctx.World.Add(e, p);
                }
            }
        }

        [Test]
        public static void Add_Get_Has()
        {
            var w = new World();
            var e = w.CreateEntity();
            w.Add(e, new Pos { X = 5 });
            Assert.True(w.Has<Pos>(e));
            Assert.Equal(5f, w.Get<Pos>(e).X);
        }

        [Test]
        public static void Query_TwoComponents()
        {
            var w = new World();
            var bus = new MessageBus();
            var systems = new SystemGroup(bus);
            systems.Add(new MoveSystem(), SystemSide.Shared, new ModId("com.test.ecs"));

            var e = w.CreateEntity();
            w.Add(e, new Pos { X = 0 });
            w.Add(e, new Vel { X = 2 });
            systems.UpdateAll(w, 0.5f);
            Assert.Equal(1f, w.Get<Pos>(e).X);
        }

        [Test]
        public static void Destroy_RemovesComponents()
        {
            var w = new World();
            var e = w.CreateEntity();
            w.Add(e, new Pos { X = 1 });
            w.Destroy(e);
            Assert.True(!w.Has<Pos>(e));
            Assert.True(!w.Exists(e));
        }

        [Test]
        public static void NonGeneric_RegisterComponent()
        {
            // 跨 ABI 注册面为非泛型（Rule 13）
            var w = new World();
            w.RegisterComponent(typeof(Pos));
            Assert.True(w.TryGetStore(typeof(Pos)) is not null);
        }

        [Test]
        public static void SystemGroup_OwnerTracking_And_RemoveAll()
        {
            var bus = new MessageBus();
            var systems = new SystemGroup(bus);
            var ownerA = new ModId("com.test.a");
            var ownerB = new ModId("com.test.b");
            systems.Add(new MoveSystem(), SystemSide.Shared, ownerA);
            systems.Add(new MoveSystem(), SystemSide.Shared, ownerA);
            systems.Add(new MoveSystem(), SystemSide.Shared, ownerB);

            Assert.Equal(2, systems.CountOf(ownerA));
            systems.RemoveAll(ownerA); // 卸载时按 owner 移除（§9.2）
            Assert.Equal(0, systems.CountOf(ownerA));
            Assert.Equal(1, systems.CountOf(ownerB));
            Assert.Equal(1, systems.Count);
        }

        [Test]
        public static void EntityOwnership_DestroyAll_ByOwner()
        {
            // 实体归属 ModObject：卸载时按 owner 强制销毁（§4/§7 ModObject 资源边界）
            var w = new World();
            var ownerA = new ModId("com.test.a");
            var ownerB = new ModId("com.test.b");

            var a1 = w.CreateEntity(ownerA);
            var a2 = w.CreateEntity(ownerA);
            var b1 = w.CreateEntity(ownerB);
            var free = w.CreateEntity(); // 无 owner（框架内部实体）
            w.Add(a1, new Pos { X = 1 });

            Assert.Equal(4, w.EntityCount);
            Assert.Equal(2, w.DestroyAll(ownerA));
            Assert.True(!w.Exists(a1) && !w.Exists(a2));
            Assert.True(!w.Has<Pos>(a1)); // 组件随实体清除
            Assert.True(w.Exists(b1) && w.Exists(free));
            Assert.Equal(2, w.EntityCount);
            Assert.Equal(0, w.DestroyAll(ownerA)); // 幂等
        }

        [Test]
        public static void SystemGroup_SideFiltering()
        {
            var w = new World();
            var bus = new MessageBus();
            var systems = new SystemGroup(bus);
            var owner = new ModId("com.test.ecs");
            var e = w.CreateEntity();
            w.Add(e, new Pos { X = 0 });
            w.Add(e, new Vel { X = 1 });

            systems.Add(new MoveSystem(), SystemSide.Server, owner);
            systems.Update(w, 1f, SystemSide.Client); // Client 侧不执行 Server 系统
            Assert.Equal(0f, w.Get<Pos>(e).X);
            systems.Update(w, 1f, SystemSide.Server);
            Assert.Equal(1f, w.Get<Pos>(e).X);
        }
    }
}
