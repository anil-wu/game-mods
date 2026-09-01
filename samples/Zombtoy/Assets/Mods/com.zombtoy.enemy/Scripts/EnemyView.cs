using Game.ECS;
using UnityEngine;
using UnityEngine.AI;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8）：
    /// - 持有 public uint EntityId（武器射线命中 collider → GetComponentInParent&lt;EnemyView&gt;().EntityId，summary §0.6）
    /// - NavMesh 追击：每帧读逻辑权威 EnemyPosition/EnemyMovement（Speed×SpeedMul 缓速）驱动 NavMeshAgent，
    ///   并把 NavMesh 实际位置回写 EnemyPosition（契约 §2：视图 NavMesh 同步回写）
    /// - 死亡：读 EnemyFlags.IsSinking → 下沉动画（sinkSpeed，原版 EnemyHealth 表现）；实体销毁 → 视图自毁
    /// - 受击/死亡粒子、头顶血条由 prefab 挂载（资源迁移时按 prefab 校准）
    /// </summary>
    public sealed class EnemyView : MonoBehaviour
    {
        /// <summary>视图绑定的敌人实体（宿主 EnemySpawnerView 实例化时写入，契约 §8）。</summary>
        public uint EntityId;

        /// <summary>下沉速度（对齐原版 EnemyHealth.sinkSpeed=2.5）。</summary>
        public float SinkSpeed = EnemyConfig.SinkSpeed;

        private NavMeshAgent? _nav;
        private Transform? _player;
        private bool _sinking;

        private void Awake()
        {
            _nav = GetComponent<NavMeshAgent>();
            var playerGo = GameObject.FindGameObjectWithTag("Player");
            if (playerGo != null) _player = playerGo.transform;
        }

        private void Update()
        {
            var world = EnemyMod.World;
            if (world is null || EntityId == 0) return;

            var e = new Entity(EntityId);
            if (!world.Exists(e))
            {
                // 实体已销毁（下沉到点 / reset 清场）→ 视图自毁
                Destroy(gameObject);
                return;
            }
            if (!world.TryGet<EnemyFlags>(e, out var flags)) return;

            if (flags.IsDead)
            {
                // 死亡：停 NavMesh → 下沉动画（EnemyHealthSystem 计时到点后销毁实体，本视图随之自毁）
                if (flags.IsSinking && !_sinking)
                {
                    _sinking = true;
                    if (_nav != null) _nav.enabled = false;
                }
                if (_sinking)
                    transform.Translate(Vector3.down * SinkSpeed * Time.deltaTime);
                return;
            }

            // 存活：NavMesh 追击玩家（逻辑权威速度；视图按 NavMesh 避障路径走）
            if (_nav != null && _nav.isOnNavMesh)
            {
                if (world.TryGet<EnemyMovement>(e, out var mv))
                    _nav.speed = Mathf.Max(0.01f, mv.Speed * mv.SpeedMul); // 缓速（冰）生效
                if (_player != null)
                    _nav.SetDestination(_player.position);
                // 视图权威（NavMesh 修正）回写逻辑位置：契约 §2 EnemyPosition 由视图同步回写
                var pos = transform.position;
                world.Add(e, new EnemyPosition { X = pos.x, Y = pos.y, Z = pos.z });
            }
            else if (world.TryGet<EnemyPosition>(e, out var pos))
            {
                // 无 NavMeshAgent（回退）：直接按逻辑位置表现
                transform.position = new Vector3(pos.X, pos.Y, pos.Z);
            }
        }
    }

    /// <summary>
    /// 宿主生成器视图（契约 §8 可选形态）：订阅 EnemyMod.OnEnemySpawned，按 EnemyKind 实例化 prefab
    /// 并绑定 EntityId（prefab 数组在 Inspector 按类别编号挂载，资源迁移时按 prefab 校准）。
    /// </summary>
    public sealed class EnemySpawnerView : MonoBehaviour
    {
        /// <summary>按 EnemyKind 编号索引的敌人 prefab（[0]=Zombunny … [9]=ZomDuck）。</summary>
        public GameObject[] PrefabsByKind = new GameObject[10];

        private void OnEnable() => EnemyMod.OnEnemySpawned += HandleSpawned;
        private void OnDisable() => EnemyMod.OnEnemySpawned -= HandleSpawned;

        private void HandleSpawned(uint entityId)
        {
            var world = EnemyMod.World;
            if (world is null) return;
            var e = new Entity(entityId);
            if (!world.TryGet<EnemyType>(e, out var type)) return;

            var prefab = type.Kind < PrefabsByKind.Length ? PrefabsByKind[type.Kind] : null;
            var go = prefab != null
                ? Instantiate(prefab)
                : GameObject.CreatePrimitive(PrimitiveType.Capsule); // prefab 未挂载回退（bundle 迁移前）
            go.name = $"Enemy_{entityId}";
            var view = go.GetComponent<EnemyView>() ?? go.AddComponent<EnemyView>();
            view.EntityId = entityId;
        }
    }
}
