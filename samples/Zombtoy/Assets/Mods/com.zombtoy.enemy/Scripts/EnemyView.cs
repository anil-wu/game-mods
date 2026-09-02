using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8）。
    /// 复刻宪法 §0：敌人表现**原原本本用原 prefab**——不再加载裸 FBX + AddComponent 自建 NavMeshAgent，
    /// 而是由 EnemySpawnerView 实例化原敌人 prefab（Zombunny.prefab / ZomBear.prefab / Hellephant.prefab /
    /// Giant Zombunny.prefab / Giant Zombear.prefab / Giant Hellephant.prefab / Titan Zombunny.prefab /
    /// Clown.prefab / MiniClown.prefab / ZomDuckk.prefab，enemy Mod 根目录，AssetId 含扩展名精确匹配，Rule 3）；
    /// 本视图只绑定 prefab 自带组件并按原版行为驱动：
    /// - NavMeshAgent：GetComponent&lt;NavMeshAgent&gt;（prefab 自带；原版 EnemyMovement 用法）每帧按逻辑权威
    ///   EnemyMovement（Speed×SpeedMul 缓速）驱动，并把 NavMesh 实际位置回写 EnemyPosition（契约 §2）；
    ///   无 NavMesh（宿主场景未烘焙）回退镜像逻辑 EnemyPosition（EnemyChaseSystem 已按玩家追击积分）。
    /// - Animator：GetComponent&lt;Animator&gt;（prefab 自带 enemyAC / Clown / ZomDuckk 控制器），死亡时对齐原版
    ///   EnemyHealth：anim.SetTrigger("Dead")（死亡下沉动画）+ StartSinking 平移下沉。
    /// - 射线命中：prefab 自带 Collider（根 CapsuleCollider + SphereCollider）→ GetComponentInParent&lt;EnemyView&gt;
    ///   取 EntityId → enemy:damage（契约 §8，summary §0.6）。
    /// - prefab 上悬空的原脚本组件（EnemyHealth/EnemyMovement 等 m_Script，代码已重写）**忽略**，不依赖不驱动。
    /// - 材质防御：prefab 自带模型材质/贴图链在包内完整时不动；断链/丢贴图时按名重链补（日志声明，Rule 3）。
    /// - 粒子贴图防御（生成/出现特效方块修复）：prefab 粒子材质贴图 GUID 因迁移拆分断链 → EnemySpawnerView
    ///   实例化后按材质名查表补 _MainTex（表见 ParticleTextureIdsByMaterial，16 组经原版工程核对），日志声明。
    ///   扫描用 GetComponentsInChildren&lt;ParticleSystemRenderer&gt;(true) 含 inactive——覆盖 FxTemporaire 死亡特效
    ///   （死亡时才激活的隐藏粒子）；StartDeath（死亡触发）再补链一次（幂等，防死亡粒子材质未在出生时被扫到）。
    /// </summary>
    public sealed class EnemyView : MonoBehaviour, IEntityView
    {
        // ---- 原敌人 prefab AssetId（Rule 3 / §8.2：路径 = Mod 目录内相对路径，**带扩展名 .prefab**——
        // 扩展名精确匹配（资源后端 Load 先精确后 stem 兜底，见 UnityAssetBundleBackend）） ----
        private static readonly AssetId[] PrefabIds =
        {
            new(EnemyMod.ModIdValue, "Zombunny.prefab"),          // 0 Zombunny
            new(EnemyMod.ModIdValue, "ZomBear.prefab"),           // 1 ZomBear
            new(EnemyMod.ModIdValue, "Hellephant.prefab"),        // 2 Hellephant
            new(EnemyMod.ModIdValue, "Giant Zombunny.prefab"),    // 3 Giant Zombunny
            new(EnemyMod.ModIdValue, "Giant Zombear.prefab"),     // 4 Giant Zombear
            new(EnemyMod.ModIdValue, "Giant Hellephant.prefab"),  // 5 Giant Hellephant
            new(EnemyMod.ModIdValue, "Titan Zombunny.prefab"),    // 6 Titan Zombunny（首领）
            new(EnemyMod.ModIdValue, "Clown.prefab"),             // 7 Clown
            new(EnemyMod.ModIdValue, "MiniClown.prefab"),         // 8 MiniClown
            new(EnemyMod.ModIdValue, "ZomDuckk.prefab"),          // 9 ZomDuck（原 prefab 双 k 拼写）
        };

        // 模型自带材质按名重链兜底（仅 prefab 材质/贴图确实丢失时用；AssetId 含扩展名）。
        // 巨型/Titan 复用基础类型材质；MiniClown 复用 Clown。
        private static readonly AssetId[] KindBodyMaterialIds =
        {
            new(EnemyMod.ModIdValue, "Materials/ZombunnyMaterial.mat"),   // 0/3/6 Zombunny 系
            new(EnemyMod.ModIdValue, "Materials/ZombearMaterial.mat"),    // 1/4 Zombear 系
            new(EnemyMod.ModIdValue, "Materials/HellephantMaterial.mat"), // 2/5 Hellephant 系
            new(EnemyMod.ModIdValue, "Materials/ZombunnyMaterial.mat"),
            new(EnemyMod.ModIdValue, "Materials/ZombearMaterial.mat"),
            new(EnemyMod.ModIdValue, "Materials/HellephantMaterial.mat"),
            new(EnemyMod.ModIdValue, "Materials/ZombunnyMaterial.mat"),
            new(EnemyMod.ModIdValue, "ImportThis/Materials/Clown_Diff.mat"),       // 7/8 Clown 系
            new(EnemyMod.ModIdValue, "ImportThis/Materials/Clown_Diff.mat"),
            new(EnemyMod.ModIdValue, "ImportThis/Materials/ZomDuckDiffuse.mat"),   // 9 ZomDuck
        };

        // ---- 粒子材质贴图按名重链表（生成/出现特效方块修复，Rule 3；AssetId 带扩展名精确命中） ----
        // 根因：敌人 prefab 上粒子系统材质（CFX_SmallSpike_AddSoft / CFX_Skull / FluffParticleMaterial 等）的贴图
        // 引用因迁移拆分 + Unity 重导 GUID 被断链（mainTexture==null → 粒子渲染成方块）。
        // 表 = 粒子材质 m_Name → 原贴图：材质/贴图对应关系按原版 Cartoon FX 命名惯例整理，并逐项对照
        // replica_projects/Zombtoy 原版工程（.meta GUID 完整未重导）核对——如 CFX_SmallSpike_AddSoft→
        // CFX_T_SpacedSpike、CFX_Smoke_4frms→CFX_T_Smoke_4Frames.tga、FluffParticleMaterial→PuffSprite.png。
        // 敌人 10 种 prefab 上全部 ParticleSystemRenderer 用到的 16 组材质均在表内；未用材质不进表。
        private const string CfxTextureDir = "Imported/JMO Assets/Cartoon FX/Textures/";
        private static readonly System.Collections.Generic.Dictionary<string, AssetId> ParticleTextureIdsByMaterial = new()
        {
            { "CFX_SmallSpike_AddSoft", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX_T_SpacedSpike.png") },
            { "CFX_Skull", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX_T_Skull.png") },
            { "CFX_Bubble", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX_T_Bubble.png") },
            { "CFX_RayRounded", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX_T_RayRounded.png") },
            { "CFX_Star Add", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX_T_Star Add.png") },
            { "CFX_Smoke_4frms", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX_T_Smoke_4Frames.tga") },
            { "CFX_Text_Boom", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX_T_Text_Boom.tga") },
            { "CFX3_FireBulk ADD", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX3_T_FireBulk.png") },
            { "CFX3_FireSparkle ADD", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX3_T_FireSparkle.png") },
            { "CFX_Anim_Bubble_Add", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX_T_Anim4_Bubble.png") },
            { "CFX_Anim_Poof_Add", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX_T_Anim4_RoundedLines.png") },
            { "CFX_Anim_Triangle2_AlphaBlendedPremult", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX_T_Anim8_Triangle2.png") },
            { "CFX2_Glow", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX2_T_Glow.png") },
            { "CFX2_SkullStretched Add", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX2_T_SkullStretch.png") },
            { "CFX2_WWSmoke2 AB", new AssetId(EnemyMod.ModIdValue, CfxTextureDir + "CFX2_T_WWInvSmoke2 AB.png") },
            { "FluffParticleMaterial", new AssetId(EnemyMod.ModIdValue, "Textures/PuffSprite.png") },
        };

        /// <summary>已声明缺失的粒子材质名（同材质多次断链时缺失日志只打一次，避免每生成一个敌人刷屏）。</summary>
        private static readonly System.Collections.Generic.HashSet<string> ParticleTexMissingLogged = new();

        /// <summary>按 EnemyKind 编号取原 prefab AssetId（越界防御 → Zombunny）。</summary>
        public static AssetId PrefabIdForKind(byte kind) =>
            PrefabIds[kind < PrefabIds.Length ? kind : 0];

        /// <summary>视图绑定的敌人实体（宿主 EnemySpawnerView 实例化时写入，契约 §8）。</summary>
        public uint EntityId;

        /// <summary>框架视图映射接口（Rule 12：WeaponView 等跨 Mod 视图经此读取实体 id，零类型引用）。</summary>
        uint IEntityView.EntityId => EntityId;

        /// <summary>下沉速度（对齐原版 EnemyHealth.sinkSpeed=2.5）。</summary>
        public float SinkSpeed = EnemyConfig.SinkSpeed;

        // prefab 自带组件（非自建）：GetComponent 对内置组件可能返回 fake-null（is null/?? 不识别），
        // 全部按 Unity 重载的 == null 判空（null = 原 prefab 缺组件 / 空 GO 兜底）。
        private NavMeshAgent? _nav;
        private Animator? _anim;
        private Rigidbody? _rb;
        private CapsuleCollider? _capsule;
        private bool _bound;   // 组件绑定/出生摆位只做一次
        private bool _dying;   // 死亡表现只触发一次（对齐原版 EnemyHealth.Death()/StartSinking()）
        private bool _sinking; // 下沉中（每帧 transform.Translate 下沉）

        // ---- 视图注册表（同 Mod 的 EnemyBossView 定位 Titan 实例，取 GroundTarget 准星/开火位；
        // EntityId 由宿主 EnemySpawnerView 在 AddComponent 后写入，注册放首次绑定处） ----
        private static readonly System.Collections.Generic.Dictionary<uint, EnemyView> ByEntityId = new();

        /// <summary>按实体取敌人视图实例（无 = 尚未生成/已销毁/无头环境）。</summary>
        public static bool TryGet(uint entityId, out EnemyView view) => ByEntityId.TryGetValue(entityId, out view!);

        private void RegisterInstance()
        {
            if (EntityId == 0) return;
            ByEntityId[EntityId] = this;
        }

        private void OnDestroy()
        {
            if (EntityId != 0 && ByEntityId.TryGetValue(EntityId, out var cur) && cur == this)
                ByEntityId.Remove(EntityId);
        }

        private void Awake()
        {
            // Shootable 层：武器射线命中层（宿主工程按名解析；未配置该层时保持 prefab 原层，
            // WeaponView 回退任意层）。Awake 里解析（Unity 禁止字段初始化调 NameToLayer）。
            var shootable = LayerMask.NameToLayer("Shootable");
            if (shootable >= 0) gameObject.layer = shootable;
        }

        private void Update()
        {
            var world = EnemyMod.World;
            if (world is null || EntityId == 0) return;

            var e = new Entity(EntityId);
            if (!world.Exists(e))
            {
                Destroy(gameObject); // 实体已销毁（下沉到点 / reset 清场）→ 视图自毁
                return;
            }
            if (!world.TryGet<EnemyFlags>(e, out var flags)) return;
            if (!world.TryGet<EnemyType>(e, out var type)) return;

            if (!_bound) BindInstance(world, e, type.Kind); // 组件来自原 prefab 实例 + 出生摆位（幂等）

            if (flags.IsDead)
            {
                // 死亡：触发原版 EnemyHealth 死亡表现（Dead 动画触发 + StartSinking 处理一次），随后平移下沉
                if (!_dying) StartDeath();
                if (_sinking)
                    transform.Translate(Vector3.down * SinkSpeed * Time.deltaTime);
                return;
            }

            // 存活：NavMesh 追击玩家（prefab 自带 NavMeshAgent；视图按 NavMesh 避障路径走）
            if (_nav != null && _nav.enabled && _nav.isOnNavMesh)
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
            else if (_nav is null || !_nav.enabled)
            {
                // 无 NavMesh（prefab 缺失兜底/宿主未烘焙，回退）：直接按逻辑位置表现（EnemyChaseSystem 已积分追击）
                if (world.TryGet<EnemyPosition>(e, out var pos))
                    transform.position = new Vector3(pos.X, pos.Y, pos.Z);
            }
        }

        /// <summary>
        /// 首次绑定：从原 prefab 实例 GetComponent 拿自带组件（对齐原版 EnemyHealth/EnemyMovement
        /// GetComponent&lt;NavMeshAgent/Animator/Rigidbody/CapsuleCollider&gt; 用法——不 AddComponent 自建），
        /// 并按 ECS 出生点 EnemyPosition 摆位（NavMeshAgent Warp 落位，避免从 prefab 默认原点开跑）。
        /// </summary>
        private void BindInstance(World world, Entity e, byte kind)
        {
            _bound = true;
            RegisterInstance(); // 视图注册表（EnemyBossView 定位 Titan 实例）
            _nav = GetComponent<NavMeshAgent>();
            _anim = GetComponent<Animator>();
            _rb = GetComponent<Rigidbody>();
            _capsule = GetComponent<CapsuleCollider>();
            Debug.Log($"[EnemyView] 绑定原 prefab 实例 kind={kind} → {PrefabIdForKind(kind).Path}："
                      + $"NavMeshAgent={_nav != null} Animator={_anim != null} Rigidbody={_rb != null} "
                      + $"CapsuleCollider={_capsule != null} Colliders={GetComponentsInChildren<Collider>().Length}");

            // 出生摆位：逻辑出生点（契约 §2 EnemyPosition）；有 NavMesh 时 Warp 落位（可追击）
            var spawn = transform.position;
            if (world.TryGet<EnemyPosition>(e, out var ep))
                spawn = new Vector3(ep.X, ep.Y, ep.Z);
            transform.position = spawn;
            if (_nav != null && _nav.enabled)
            {
                var warped = false;
                try { warped = _nav.Warp(spawn); }
                catch (System.Exception ex) { Debug.LogWarning($"[EnemyView] Warp 异常: {ex.Message}"); warped = false; }
                if (!warped) _nav.enabled = false; // 落不上 NavMesh（未烘焙）→ 走镜像逻辑位置路径
                Debug.Log($"[EnemyView] 出生摆位 kind={kind} @ ({spawn.x:F1},{spawn.y:F1},{spawn.z:F1}) warp={warped}");
            }
            else if (_nav is null)
            {
                Debug.LogWarning($"[EnemyView] 实例无 NavMeshAgent（原 prefab 缺失走空 GO 兜底）kind={kind} → 按逻辑位置镜像");
            }

            RelinkBrokenMaterials(kind); // 材质/贴图防御：prefab 链断链时按名重链（日志声明）
        }

        /// <summary>
        /// 死亡表现（对齐原版 EnemyHealth.Death() + StartSinking()，只触发一次）：
        /// anim.SetTrigger("Dead")（enemyAC 死亡下沉动画）、NavMeshAgent 停用、Rigidbody 转运动学
        /// （防物理抖动/尸体挡路）、CapsuleCollider 转 Trigger（尸体不再挡路；射线命中语义同原版）。
        /// 随后 Update 每帧按 sinkSpeed 平移下沉（原版 transform.Translate(-Vector3.up*sinkSpeed*dt)），
        /// 到点由 EnemyHealthSystem 销毁实体 → 本视图随实体销毁自毁。
        /// </summary>
        private void StartDeath()
        {
            _dying = true;
            if (_anim != null)
            {
                try { _anim.SetTrigger("Dead"); } // 参数不存在时静默忽略（不抛异常），表现即平移下沉
                catch (System.Exception ex) { Debug.LogWarning($"[EnemyView] 死亡动画触发异常: {ex.Message}"); }
            }
            if (_nav != null) _nav.enabled = false;      // 原版 StartSinking：停追击代理
            if (_rb != null) _rb.isKinematic = true;     // 原版 StartSinking：运动学防物理干扰
            if (_capsule != null) _capsule.isTrigger = true; // 原版 Death：尸体转 Trigger 不挡路
            _sinking = true;
            RelinkBrokenParticleTextures(); // 死亡触发时对死亡特效（FxTemporaire 死亡时才激活）粒子再次补链
                                           // （幂等：出生时已修/材质完好的直接跳过，日志声明；覆盖死亡粒子方块）
            Debug.Log("[EnemyView] 敌人死亡：Dead 动画触发 + 停 NavMeshAgent + Rigidbody 运动学 + 下沉");
        }

        /// <summary>
        /// AnimationEvent 接收器（原版 EnemyHealth.StartSinking 被 enemyAC 'Death' 动画事件调用）：
        /// 触发下沉；与 StartDeath 幂等（死亡表现只触发一次）。EnemyView 挂在原敌人 prefab 实例上（与 Animator 同 GameObject），
        /// 故此公开方法能作为 'StartSinking' 动画事件的接收器，消除 “has no receiver” 警告。
        /// </summary>
        public void StartSinking()
        {
            if (_dying) { _sinking = true; return; }
            StartDeath();
        }

        /// <summary>
        /// 材质/贴图防御（复刻宪法允许的原版资源缺失回退，日志声明）：prefab 自带模型材质完好（sharedMaterial
        /// 非空且 mainTexture 非空）时**不干预**；断链/丢贴图时经 context.Resources.Load 按 EnemyKind 取
        /// 本 Mod 自带材质（AssetId 带扩展名精确命中，Rule 3）整条补链。渲染体对象名含类型名（Zombunny/Zombear/
        /// Hellephant/Clown/ZomDuck…）的才算敌人模型，不误伤粒子装饰。
        /// </summary>
        private void RelinkBrokenMaterials(byte kind)
        {
            if (EnemyMod.Context is null) return;
            var modelNames = new[] { "Zombunny", "Zombear", "Zombear", "Hellephant", "Clown", "ZomDuck", "Duck" };
            var matId = KindBodyMaterialIds[kind < KindBodyMaterialIds.Length ? kind : 0];
            Material? mat = null;
            var relinked = 0;
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (r is null) continue;
                // 只认敌人模型渲染体（按对象名识别；巨型/Titan 子节点名是基础类型名）
                var isBody = false;
                foreach (var n in modelNames)
                {
                    if (r.gameObject.name.StartsWith(n, System.StringComparison.Ordinal)) { isBody = true; break; }
                }
                if (!isBody) continue;
                var cur = r.sharedMaterial;
                if (cur != null && cur.mainTexture != null) continue; // 原材质完好：不干预

                if (mat is null)
                {
                    try { mat = EnemyMod.Context.Resources.Load(matId) as Material; }
                    catch (System.Exception ex) { Debug.LogWarning($"[EnemyView] 材质重链加载异常 {matId}: {ex.Message}"); mat = null; }
                    if (mat is null) { Debug.LogWarning($"[EnemyView] 材质重链失败（{matId}）→ 保持原样"); return; }
                }
                r.sharedMaterial = mat;
                relinked++;
            }
            if (relinked > 0)
                Debug.LogWarning($"[EnemyView] 敌人模型材质断链 → 按名重链 {relinked} 个渲染体（{matId.Path}，原版资源缺失回退，日志声明）");
        }

        /// <summary>
        /// 敌人 prefab 粒子贴图按名重链（生成/出现特效方块修复；复刻宪法允许的原版资源缺失回退，日志声明）。
        /// 粒子材质贴图完好（mainTexture 非空）时不干预；断链（mainTexture==null，渲染成方块）时按粒子材质
        /// m_Name 查 ParticleTextureIdsByMaterial 表，经本 Mod context.Resources.Load 取原贴图
        /// （AssetId 带扩展名精确命中，Rule 3）+ mat.SetTexture("_MainTex", tex) 补链（同 WeaponView 枪口先例）。
        /// 扫描用 GetComponentsInChildren&lt;ParticleSystemRenderer&gt;(true)：**含 inactive 对象**——prefab 上默认隐藏的
        /// 粒子（FxTemporaire 死亡特效，死亡时才激活/播发）不因激活态漏扫。出生实例化后与死亡触发（StartDeath）各调一次。
        /// 查表不到（非敌人粒子材质）不干预保持原样；贴图缺失时日志声明且不换默认材质。
        /// 修复写共享材质实例：同一材质的所有渲染体/后续生成一并恢复（幂等，之后 mainTexture 非空直接跳过）。
        /// </summary>
        internal void RelinkBrokenParticleTextures()
        {
            var ctx = EnemyMod.Context;
            if (ctx is null) return; // 未注册（防御：空 GO 兜底/编辑器预览）
            var relinked = 0;
            foreach (var psr in GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (psr is null) continue;
                var mat = psr.sharedMaterial;
                if (mat is null) continue;
                if (mat.mainTexture != null) continue; // 原粒子材质/贴图完好：不干预
                if (!ParticleTextureIdsByMaterial.TryGetValue(mat.name, out var texId)) continue; // 非表内材质：保持原样
                Texture? tex = null;
                try { tex = ctx.Resources.Load(texId) as Texture; }
                catch (System.Exception ex) { Debug.LogWarning($"[EnemyView] 粒子贴图加载异常 {texId}: {ex.Message}"); tex = null; }
                if (tex is null)
                {
                    if (ParticleTexMissingLogged.Add(mat.name)) // 只声明一次
                        Debug.LogWarning($"[EnemyView] 粒子贴图缺失（{mat.name}）→ 保持原样（原版资源缺失回退，日志声明）");
                    continue;
                }
                mat.SetTexture("_MainTex", tex);
                relinked++;
                Debug.Log($"[EnemyView] 粒子贴图已按名重链 {mat.name} → {texId.Path}（生成/出现特效方块修复，日志声明）");
            }
            if (relinked > 0)
                Debug.LogWarning($"[EnemyView] 敌人粒子材质贴图断链 → 按名重链 {relinked} 个粒子渲染体（原版资源缺失回退，日志声明）");
        }
    }

    /// <summary>
    /// 宿主生成器视图（契约 §8）：订阅 EnemyMod.OnEnemySpawned，按 EnemyKind 实例化**原敌人 prefab**
    /// （EnemyView.PrefabIdForKind 经本 Mod context.Resources.Load，Rule 3）并绑定 EntityId；
    /// prefab 自带 NavMeshAgent/Animator/双 Collider/模型，EnemyView 只绑定不重建。
    /// 原 prefab 确实缺失（bundle 未装）时回退空 GO（日志声明；不建自建胶囊/NavMeshAgent 近似物，复刻宪法）。
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
            if (type.Kind >= PrefabsByKind.Length) return; // 防御

            // 原敌人 prefab（优先 Inspector 挂载；缺省经 context.Resources.Load，Rule 3）
            var prefab = PrefabsByKind[type.Kind] != null ? PrefabsByKind[type.Kind] : LoadPrefab(type.Kind);

            GameObject go;
            if (prefab is null)
            {
                Debug.LogError($"[EnemySpawnerView] 原敌人 prefab 缺失 kind={type.Kind}（{EnemyView.PrefabIdForKind(type.Kind).Path}）"
                               + "→ 空 GO 兜底（无视觉/无碰撞；原版资源缺失回退，日志声明；不自建胶囊近似物）");
                go = new GameObject($"Enemy_{entityId}");
            }
            else
            {
                go = Instantiate(prefab);
                Debug.Log($"[EnemySpawnerView] 敌人实例化 kind={type.Kind} → {EnemyView.PrefabIdForKind(type.Kind).Path}"
                          + "（原 prefab：NavMeshAgent/Animator/Rigidbody/双 Collider/模型自带，无自建组件）");
            }
            go.name = $"Enemy_{entityId}";
            var view = go.GetComponent<EnemyView>() ?? go.AddComponent<EnemyView>();
            view.EntityId = entityId;
            view.RelinkBrokenParticleTextures(); // 实例化后立即按名重链粒子贴图（生成/出现特效方块修复，日志声明）
        }

        /// <summary>经本 Mod context.Resources.Load 加载原敌人 prefab（Rule 3；AssetId 带扩展名 .prefab 精确命中；失败返回 null）。</summary>
        private static GameObject? LoadPrefab(byte kind)
        {
            var ctx = EnemyMod.Context;
            if (ctx is null) return null; // 未注册（防御）
            var id = EnemyView.PrefabIdForKind(kind);
            GameObject? prefab = null;
            try { prefab = ctx.Resources.Load(id) as GameObject; }
            catch (System.Exception ex) { Debug.LogWarning($"[EnemySpawnerView] 敌人 prefab 加载异常 {id}: {ex.Message}"); prefab = null; }
            if (prefab is null) Debug.LogWarning($"[EnemySpawnerView] 原敌人 prefab 加载失败（{id.Path}）");
            return prefab;
        }
    }
}
