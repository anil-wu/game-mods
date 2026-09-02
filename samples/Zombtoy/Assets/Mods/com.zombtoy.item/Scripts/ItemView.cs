using Game.ECS;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;
using UnityEngine;

namespace Com.Zombtoy.Item
{
    /// <summary>
    /// 道具视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8）：
    /// - 持有 public uint EntityId（宿主 ItemSpawnerView 实例化时写入，summary §0.6）
    /// - 旋转动画（对齐原版道具旋转）
    /// - OnTriggerEnter（玩家 tag）→ 静态桥调本 Mod item:pickup（二进制 ModCall，Rule 14）——
    ///   权威判定在能力内同步完成（实体/存活/分派/消耗/发消息），视图只做表现
    /// - 拾取：经 OnItemPicked 立即禁 Collider + 播放音效（防同帧重复触发）；实体已销毁，下一帧自毁
    /// - 实体销毁（拾取/reset/卸载）→ Update 检查 Exists → 视图自毁（同 EnemyView 模式）
    /// </summary>
    public sealed class ItemView : MonoBehaviour, IEntityView
    {
        /// <summary>视图绑定的道具实体（宿主 ItemSpawnerView 实例化时写入，契约 §8）。</summary>
        public uint EntityId;

        /// <summary>框架视图映射接口（Rule 12：跨 Mod 视图经此读取实体 id，零类型引用）。</summary>
        uint IEntityView.EntityId => EntityId;

        /// <summary>旋转速度（对齐原版道具旋转动画，资源迁移时按 prefab 校准）。</summary>
        public float RotateSpeed = 60f;

        private Collider? _collider;
        private AudioSource? _audio;

        private void Awake()
        {
            _collider = GetComponent<Collider>();
            _audio = GetComponent<AudioSource>();
            if (_audio == null) _audio = gameObject.AddComponent<AudioSource>(); // 自建拾取音效
        }

        private void OnEnable() => ItemMod.OnItemPicked += HandlePicked;
        private void OnDisable() => ItemMod.OnItemPicked -= HandlePicked;

        private void HandlePicked(uint entityId)
        {
            if (entityId != EntityId) return;
            if (_collider is not null) _collider.enabled = false; // 拾取后禁用 Collider（契约 §8，防重复触发）
            if (_audio != null) _audio.Play();                // 拾取音效（契约 §8）
        }

        private void Update()
        {
            var world = ItemMod.World;
            if (world is null || EntityId == 0) return;

            var e = new Entity(EntityId);
            if (!world.Exists(e))
            {
                // 实体已销毁（拾取消耗 / reset 清场 / 卸载）→ 视图自毁
                Destroy(gameObject);
                return;
            }

            transform.Rotate(0f, RotateSpeed * Time.deltaTime, 0f); // 旋转动画（契约 §8）
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag("Player")) return; // 玩家触发（契约 §8：OnTriggerEnter(玩家 tag)）
            var ctx = ItemMod.Context;
            if (ctx is null || EntityId == 0) return;
            // 静态桥调本 Mod item:pickup（二进制 ModCall，Rule 14；权威在能力内同步完成，契约头部链路）
            ctx.Mods.Call(ItemMod.ModIdValue, ItemMod.PickupCap, DataCodec.Write(new object?[] { EntityId }));
        }
    }

    /// <summary>
    /// 宿主生成器视图（契约 §8）：订阅 ItemMod.OnItemSpawned，按 ItemKind 实例化 prefab
    /// 并绑定 EntityId（prefab 数组在 Inspector 按类别编号挂载，[0]=血瓶 [1]=弹药箱，资源迁移时按 prefab 校准）。
    /// </summary>
    public sealed class ItemSpawnerView : MonoBehaviour
    {
        /// <summary>按 ItemKind 编号索引的道具 prefab（[0]=血瓶 [1]=弹药箱）。</summary>
        public GameObject[] PrefabsByKind = new GameObject[2];

        private void OnEnable() => ItemMod.OnItemSpawned += HandleSpawned;
        private void OnDisable() => ItemMod.OnItemSpawned -= HandleSpawned;

        private void HandleSpawned(uint entityId)
        {
            var world = ItemMod.World;
            if (world is null) return;
            var e = new Entity(entityId);
            if (!world.TryGet<ItemData>(e, out var data)) return;

            var prefab = data.Kind < PrefabsByKind.Length ? PrefabsByKind[data.Kind] : null;
            var go = prefab != null
                ? Instantiate(prefab)
                : GameObject.CreatePrimitive(PrimitiveType.Cube); // prefab 未挂载回退（bundle 迁移前）
            go.name = $"Item_{entityId}";
            var view = go.GetComponent<ItemView>() ?? go.AddComponent<ItemView>();
            view.EntityId = entityId;
        }
    }
}
