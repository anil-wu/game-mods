using Game.ECS;
using Game.Mod.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8）：
    /// - 持有 public uint EntityId（武器射线命中 collider → GetComponentInParent&lt;EnemyView&gt;().EntityId，summary §0.6）
    /// - 视觉自建（任务：视图自建视觉，不依赖 prefab）：Awake 里自建胶囊（PrimitiveType.Capsule + Standard 材质，
    ///   按 EnemyKind 配色/体型）+ NavMeshAgent；实体首次可见时按类别应用颜色与大小（多色可区分）。
    /// - NavMesh 追击：每帧读逻辑权威 EnemyMovement（Speed×SpeedMul 缓速）驱动 NavMeshAgent，
    ///   并把 NavMesh 实际位置回写 EnemyPosition（契约 §2：视图 NavMesh 同步回写）；无 NavMesh 时
    ///   （宿主场景未烘焙）回退直接镜像逻辑 EnemyPosition（EnemyChaseSystem 已按玩家追击积分）。
    /// - 追击目标：经 enemy Mod 静态桥 EnemyMod.TryGetPlayerPosition（二进制 ModCall，Rule 14）——
    ///   不 FindGameObjectWithTag / 全局发现（Rule 12 禁例）。
    /// - 死亡：读 EnemyFlags.IsSinking → 下沉动画（sinkSpeed，原版 EnemyHealth 表现）；实体销毁 → 视图自毁
    /// </summary>
    public sealed class EnemyView : MonoBehaviour, IEntityView
    {
        /// <summary>视图绑定的敌人实体（宿主 EnemySpawnerView 实例化时写入，契约 §8）。</summary>
        public uint EntityId;

        /// <summary>框架视图映射接口（Rule 12：WeaponView 等跨 Mod 视图经此读取实体 id，零类型引用）。</summary>
        uint IEntityView.EntityId => EntityId;

        /// <summary>下沉速度（对齐原版 EnemyHealth.sinkSpeed=2.5）。</summary>
        public float SinkSpeed = EnemyConfig.SinkSpeed;

        /// <summary>敌人类别配色（按 EnemyKind 编号，契约 §1；多色便于识别/验收像素分析）。</summary>
        private static readonly Color[] KindColors =
        {
            new(0.45f, 0.75f, 0.35f), // 0 Zombunny 绿
            new(0.58f, 0.4f, 0.22f),  // 1 ZomBear 棕
            new(0.6f, 0.6f, 0.62f),   // 2 Hellephant 灰
            new(0.28f, 0.52f, 0.22f), // 3 GiantZombunny 深绿
            new(0.45f, 0.3f, 0.16f),  // 4 GiantZomBear 深棕
            new(0.42f, 0.44f, 0.5f),  // 5 GiantHellephant 深灰
            new(0.4f, 0.16f, 0.5f),   // 6 Titan 紫
            new(0.8f, 0.2f, 0.2f),    // 7 Clown 红
            new(0.95f, 0.6f, 0.15f),  // 8 MiniClown 橙
            new(0.9f, 0.85f, 0.2f),   // 9 ZomDuck 黄
        };

        /// <summary>敌人类别体型（胶囊高度倍率：普通≈1 / 巨型 1.7+ / Titan 2.4；脚底着地）。</summary>
        private static readonly float[] KindScale =
        {
            0.9f, 1.0f, 1.1f, 1.7f, 1.8f, 1.9f, 2.4f, 1.0f, 0.6f, 0.8f,
        };

        private NavMeshAgent? _nav;
        private bool _sinking;
        private bool _visualized;

        private void Awake()
        {
            BuildVisuals(); // 宿主只建空 GameObject → 视图自建胶囊 + NavMeshAgent（不依赖 prefab）
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

            if (!_visualized) ApplyKindVisual(world, e); // 首次实体可见：按 EnemyKind 配色/体型

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
                var player = EnemyMod.TryGetPlayerPosition(EnemyMod.Context); // 追击目标（Rule 14 二进制 ModCall）
                if (player is not null)
                    _nav.SetDestination(new Vector3(player.Value.X, player.Value.Y, player.Value.Z));
                // 视图权威（NavMesh 修正）回写逻辑位置：契约 §2 EnemyPosition 由视图同步回写
                var pos = transform.position;
                world.Add(e, new EnemyPosition { X = pos.x, Y = pos.y, Z = pos.z });
            }
            else if (world.TryGet<EnemyPosition>(e, out var pos))
            {
                // 无 NavMesh（宿主未烘焙，回退）：直接按逻辑位置表现（EnemyChaseSystem 已积分追击）
                transform.position = new Vector3(pos.X, pos.Y, pos.Z);
            }
        }

        /// <summary>自建视觉与移动组件（幂等：prefab 已有渲染体/Agent 时跳过——资源迁移路径兼容）。</summary>
        private void BuildVisuals()
        {
            // Shootable 层：武器射线命中层（工程未配置该层时保持默认层，WeaponView 回退任意层）
            var shootable = LayerMask.NameToLayer("Shootable");
            if (shootable >= 0) gameObject.layer = shootable;

            // 胶囊体（collider 供武器射线命中 → GetComponentsInParent 找本视图）
            if (GetComponentInChildren<Renderer>() == null)
            {
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(transform, false);
                if (shootable >= 0) body.layer = shootable;
            }

            // NavMeshAgent（自建；宿主场景无烘焙 NavMesh 时 isOnNavMesh=false → Update 走逻辑位置回退）
            // 注意：GetComponent 对内置组件可能返回 fake-null，必须用 Unity 重载的 == null 判断（is null 不识别）。
            _nav = GetComponent<NavMeshAgent>();
            if (_nav == null) _nav = gameObject.AddComponent<NavMeshAgent>();
            if (_nav == null) return; // 防御（不可达）
            _nav.speed = 3f;
            _nav.acceleration = 8f;
            _nav.angularSpeed = 720f;
            _nav.radius = 0.5f;
            _nav.height = 2f;
            _nav.stoppingDistance = 1f;
        }

        /// <summary>按 EnemyKind 应用配色与体型（逻辑位置 Y=0 = 脚底；胶囊底部着地）。</summary>
        private void ApplyKindVisual(World world, Entity e)
        {
            _visualized = true;
            if (!world.TryGet<EnemyType>(e, out var type)) return;
            var body = transform.Find("Body");
            if (body is null) return;

            var idx = Mathf.Clamp(type.Kind, 0, KindColors.Length - 1);
            var renderer = body.GetComponent<Renderer>();
            if (renderer is not null)
                renderer.material = StandardMaterial(KindColors[idx]);

            var s = KindScale[idx];
            body.localScale = new Vector3(s, s, s);
            body.localPosition = new Vector3(0f, s, 0f); // 半高 = 胶囊底着地（Y=0）
        }

        private static Material StandardMaterial(Color color)
        {
            var shader = Shader.Find("Standard");
            var mat = shader != null ? new Material(shader) : new Material(Shader.Find("Diffuse"));
            mat.color = color;
            return mat;
        }
    }

    /// <summary>
    /// 宿主生成器视图（契约 §8）：订阅 EnemyMod.OnEnemySpawned，按 EnemyKind 实例化并绑定 EntityId。
    /// prefab 未挂载（资源 GUID 断链未修）→ 空 GameObject，由 EnemyView.Awake 自建胶囊/NavMeshAgent。
    /// </summary>
    public sealed class EnemySpawnerView : MonoBehaviour
    {
        /// <summary>按 EnemyKind 编号索引的敌人 prefab（[0]=Zombunny … [9]=ZomDuck）；资源迁移后按 prefab 校准。</summary>
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
                : new GameObject($"Enemy_{entityId}"); // prefab 未挂载：空 GO，EnemyView.Awake 自建视觉
            go.name = $"Enemy_{entityId}";
            var view = go.GetComponent<EnemyView>() ?? go.AddComponent<EnemyView>();
            view.EntityId = entityId;
        }
    }
}
