using Com.Game.Network;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// ECS Replication 端到端（§11.10）：Host 回环下，
    /// Server Spawn → Client 副本创建 / Snapshot → 组件同步 / Despawn → 副本销毁 / 卸载清理。
    /// </summary>
    public static class ReplicationTests
    {
        private static readonly ModId Owner = new("com.test.repl");

        // ---- 测试组件与 Codec（生成代码的手写等价物） ----

        public struct Pos : IComponent { public float X, Y, Z; }
        public struct Hp : IComponent { public int Current; }

        private sealed class PosCodec : IComponentCodec
        {
            public System.Type ComponentType => typeof(Pos);
            public void Write(object boxed, INetworkWriter w)
            {
                var p = (Pos)boxed;
                w.WriteFloat(1, p.X);
                w.WriteFloat(2, p.Y);
                w.WriteFloat(3, p.Z);
            }
            public object Read(INetworkReader r)
            {
                r.TryReadFloat(1, out var x);
                r.TryReadFloat(2, out var y);
                r.TryReadFloat(3, out var z);
                return new Pos { X = x, Y = y, Z = z };
            }
        }

        private sealed class HpCodec : IComponentCodec
        {
            public System.Type ComponentType => typeof(Hp);
            public void Write(object boxed, INetworkWriter w) => w.WriteInt32(1, ((Hp)boxed).Current);
            public object Read(INetworkReader r)
            {
                r.TryReadInt32(1, out var c);
                return new Hp { Current = c };
            }
        }

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public NetworkRuntimeImpl Runtime = null!;
            public ReplicationRuntime Replication = null!;
            public World World => Host.World;

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.network")), typeof(NetworkMod).Assembly);
                f.Runtime = NetworkMod.Current!;
                f.Runtime.AttachTransport(new LoopbackTransport());
                f.Runtime.Start(new NetworkConfig { Role = NetworkRole.Host });
                f.Replication = f.Runtime.Replication!;
                return f;
            }

            public Entity SpawnServerEntity(ArchetypeId archetype, Pos pos, Hp hp)
            {
                var e = World.CreateEntity();
                World.Add(e, pos);
                World.Add(e, hp);
                Replication.SpawnReplicated(Owner, e, archetype);
                return e;
            }

            public void Tick(int frames, float dt = 0.05f)
            {
                for (var i = 0; i < frames; i++) Host.Tick(dt);
            }
        }

        private static readonly ArchetypeId TestArchetype = new(Owner, "test_pawn");

        private static void RegisterArchetype(Fixture f) =>
            f.Replication.RegisterArchetype(Owner, TestArchetype,
                new IComponentCodec[] { new PosCodec(), new HpCodec() });

        [Test]
        public static void Snapshot_CreatesClientReplica_WithComponents()
        {
            var f = Fixture.Create();
            RegisterArchetype(f);
            f.SpawnServerEntity(TestArchetype, new Pos { X = 1, Y = 2, Z = 3 }, new Hp { Current = 80 });

            f.Tick(10); // 15Hz 快照 → Host 回环 → Client 创建副本

            // Client 侧：netId=1 的副本实体存在且组件已应用
            Assert.True(f.Replication.TryGetEntity(1u, out var replica));
            Assert.True(f.World.TryGet<Pos>(replica, out var pos));
            Assert.Equal(1f, pos.X);
            Assert.Equal(3f, pos.Z);
            Assert.True(f.World.TryGet<Hp>(replica, out var hp));
            Assert.Equal(80, hp.Current);
        }

        [Test]
        public static void Snapshot_UpdatesChangedComponents()
        {
            var f = Fixture.Create();
            RegisterArchetype(f);
            var server = f.SpawnServerEntity(TestArchetype, new Pos { X = 0 }, new Hp { Current = 100 });
            f.Tick(10);
            Assert.True(f.Replication.TryGetEntity(1u, out _));

            // Server 权威修改 → 下一帧快照同步到 Client
            f.World.Add(server, new Pos { X = 9, Y = 1, Z = -2 });
            f.World.Add(server, new Hp { Current = 55 });
            f.Tick(10);

            Assert.True(f.Replication.TryGetEntity(1u, out var replica));
            Assert.Equal(9f, f.World.Get<Pos>(replica).X);
            Assert.Equal(-2f, f.World.Get<Pos>(replica).Z);
            Assert.Equal(55, f.World.Get<Hp>(replica).Current);
        }

        [Test]
        public static void Despawn_DestroysClientReplica()
        {
            var f = Fixture.Create();
            RegisterArchetype(f);
            var server = f.SpawnServerEntity(TestArchetype, new Pos(), new Hp());
            f.Tick(10);
            Assert.True(f.Replication.TryGetEntity(1u, out _));

            f.Replication.DespawnReplicated(Owner, server);
            f.World.Destroy(server);
            f.Tick(2);

            Assert.True(!f.Replication.TryGetEntity(1u, out _));
        }

        [Test]
        public static void ServerEntityDestroyed_Untracks_And_Despawns()
        {
            // 服务端实体被外部销毁（如死亡）→ UntrackIfMissing 自动广播 Despawn
            var f = Fixture.Create();
            RegisterArchetype(f);
            var server = f.SpawnServerEntity(TestArchetype, new Pos(), new Hp());
            f.Tick(10);
            Assert.True(f.Replication.TryGetEntity(1u, out _));

            f.World.Destroy(server); // 不经 DespawnReplicated，直接销毁
            f.Tick(10);

            Assert.True(!f.Replication.TryGetEntity(1u, out _));
        }

        [Test]
        public static void UnregisterAll_CleansArchetypesAndReplicas()
        {
            var f = Fixture.Create();
            RegisterArchetype(f);
            f.SpawnServerEntity(TestArchetype, new Pos { X = 5 }, new Hp());
            f.Tick(10);
            Assert.True(f.Replication.TryGetEntity(1u, out var replica));

            f.Replication.UnregisterAll(Owner);

            Assert.True(!f.World.Exists(replica)); // 客户端副本实体销毁
            Assert.Throws<NetworkException>(() =>
                f.Replication.SpawnReplicated(Owner, f.World.CreateEntity(), TestArchetype)); // Archetype 已清
        }

        [Test]
        public static void Spawn_WrongOwner_Rejected()
        {
            var f = Fixture.Create();
            RegisterArchetype(f);
            var other = new ModId("com.test.other");
            Assert.Throws<NetworkException>(() =>
                f.Replication.SpawnReplicated(other, f.World.CreateEntity(), TestArchetype));
        }

        [Test]
        public static void Snapshot_OnlyAfterInterval()
        {
            var f = Fixture.Create();
            RegisterArchetype(f);
            var server = f.SpawnServerEntity(TestArchetype, new Pos { X = 1 }, new Hp());
            // 15Hz → 间隔 ~0.0667s；单帧 0.05s 不足一个间隔不广播
            f.Host.Tick(0.05f);
            f.World.Add(server, new Pos { X = 2 });
            f.Host.Tick(0.05f); // 累计 0.1s → 触发一次
            Assert.True(f.Replication.TryGetEntity(1u, out var replica));
            Assert.Equal(2f, f.World.Get<Pos>(replica).X);
        }
    }
}
