using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8）：
    /// - 持有 public uint EntityId（武器射线命中 collider → GetComponentInParent&lt;EnemyView&gt;().EntityId，summary §0.6）
    /// - 视觉：优先加载**原游戏资源**（Rule 3：经本 Mod context.Resources.Load，不 Resources.Load/AssetDatabase）——
    ///   Models/Characters/ 下 Zombunny/ZomBear/Hellephant FBX + ImportThis/Clown、ZombieDuck FBX，
    ///   按 EnemyKind 选模型（巨型/Titan 用同一模型放大），材质用模型自带（不再手动染色）；
    ///   加载失败（bundle 缺失/路径不符）回退程序化胶囊 + EnemyKind 配色（保证可玩不崩溃）。
    /// - NavMesh 追击：每帧读逻辑权威 EnemyMovement（Speed×SpeedMul 缓速）驱动 NavMeshAgent，
    ///   并把 NavMesh 实际位置回写 EnemyPosition（契约 §2：视图 NavMesh 同步回写）；无 NavMesh 时
    ///   （宿主场景未烘焙）回退直接镜像逻辑 EnemyPosition（EnemyChaseSystem 已按玩家追击积分）。
    /// - 追击目标：经 enemy Mod 静态桥 EnemyMod.TryGetPlayerPosition（二进制 ModCall，Rule 14）——
    ///   不 FindGameObjectWithTag / 全局发现（Rule 12 禁例）。
    /// - 死亡：读 EnemyFlags.IsSinking → 下沉动画（sinkSpeed，原版 EnemyHealth 表现）；实体销毁 → 视图自毁
    /// </summary>
    public sealed class EnemyView : MonoBehaviour, IEntityView
    {
        // ---- 原游戏资源 AssetId（Rule 3 / §8.2：路径 = Mod 目录内相对路径去扩展名） ----
        private static readonly AssetId ZombunnyModel = new(EnemyMod.ModIdValue, "Models/Characters/Zombunny");
        private static readonly AssetId ZomBearModel = new(EnemyMod.ModIdValue, "Models/Characters/ZomBear");
        private static readonly AssetId HellephantModel = new(EnemyMod.ModIdValue, "Models/Characters/Hellephant");
        private static readonly AssetId ClownModel = new(EnemyMod.ModIdValue, "ImportThis/Clown");
        private static readonly AssetId DuckModel = new(EnemyMod.ModIdValue, "ImportThis/ZombieDuck");
        private static readonly AssetId EnemyAc = new(EnemyMod.ModIdValue, "Animations/enemyAC");
        // 自带材质（FBX 内嵌材质打包后无纹理 → 用本 Mod .mat 重映射，Rule 3；纹理 GUID 已修复可解析）
        private static readonly AssetId ZombunnyMat = new(EnemyMod.ModIdValue, "Materials/ZombunnyMaterial");
        private static readonly AssetId ZomBearMat = new(EnemyMod.ModIdValue, "Materials/ZombearMaterial");
        private static readonly AssetId HellephantMat = new(EnemyMod.ModIdValue, "Materials/HellephantMaterial");
        private static readonly AssetId ClownMat = new(EnemyMod.ModIdValue, "ImportThis/Materials/Clown_Diff");
        private static readonly AssetId DuckMat = new(EnemyMod.ModIdValue, "ImportThis/Materials/ZomDuckDiffuse");

        /// <summary>视图绑定的敌人实体（宿主 EnemySpawnerView 实例化时写入，契约 §8）。</summary>
        public uint EntityId;

        /// <summary>框架视图映射接口（Rule 12：WeaponView 等跨 Mod 视图经此读取实体 id，零类型引用）。</summary>
        uint IEntityView.EntityId => EntityId;

        /// <summary>下沉速度（对齐原版 EnemyHealth.sinkSpeed=2.5）。</summary>
        public float SinkSpeed = EnemyConfig.SinkSpeed;

        /// <summary>回退图元配色（按 EnemyKind 编号，契约 §1；仅模型加载失败时使用，多色便于识别）。</summary>
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

        /// <summary>回退图元体型（胶囊高度倍率：普通≈1 / 巨型 1.7+ / Titan 2.4；脚底着地）。</summary>
        private static readonly float[] KindScale =
        {
            0.9f, 1.0f, 1.1f, 1.7f, 1.8f, 1.9f, 2.4f, 1.0f, 0.6f, 0.8f,
        };

        /// <summary>
        /// 模型目标身高（原版 Collider 尺寸校准：普通 1.5m、Hellephant 大象 2.6m、
        /// 巨型/Titan 按类别倍率放大；FBX 导入比例差异（如 Hellephant 14m）统一归一化）。
        /// </summary>
        private static readonly float[] KindHeight =
        {
            1.55f, 1.6f, 2.6f, 2.6f, 2.6f, 4.9f, 3.7f, 1.55f, 1.2f, 1.5f,
        };

        private NavMeshAgent? _nav;
        private bool _sinking;
        private bool _visualized;

        private void Awake()
        {
            BuildVisuals(); // 宿主只建空 GameObject → 视图自建 NavMeshAgent（模型在 ApplyKindVisual 按类别加载）
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

            if (!_visualized) ApplyKindVisual(world, e); // 首次实体可见：按 EnemyKind 加载模型/配色体型

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

        /// <summary>自建移动组件（幂等：prefab 已有 Agent 时跳过——资源迁移路径兼容）。</summary>
        private void BuildVisuals()
        {
            // Shootable 层：武器射线命中层（工程未配置该层时保持默认层，WeaponView 回退任意层）
            var shootable = LayerMask.NameToLayer("Shootable");
            if (shootable >= 0) gameObject.layer = shootable;

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

        /// <summary>按 EnemyKind 构建视觉：优先加载原 FBX 模型，失败回退胶囊图元（保留配色/体型）。</summary>
        private void ApplyKindVisual(World world, Entity e)
        {
            _visualized = true;
            if (!world.TryGet<EnemyType>(e, out var type)) return;
            var idx = Mathf.Clamp(type.Kind, 0, KindColors.Length - 1);
            var s = KindScale[idx];

            // 原模型（Rule 3：经本 Mod context.Resources.Load；失败回退图元，不崩溃）
            if (transform.Find("Model") is null)
            {
                if (TryLoadModel(type.Kind, s) == null)
                    ApplyFallbackPrimitive(idx, s); // 模型缺失：回退图元
            }
        }

        /// <summary>
        /// 经 context.Resources.Load 加载原 FBX 并实例化为 "Model" 子节点（Rule 3，禁 Resources.Load/AssetDatabase）。
        /// 返回 null 表示资源缺失（bundle 未装/路径不符）——调用方回退图元。
        /// </summary>
        private GameObject? TryLoadModel(byte kind, float scale)
        {
            var ctx = EnemyMod.Context;
            if (ctx is null) return null; // 未注册（防御）

            GameObject? asset = null;
            try { asset = ctx.Resources.Load(ModelForKind(kind)) as GameObject; }
            catch (System.Exception e) { Debug.LogWarning($"[EnemyView] 模型加载异常 {ModelForKind(kind)}: {e.Message}"); asset = null; }
            if (asset is null)
            {
                Debug.LogWarning($"[EnemyView] 原模型缺失（kind={kind}，AssetId={ModelForKind(kind)}）→ 回退图元");
                return null;
            }

            // 实例化（FBX 根自带导入比例 globalScale≈0.01；不同 FBX 导入尺寸差异大（Hellephant 达 14m））
            var go = Object.Instantiate(asset);
            go.name = "Model";
            go.transform.SetParent(null, false); // 先脱离，避免父级 transform 影响 bounds 计算
            go.transform.position = Vector3.zero;

            // 身高归一化：按类别目标身高（保留 FBX 自然比例 → 等比缩放到 KindHeight）
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length > 0)
            {
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                var h = b.size.y;
                if (h > 0.001f)
                {
                    var s = KindHeight[Mathf.Clamp(kind, 0, KindHeight.Length - 1)] / h;
                    go.transform.localScale = new Vector3(s, s, s);
                }
            }

            // 脚底着地：按渲染 bounds 自动对齐（FBX 根可能在模型中心/脚底，不依赖导入约定）
            var feetY = ComputeFeetLocalY(go);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, -feetY, 0f);

            // Shootable 层（武器射线命中；模型整体入层）
            var shootable = LayerMask.NameToLayer("Shootable");
            if (shootable >= 0) SetLayerRecursive(go, shootable);

            // Collider 供射线命中（FBX 导入 addColliders=0 无碰撞体）：独立 Hitbox 子节点（不受模型缩放污染）
            if (go.GetComponentInChildren<Collider>() == null)
            {
                var hit = new GameObject("Hitbox");
                hit.transform.SetParent(transform, false); // 与 Model 平级（根坐标系，scale=1）
                if (shootable >= 0) hit.layer = shootable;
                var col = hit.AddComponent<CapsuleCollider>();
                col.radius = 0.5f * scale;
                col.height = 1.5f * scale;
                col.center = new Vector3(0f, 0.8f * scale, 0f); // 底部≈脚底
            }

            // Animator 挂 enemyAC.controller（Rule 3 经本 Mod Resources；Avatar 子资产 scope 不可达 → 保持绑定姿态，仍可见）
            var anim = go.GetComponent<Animator>();
            if (anim == null) anim = go.AddComponent<Animator>();
            anim.applyRootMotion = false;
            try { anim.runtimeAnimatorController = ctx.Resources.Load(EnemyAc) as RuntimeAnimatorController; }
            catch (System.Exception ex) { Debug.LogWarning($"[EnemyView] enemyAC 加载异常: {ex.Message}"); }

            // 材质重映射：FBX 内嵌材质打包后无纹理（materialSearch 未命中本 Mod 文件夹）→
            // 用本 Mod 自带 .mat（纹理 GUID 已修复；加载失败保持模型默认材质，不阻塞）
            var matId = MaterialForKind(kind);
            if (matId is not null)
            {
                Material? mat = null;
                try { mat = ctx.Resources.Load(matId.Value) as Material; }
                catch (System.Exception me) { Debug.LogWarning($"[EnemyView] 材质加载异常 {matId}: {me.Message}"); mat = null; }
                if (mat != null)
                {
                    foreach (var r in go.GetComponentsInChildren<Renderer>())
                        r.sharedMaterial = mat;
                }
            }

            Debug.Log($"[EnemyView] 敌人模型已加载 kind={kind} → {ModelForKind(kind)}（材质 {matId}）");
            return go;
        }

        /// <summary>按 EnemyKind 选本 Mod 自带材质（与模型一一对应；巨型/Titan 复用基础材质）。</summary>
        private static AssetId? MaterialForKind(byte kind) => kind switch
        {
            1 or 4 => ZomBearMat,
            2 or 5 => HellephantMat,
            7 or 8 => ClownMat,
            9 => DuckMat,
            _ => ZombunnyMat,
        };

        /// <summary>按 EnemyKind 选模型（巨型/Titan 复用基础模型放大；Clown/MiniClown/ZomDuck 用各自 FBX）。</summary>
        private static AssetId ModelForKind(byte kind) => kind switch
        {
            1 or 4 => ZomBearModel,      // ZomBear / GiantZomBear
            2 or 5 => HellephantModel,   // Hellephant / GiantHellephant
            7 or 8 => ClownModel,        // Clown / MiniClown
            9 => DuckModel,              // ZomDuck
            _ => ZombunnyModel,          // Zombunny / GiantZombunny / Titan 同一模型放大
        };

        /// <summary>按渲染 bounds 算模型脚底在本地坐标系的 Y（不依赖 FBX 导入原点约定）。</summary>
        private static float ComputeFeetLocalY(GameObject model)
        {
            var renderers = model.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return 0f; // 无渲染体：保持根位置
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return model.transform.InverseTransformPoint(b.min).y;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

        /// <summary>回退图元：胶囊 + EnemyKind 配色/体型（模型加载失败时保持可玩，不崩溃）。</summary>
        private void ApplyFallbackPrimitive(int idx, float s)
        {
            if (transform.Find("Body") != null) return; // 幂等

            var shootable = LayerMask.NameToLayer("Shootable");
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(transform, false);
            if (shootable >= 0) body.layer = shootable;

            var renderer = body.GetComponent<Renderer>();
            if (renderer is not null)
                renderer.material = StandardMaterial(KindColors[idx]);

            body.transform.localScale = new Vector3(s, s, s);
            body.transform.localPosition = new Vector3(0f, s, 0f); // 半高 = 胶囊底着地（Y=0）
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
    /// prefab 未挂载（资源 GUID 断链未修）→ 空 GameObject，由 EnemyView.Awake 自建 NavMeshAgent + 按类别加载模型。
    /// </summary>
    public sealed class EnemySpawnerView : MonoBehaviour
    {
        /// <summary>按 EnemyKind 编号索引的敌人 prefab（[0]=Zombunny … [9]=ZomDuck）；Inspector 可挂载，缺省走资源加载。</summary>
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
                : new GameObject($"Enemy_{entityId}"); // prefab 未挂载：空 GO，EnemyView 自建视觉
            go.name = $"Enemy_{entityId}";
            var view = go.GetComponent<EnemyView>() ?? go.AddComponent<EnemyView>();
            view.EntityId = entityId;
        }
    }
}
