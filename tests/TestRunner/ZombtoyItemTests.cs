using System;
using Com.Game.Core;
using Com.Zombtoy.Enemy;
using Com.Zombtoy.Item;
using Com.Zombtoy.Player;
using Com.Zombtoy.Weapon;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// com.zombtoy.item 无头测试（单机 Host 路径，契约 §2：SystemSide.Server + UpdateAll）：
    /// 定时生成（首延迟 10s / 间隔随机 [5,15] / MaxItems 上限 / 轮转出生点 / 玩家死亡不生成）、
    /// set_spawning 相位开关、reset 清场、拾取链路（血瓶→player:heal 回血、弹药箱→weapon:add_ammo 补弹，
    /// 消耗 + item:PickedUp 发布）、重复拾取/未知实体/玩家死亡拒绝、契约测试向量自校验（§14.11.3）、
    /// 卸载清理（Rule 20）。
    /// 道具由测试直接建（等价生成器产出路径：ItemData/ItemState/ItemPosition 全组件）；
    /// 依赖链（契约 §6）：core → player → enemy → weapon → item。
    /// </summary>
    public static class ZombtoyItemTests
    {
        private static readonly ModId ItemId = new("com.zombtoy.item");
        private static readonly ModId PlayerId = new("com.zombtoy.player");
        private static readonly ModId WeaponId = new("com.zombtoy.weapon");
        private static readonly ModId EnemyId = new("com.zombtoy.enemy");
        private static readonly ModId TestSubscriber = new("com.test.runner");

        private sealed class Fixture
        {
            public ModRuntimeHost Host = null!;
            public World World => Host.World;
            public IModContext ItemCtx = null!;
            public IModContext PlayerCtx = null!;
            public IModContext WeaponCtx = null!;
            public IModContext EnemyCtx = null!;

            // 消费方视角订阅（PayloadReader 字段解析，Rule 14；契约 §5）
            public int PickedUpEvents;
            public uint LastPickedKind;
            public int LastPickedAmount;

            public static Fixture Create()
            {
                var f = new Fixture { Host = new ModRuntimeHost(RuntimeRole.Host) };

                // 依赖（契约 §6）：core → player → enemy → weapon → item
                f.Host.Load(ModManifest.Parse(RepoPaths.ModJson("com.game.core")), typeof(CoreMod).Assembly);
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.player")),
                    typeof(PlayerMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.player"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.enemy")),
                    typeof(EnemyMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.enemy"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.weapon")),
                    typeof(WeaponMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.weapon"));
                f.Host.Load(ModManifest.Parse(RepoPaths.SampleModJson("Zombtoy", "com.zombtoy.item")),
                    typeof(ItemMod).Assembly, RepoPaths.SampleModDir("Zombtoy", "com.zombtoy.item"));
                f.ItemCtx = f.Host.Manager.Get(ItemId)!.Context;
                f.PlayerCtx = f.Host.Manager.Get(PlayerId)!.Context;
                f.WeaponCtx = f.Host.Manager.Get(WeaponId)!.Context;
                f.EnemyCtx = f.Host.Manager.Get(EnemyId)!.Context;

                // 关闭敌人生成器（item 测试聚焦道具链路；enemy 生成/追击/攻击由 enemy 测试覆盖，
                // 否则随机敌人在 10s 首延迟窗口内可能击杀玩家，导致"玩家存活才生成"条件失效，测试不稳定）
                ModCall.InvokeBool(f.EnemyCtx.Mods, EnemyId, EnemyMod.SetSpawningCap, false);

                f.Host.Messages.Subscribe(TestSubscriber, ItemMod.PickedUpEvent,
                    (in Game.Messaging.MessageEnvelope _, PayloadReader reader) =>
                    {
                        f.PickedUpEvents++;
                        if (reader.TryReadUInt32(1, out var kind)) f.LastPickedKind = kind;
                        if (reader.TryReadInt32(2, out var amount)) f.LastPickedAmount = amount;
                    });
                return f;
            }

            public void Tick(int frames, float dt = 0.05f)
            {
                for (var i = 0; i < frames; i++) Host.Tick(dt);
            }

            public object?[] Call(CapabilityId cap, params object?[] args)
                => ModCall.Invoke(ItemCtx.Mods, ItemId, cap, args);

            public bool CallBool(CapabilityId cap, params object?[] args)
                => ModCall.InvokeBool(ItemCtx.Mods, ItemId, cap, args);

            public bool CallPlayerBool(CapabilityId cap, params object?[] args)
                => ModCall.InvokeBool(PlayerCtx.Mods, PlayerId, cap, args);

            public Entity Spawner => new(ItemMod.SpawnerEntityId);

            public ItemSpawner SpawnerState() => World.Get<ItemSpawner>(Spawner);

            /// <summary>玩家当前生命（测试读组件）。</summary>
            public int PlayerHealth() => World.Get<PlayerHealth>(new Entity(PlayerMod.LocalPlayerEntityId)).Current;

            /// <summary>加速生成器：Timer=0，让生成尽快发生（测试不依赖 10s 首延迟）。</summary>
            public void SpeedUpSpawner()
            {
                var sp = SpawnerState();
                sp.Timer = 0f;
                World.Add(Spawner, sp);
            }

            /// <summary>轮转出生点推进后，取最近生成的道具实体 id（最后一个 ItemData 实体，测试读组件）。</summary>
            public uint LastItemId()
            {
                uint last = 0;
                foreach (var (id, _) in World.Store<ItemData>().All()) last = id;
                return last;
            }
        }

        // ---- 生成辅助（等价生成器产出路径：全组件；拾取测试直建，不依赖随机类型） ----

        private static uint SpawnItem(Fixture f, ItemKind kind, int amount, float x, float y, float z)
        {
            var e = f.World.CreateEntity();
            f.World.Add(e, new ItemData { Kind = (byte)kind, Amount = amount });
            f.World.Add(e, new ItemState { Picked = false });
            f.World.Add(e, new ItemPosition { X = x, Y = y, Z = z });
            return e.Id;
        }

        private static ItemPosition Pos(Fixture f, uint id) => f.World.Get<ItemPosition>(new Entity(id));

        // ---- 注册与默认状态 ----

        [Test]
        public static void Register_DefaultState_SpawnerCreated()
        {
            var f = Fixture.Create();
            Assert.True(ItemMod.SpawnerEntityId != 0, "道具生成器实体未创建");
            Assert.Equal(1, f.Host.Systems.CountOf(ItemId)); // ItemSpawnSystem（契约 §3 序 1）

            var sp = f.SpawnerState();
            Assert.True(sp.Enabled);
            Assert.Equal(ItemConfig.FirstDelay, sp.Timer); // 首延迟 10s（契约 §9）
            Assert.Equal(0, sp.ActiveCount);
            Assert.Equal(ItemConfig.MaxItems, sp.MaxItems); // 上限 3（契约 §9）
            Assert.Equal(0, sp.NextSpawnIndex);
            // 实体：player(1) + enemy 生成器(1) + weapon 域(1) + 槽位(4) + item 生成器(1) = 8
            Assert.Equal(8, f.World.EntityCount);
        }

        // ---- 定时生成（契约 §3 序 1） ----

        [Test]
        public static void Spawn_FirstDelay_NoItemBeforeTenSeconds()
        {
            var f = Fixture.Create();
            f.Tick(199, 0.05f); // 9.95s：Timer=0.05 > 0，未到首延迟
            Assert.Equal(0, f.World.Store<ItemData>().Count);

            f.Tick(1, 0.05f); // 10.0s：Timer 归零 → 首刷
            Assert.Equal(1, f.World.Store<ItemData>().Count);
            Assert.Equal(1, f.SpawnerState().ActiveCount);
            // 新实体带全组件（契约 §2）
            var itemId = f.LastItemId();
            var data = f.World.Get<ItemData>(new Entity(itemId));
            Assert.True(data.Kind is (byte)ItemKind.HealthPotion or (byte)ItemKind.AmmoBox);
            Assert.True(!f.World.Get<ItemState>(new Entity(itemId)).Picked);
        }

        [Test]
        public static void Spawn_Interval_Random5To15()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();
            f.Tick(1); // 首刷（Timer=0）
            var timer = f.SpawnerState().Timer; // 下次间隔随机 [5,15]（契约 §9）
            Assert.True(timer >= ItemConfig.MinInterval && timer <= ItemConfig.MaxInterval,
                $"间隔不在 [5,15]: {timer}");

            // 间隔内不再生成（ActiveCount=1 < 3，仅受 Timer 门控）
            f.Tick((int)(ItemConfig.MinInterval / 0.05f) - 1, 0.05f); // 略小于 5s
            Assert.Equal(1, f.World.Store<ItemData>().Count);
        }

        [Test]
        public static void Spawn_RotatesSpawnPoints()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();
            f.Tick(1); // 生成 #1 → SpawnPoints[0]
            var first = f.LastItemId();
            var p0 = ItemConfig.SpawnPoints[0];
            var pos0 = Pos(f, first);
            Assert.Equal(p0.X, pos0.X);
            Assert.Equal(p0.Y, pos0.Y);
            Assert.Equal(p0.Z, pos0.Z);

            f.SpeedUpSpawner();
            f.Tick(1); // 生成 #2 → SpawnPoints[1]
            var second = f.LastItemId();
            Assert.True(second != first, "轮转后应生成新实体");
            var p1 = ItemConfig.SpawnPoints[1];
            var pos1 = Pos(f, second);
            Assert.Equal(p1.X, pos1.X);
            Assert.Equal(p1.Y, pos1.Y);
            Assert.Equal(p1.Z, pos1.Z);
            Assert.Equal(2, f.SpawnerState().NextSpawnIndex); // 游标轮转（契约 §3）
        }

        [Test]
        public static void Spawn_MaxItems_CapEnforced()
        {
            var f = Fixture.Create();
            for (var i = 0; i < 20; i++)
            {
                f.SpeedUpSpawner();
                f.Tick(1);
            }
            // 上限 3（契约 §3 序 1：ActiveCount < MaxItems 才生成；契约 §9 MaxItems=3）
            Assert.Equal(ItemConfig.MaxItems, f.World.Store<ItemData>().Count);
            Assert.Equal(ItemConfig.MaxItems, f.SpawnerState().ActiveCount);
        }

        [Test]
        public static void Spawn_PlayerDead_NoSpawn()
        {
            var f = Fixture.Create();
            f.CallPlayerBool(PlayerMod.DamageCap, 999, 0u); // 击杀玩家
            f.SpeedUpSpawner();
            f.Tick(20); // 1s：玩家死亡 → 不生成（契约 §3 序 1：玩家存活才生成）
            Assert.Equal(0, f.World.Store<ItemData>().Count);
        }

        // ---- set_spawning / reset（game 相位控制，契约 §4） ----

        [Test]
        public static void SetSpawning_TogglesPauseResume()
        {
            var f = Fixture.Create();
            var ok = f.CallBool(ItemMod.SetSpawningCap, false);
            Assert.True(ok);
            Assert.True(!f.SpawnerState().Enabled);

            f.SpeedUpSpawner();
            f.Tick(20);
            Assert.Equal(0, f.World.Store<ItemData>().Count); // 关闭不生成（契约 §4）

            ok = f.CallBool(ItemMod.SetSpawningCap, true);
            Assert.True(ok);
            Assert.True(f.SpawnerState().Enabled);
            Assert.Equal(ItemConfig.FirstDelay, f.SpawnerState().Timer); // 恢复：满首延迟后首刷
            f.Tick(200, 0.05f); // 10s
            Assert.Equal(1, f.World.Store<ItemData>().Count);
        }

        [Test]
        public static void Reset_ClearsItems_ResetsSpawner()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();
            f.Tick(1);
            f.SpeedUpSpawner();
            f.Tick(1); // 生成器产出 2 个
            SpawnItem(f, ItemKind.HealthPotion, 30, 1f, 0f, 1f); // 手动建 1 个（reset 应清全部）
            Assert.Equal(3, f.World.Store<ItemData>().Count);

            var ok = f.CallBool(ItemMod.ResetCap);
            Assert.True(ok);
            Assert.Equal(0, f.World.Store<ItemData>().Count); // 全部销毁（契约 §4）
            var sp = f.SpawnerState();
            Assert.Equal(0, sp.ActiveCount);
            Assert.Equal(0, sp.NextSpawnIndex);
            Assert.Equal(ItemConfig.FirstDelay, sp.Timer); // Timer=首延迟
        }

        // ---- 拾取链路：血瓶 → player:heal（契约 §4/§7） ----

        [Test]
        public static void Pickup_HealthPotion_HealsPlayer_PublishesPickedUp()
        {
            var f = Fixture.Create();
            f.CallPlayerBool(PlayerMod.DamageCap, 40, 0u); // 玩家掉血 40 → 60
            Assert.Equal(60, f.PlayerHealth());

            var itemId = SpawnItem(f, ItemKind.HealthPotion, ItemConfig.HealthPotionAmount, 5f, 0f, 5f);
            var result = f.Call(ItemMod.PickupCap, itemId);
            Assert.True((bool)result[0]!, "血瓶拾取应 applied");
            Assert.Equal(0u, (uint)result[1]!); // kind=HealthPotion（契约 §4 返回 [applied, kind]）

            Assert.Equal(90, f.PlayerHealth()); // 60+30（契约 §9：heal 30）
            Assert.True(!f.World.Exists(new Entity(itemId)), "拾取后实体应销毁");
            Assert.Equal(1, f.PickedUpEvents);
            Assert.Equal(0u, f.LastPickedKind); // item:PickedUp 字段1=kind（契约 §5）
            Assert.Equal(ItemConfig.HealthPotionAmount, f.LastPickedAmount); // 字段2=amount
        }

        [Test]
        public static void Pickup_HealthPotion_FullHealth_StillConsumed()
        {
            var f = Fixture.Create();
            var itemId = SpawnItem(f, ItemKind.HealthPotion, ItemConfig.HealthPotionAmount, 0f, 0f, 0f);
            var result = f.Call(ItemMod.PickupCap, itemId);

            // 满血照常消耗（对齐原版血瓶行为；player:heal 满血仍返回 true 不扣减，契约 player §3）
            Assert.True((bool)result[0]!);
            Assert.Equal(0u, (uint)result[1]!);
            Assert.Equal(100, f.PlayerHealth()); // 治疗量溢出丢弃
            Assert.True(!f.World.Exists(new Entity(itemId)), "满血拾取仍应消耗实体");
            Assert.Equal(1, f.PickedUpEvents);
        }

        // ---- 拾取链路：弹药箱 → weapon:add_ammo（契约 §4/§7） ----

        [Test]
        public static void Pickup_AmmoBox_AddsAmmo_PublishesPickedUp()
        {
            var f = Fixture.Create();
            var itemId = SpawnItem(f, ItemKind.AmmoBox, ItemConfig.AmmoBoxAmount, 0f, 0f, 0f);
            var result = f.Call(ItemMod.PickupCap, itemId);
            Assert.True((bool)result[0]!, "弹药箱拾取应 applied");
            Assert.Equal(1u, (uint)result[1]!); // kind=AmmoBox

            // 每槽 +1 满弹匣（契约 §9：AmmoBox.Amount=1 → weapon:add_ammo(1)，契约 weapon §4）
            Assert.Equal(75, f.World.Get<WeaponAmmo>(new Entity(WeaponMod.SlotEntityIds[0])).Reserve); // 50+25
            Assert.Equal(32, f.World.Get<WeaponAmmo>(new Entity(WeaponMod.SlotEntityIds[1])).Reserve); // 24+8
            Assert.True(!f.World.Exists(new Entity(itemId)), "拾取后实体应销毁");
            Assert.Equal(1, f.PickedUpEvents);
            Assert.Equal(1u, f.LastPickedKind);
            Assert.Equal(ItemConfig.AmmoBoxAmount, f.LastPickedAmount);
        }

        // ---- 拾取拒绝（契约 §4/§7） ----

        [Test]
        public static void Pickup_AlreadyPicked_Rejected()
        {
            var f = Fixture.Create();
            var itemId = SpawnItem(f, ItemKind.HealthPotion, ItemConfig.HealthPotionAmount, 0f, 0f, 0f);
            // 直接置 Picked（模拟已拾取状态；防重复触发）
            var st = f.World.Get<ItemState>(new Entity(itemId));
            st.Picked = true;
            f.World.Add(new Entity(itemId), st);

            var result = f.Call(ItemMod.PickupCap, itemId);
            Assert.True(!(bool)result[0]!, "已拾取应拒绝");
            Assert.Equal(0u, (uint)result[1]!);
            Assert.True(f.World.Exists(new Entity(itemId)), "拒绝时不销毁实体");
            Assert.Equal(0, f.PickedUpEvents);
        }

        [Test]
        public static void Pickup_UnknownEntity_Rejected()
        {
            var f = Fixture.Create();
            var result = f.Call(ItemMod.PickupCap, 99999u); // 不存在的实体
            Assert.True(!(bool)result[0]!);
            Assert.Equal(0u, (uint)result[1]!);
            Assert.Equal(0, f.PickedUpEvents);
        }

        [Test]
        public static void Pickup_PlayerDead_Rejected()
        {
            var f = Fixture.Create();
            f.CallPlayerBool(PlayerMod.DamageCap, 999, 0u); // 击杀玩家
            var itemId = SpawnItem(f, ItemKind.HealthPotion, ItemConfig.HealthPotionAmount, 0f, 0f, 0f);
            var result = f.Call(ItemMod.PickupCap, itemId);
            // 玩家死亡不拾取（契约 §7：拾取前存活判定）
            Assert.True(!(bool)result[0]!);
            Assert.Equal(0u, (uint)result[1]!);
            Assert.True(f.World.Exists(new Entity(itemId)), "拒绝时不销毁实体");
        }

        // ---- 契约测试向量（§14.11.3）：owner 规范字节 ↔ 样例一致 ----

        [Test]
        public static void TestVectors_DataCodecShapes_RoundTrip()
        {
            foreach (var v in ItemTestVectors.All())
            {
                if (v.ContractId == ItemTestVectors.PickedUpMsgContract)
                    continue; // 消息向量不是 DataCodec 形状，走下一测试

                var fields = v.DecodeFields(); // owner 规范字节 → 字段元组
                Assert.Equal(v.Sample.Length, fields.Length, $"{v.ContractId} 字段数漂移");
                for (var i = 0; i < v.Sample.Length; i++)
                    Assert.Equal(v.Sample[i], fields[i], $"{v.ContractId}[{i}] 与样例不一致");
            }
        }

        [Test]
        public static void TestVectors_Messages_ConsumerStyleParsers()
        {
            // 消费方（ui.HudPickupHint，可选拾取提示）各自重复定义的解析器（Rule 19）
            // 解析 owner 规范字节，与样例一致 → "可重复定义未漂移"（§14.11.3）
            foreach (var v in ItemTestVectors.All())
            {
                if (v.ContractId != ItemTestVectors.PickedUpMsgContract) continue;
                Assert.True(HudPickupHint.TryRead(v.Bytes, out var kind, out var amount), $"解析失败: {v}");
                Assert.Equal((uint)v.Sample[0]!, kind);
                Assert.Equal((int)v.Sample[1]!, amount);
            }
        }

        // ---- 卸载清理（Rule 20 / §9.2 / §13.5） ----

        [Test]
        public static void Unload_CleansSystemsEntitiesCapabilities()
        {
            var f = Fixture.Create();
            f.SpeedUpSpawner();
            f.Tick(1);
            Assert.True(f.World.Store<ItemData>().Count > 0, "卸载前应有道具");
            var spawnerId = ItemMod.SpawnerEntityId;
            Assert.True(spawnerId != 0);
            Assert.Equal(1, f.Host.Systems.CountOf(ItemId));

            f.Host.Manager.Unload(ItemId);

            Assert.True(!f.Host.Manager.IsLoaded(ItemId));
            Assert.Equal(0, f.Host.Systems.CountOf(ItemId));            // 系统移除（§9.2）
            Assert.Equal(0, f.World.Store<ItemData>().Count);           // 道具实体随卸载强制销毁（§9.2）
            Assert.True(!f.World.Exists(new Entity(spawnerId)));        // 生成器实体销毁
            Assert.Equal(7, f.World.EntityCount);                       // 仅剩 player + enemy 生成器 + weapon 域 + 4 槽
            Assert.Equal(0, f.Host.Messages.SubscriptionCount(ItemId)); // 本 Mod 无订阅残留（只发布）
            Assert.Equal(0u, ItemMod.SpawnerEntityId);                  // 静态桥清空
        }

        // ---- 消费方风格解析器（Rule 19：结构相同、定义独立，模拟 ui 各自重复定义） ----

        /// <summary>消费方 ui.HudPickupHint：item:PickedUp 解析器（[1]=kind [2]=amount，契约 §5）。</summary>
        public sealed class HudPickupHint
        {
            public static bool TryRead(byte[] bytes, out uint kind, out int amount)
            {
                kind = 0; amount = 0;
                var reader = new PayloadReader(bytes);
                if (!reader.TryReadUInt32(1, out kind)) return false;
                if (!reader.TryReadInt32(2, out amount)) return false;
                return true;
            }
        }
    }
}
