using Game.ECS;
using Game.Messaging;

namespace TestRunner
{
    public static class EcsTests
    {
        private struct Position : IComponent { public float X; public float Y; }
        private struct Velocity : IComponent { public float X; public float Y; }
        private struct Dead : IComponent { }

        [Test]
        public static void Add_Get_Has()
        {
            var world = new World();
            var e = world.CreateEntity();
            world.Add(e, new Position { X = 1, Y = 2 });
            Assert.True(world.Has<Position>(e), "Has");
            Assert.True(world.TryGet(e, out Position p) && p.X == 1, "TryGet 值");
            Assert.True(!world.Has<Dead>(e), "无 Dead 组件");
        }

        [Test]
        public static void Query_TwoComponents()
        {
            var world = new World();
            var e = world.CreateEntity();
            world.Add(e, new Position { X = 1, Y = 2 });
            world.Add(e, new Velocity { X = 3, Y = 4 });

            var ctx = new SystemContext(world, new MessageBus(), 0.016f);
            int count = 0;
            foreach (var (ent, vel, pos) in ctx.Query<Velocity, Position>())
            {
                count++;
                Assert.True(ent == e, "命中实体");
                Assert.True(vel.X == 3 && pos.Y == 2, "组件值");
            }
            Assert.Equal(1, count, "命中 1 个实体");
        }

        [Test]
        public static void Destroy_RemovesComponents()
        {
            var world = new World();
            var e = world.CreateEntity();
            world.Add(e, new Position { X = 1 });
            world.Destroy(e);
            Assert.True(!world.Has<Position>(e), "销毁后组件移除");
            Assert.Equal(0, world.EntityCount, "实体计数归零");
        }

        [Test]
        public static void WriteBack_DuringQuery()
        {
            // 回归：系统在查询期间写回同一组件类型（如 WinCheckSystem 写回 Session），
            // 不应抛“集合被修改”异常（Mono 下 Dictionary 更新现有键会失效枚举器）。
            var world = new World();
            var e = world.CreateEntity();
            world.Add(e, new Position { X = 1 });

            var ctx = new SystemContext(world, new MessageBus(), 0.016f);
            foreach (var (ent, pos) in ctx.Query<Position>())
            {
                var p = pos; // foreach 解构变量只读，复制到可变局部变量
                p.X += 1;
                world.Add(ent, p); // 查询期间写回
            }
            Assert.True(world.Get<Position>(e).X == 2, "查询期间写回应生效");
        }
    }
}
