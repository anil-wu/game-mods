using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;
using UnityEngine;

namespace Com.Zombtoy.Weapon
{
    /// <summary>
    /// 投射物视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8；复刻宪法 §0）。
    /// 宿主 ModRuntime 自动实例化本视图（InstantiateViews）→ OnEnable 订阅 WeaponMod.OnProjectileSpawned/
    /// OnProjectileDestroyed（Rule 20 对称撤销）：
    /// - 按 Projectile.Kind 经 context.Resources.Load 加载**原投射物 prefab**（Rule 3，AssetId 带扩展名 .prefab 精确命中）：
    ///   RocketLauncher → Rocket.prefab；CryoPistol → IceBullet.prefab；Tornado → Tornado.prefab。
    /// - Instantiate 后每帧按逻辑 Projectile 实体同步位置/朝向（逻辑权威；原版 Rocket/IceBullet/Tornado 都沿自身
    ///   +Z 前向 Translate，故 rotation = LookRotation(飞行方向)）；实体命中/超时/销毁 → 视图销毁。
    /// - 出膛表现：Rocket 引擎火焰（Fire_B 组 loop）playOnAwake 自带、爆炸组（WFX_ExplosiveSmokeGround Big Alt，
    ///   原版 Rocket.cs explosion 字段指向它）发射时 Pause、命中才 Play（对齐原版 Start explosion.Pause()）；
    ///   Tornado 出膛 Play 自带 AudioSource（原版 Tornado.cs Start）；IceBullet TrailRenderer 原 prefab 自带。
    /// - Rocket 命中爆炸：用原 prefab 自带爆炸特效（悬空原脚本忽略）：爆炸组脱离火箭播放后自灭、弹体即隐
    ///   （对齐原版 collided→explosion.Play()+mesh.enabled=false+DestroyTime 销毁）。
    /// - 断链重链（迁移后 prefab 内嵌 guid 引用在 Bundle 内失效 → 原版资源缺失回退，日志声明）：渲染体材质为空的
    ///   按对象名取本 Mod 同路素材整条补链（扩展名精确匹配 + SetTexture 补贴图）；Rocket 弹体网格按名从原
    ///   Rocket9.fbx 补。物理组件（Collider/Rigidbody，原版命中判定用）在 ECS 版归逻辑裁决 → 实例上关闭，
    ///   避免视图物理推挤敌人/挡路（表现不受影响）。
    /// </summary>
    public sealed class ProjectileView : MonoBehaviour
    {
        // ---- 原投射物 prefab AssetId（Rule 3 / CONTRACT.md §8：路径 = Mod 目录内相对路径，带扩展名 .prefab） ----
        public static readonly AssetId RocketPrefabId = new(WeaponMod.ModIdValue, "Rocket.prefab");
        public static readonly AssetId IceBulletPrefabId = new(WeaponMod.ModIdValue, "IceBullet.prefab");
        public static readonly AssetId TornadoPrefabId = new(WeaponMod.ModIdValue, "Tornado.prefab");


        // 断链重链资产：迁移后原 prefab 内嵌引用（旧 guid）在 Bundle 内失效，素材按原路径在本 Mod Bundle 内
        // （Rule 3 按名取、AssetId 带扩展名精确命中）。映射经 Rocket.prefab/IceBullet.prefab/Tornado.prefab
        // 序列化引用逐条比对得出（只补确实断链的渲染体，完好引用不干预）。
        private static readonly AssetId RocketBodyMatId = new(WeaponMod.ModIdValue, "Imported/Rocket Pack/Materials/Grey.mat");
        private static readonly AssetId RocketBodyTexId = new(WeaponMod.ModIdValue, "Imported/Rocket Pack/Textures/Grey.png");
        private static readonly AssetId RocketBodyFbxId = new(WeaponMod.ModIdValue, "Imported/Rocket Pack/FBX/Rocket9.fbx"); // 弹体网格
        private static readonly AssetId FlameMatId = new(WeaponMod.ModIdValue, "Imported/EffectTexturesAndPrefabs/Materials/Fire_A.mat");
        private static readonly AssetId FlameTexId = new(WeaponMod.ModIdValue, "Imported/EffectTexturesAndPrefabs/Textures/fire_A.png");
        private static readonly AssetId FlameParticleMatId = new(WeaponMod.ModIdValue, "Imported/EffectTexturesAndPrefabs/Materials/FireParticle_A.mat");
        private static readonly AssetId FlameParticleTexId = new(WeaponMod.ModIdValue, "Imported/EffectTexturesAndPrefabs/Textures/particle_A.png");
        private static readonly AssetId FlameSmokeMatId = new(WeaponMod.ModIdValue, "Imported/EffectTexturesAndPrefabs/Materials/Smoke_thin.mat");
        private static readonly AssetId FlameSmokeTexId = new(WeaponMod.ModIdValue, "Imported/EffectTexturesAndPrefabs/Textures/smoke_n.png");
        // CFX 火球爆炸组（CFX_Explosion_B_Smoke+Text，playOnAwake=0，命中不播仅随 prefab 保留；补链保证自带特效可用）
        private static readonly AssetId CfxTriangleMatId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Materials/Animated/CFX_Anim_Triangle2_AlphaBlendedPremult.mat");
        private static readonly AssetId CfxTriangleTexId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Textures/CFX_T_Anim8_Triangle2.png");
        private static readonly AssetId CfxBubbleMatId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Materials/Animated/CFX_Anim_Bubble_Add.mat");
        private static readonly AssetId CfxBubbleTexId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Textures/CFX_T_Anim4_Bubble.png");
        private static readonly AssetId CfxPoofMatId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Materials/Animated/CFX_Anim_Poof_Add.mat");
        private static readonly AssetId CfxPoofTexId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Textures/CFX_T_Anim4_RoundedLines.png");
        private static readonly AssetId CfxSmokeMatId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Materials/Misc/CFX_Smoke_4frms.mat");
        private static readonly AssetId CfxSmokeTexId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Textures/CFX_T_Smoke_4Frames.tga");
        private static readonly AssetId CfxBoomTextMatId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Materials/Texts/CFX_Text_Boom.mat");
        private static readonly AssetId CfxBoomTextTexId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Textures/CFX_T_Text_Boom.tga");
        // Tornado / IceBullet
        private static readonly AssetId TornadoFunnelMatId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Materials/Misc/CFX_Tornado.mat");
        private static readonly AssetId TornadoFunnelTexId = new(WeaponMod.ModIdValue, "Imported/JMO Assets/Cartoon FX/Textures/CFX_T_Tornado.png");
        private static readonly AssetId IceTrailMatId = new(WeaponMod.ModIdValue, "New Material.mat"); // IceBullet 拖尾（原引用根目录同名资产）

        private sealed class ProjectileVisual
        {
            public GameObject Go = null!;
            public bool LaunchKicked; // 出膛一次性动作（Tornado 音效），只做一次
        }

        private readonly Dictionary<uint, ProjectileVisual> _instances = new();

        private void OnEnable()
        {
            WeaponMod.OnProjectileSpawned += HandleSpawned;
            WeaponMod.OnProjectileDestroyed += HandleDestroyed;
        }

        private void OnDisable()
        {
            WeaponMod.OnProjectileSpawned -= HandleSpawned;
            WeaponMod.OnProjectileDestroyed -= HandleDestroyed;
            DestroyAllInstances();
        }

        private void Update()
        {
            var world = WeaponMod.World;
            if (_instances.Count == 0) return;
            if (world is null)
            {
                DestroyAllInstances(); // 卸载竞态：逻辑 World 已不可用 → 视图清场
                return;
            }

            // 同步/清理：实体销毁（命中/超时/reset 清场/卸载）→ 视图销毁（事件竞态兜底：事件未达也按实体状态自清）
            var stale = new List<uint>();
            foreach (var kv in _instances)
            {
                var id = kv.Key;
                var vis = kv.Value;
                if (vis.Go == null) { stale.Add(id); continue; }

                var e = new Entity(id);
                if (!world.Exists(e) || !world.TryGet<Projectile>(e, out var p))
                {
                    stale.Add(id); // 实体已销毁（漏通知路径）：视图随销毁
                    continue;
                }

                // 逻辑位置/朝向 → 视图（原版沿 +Z 前向 Translate，LookRotation(dir) 使前向对齐飞行方向）
                vis.Go.transform.position = new Vector3(p.X, p.Y, p.Z);
                var fwd = new Vector3(p.DirX, p.DirY, p.DirZ);
                if (fwd.sqrMagnitude > 0.0001f) vis.Go.transform.rotation = Quaternion.LookRotation(fwd);

                if (!vis.LaunchKicked)
                {
                    vis.LaunchKicked = true;
                    KickLaunch(p.Kind, vis.Go); // 出膛一次性动作（Tornado 音效；Rocket 尾焰/爆炸暂停已在实例化时处理）
                }
            }
            foreach (var id in stale) DestroyInstance(id);
        }

        // ---- 事件桥（WeaponMod.NotifyProjectileSpawned/Destroyed，CONTRACT.md §8） ----

        private void HandleSpawned(uint entityId, byte kind)
        {
            if (_instances.ContainsKey(entityId)) return;

            var prefab = LoadPrefab(kind);
            if (prefab is null)
            {
                // 原 prefab 缺失（bundle 未装）：逻辑实体仍按无视觉飞行（超时/命中照常），仅记日志
                Debug.LogError($"[ProjectileView] 原投射物 prefab 缺失（kind={KindName(kind)}）→ 无视觉（原版资源缺失回退）");
                return;
            }

            var go = Instantiate(prefab);
            go.name = $"Projectile_{KindName(kind)}_{entityId}";
            PrepareInstance(go, kind); // 断链重链 + 物理中性化 + Rocket 爆炸组暂停（原版 Start explosion.Pause()）
            Debug.Log($"[ProjectileView] 投射物视图实例化 kind={KindName(kind)} entity={entityId} → {PrefabId(kind).Path}"
                      + "（原 prefab：模型/尾焰/爆炸粒子/灯光/音效自带，悬空原脚本忽略）");
            _instances[entityId] = new ProjectileVisual { Go = go };
        }

        /// <summary>命中/超时/清场销毁。Rocket 命中（hitBurst）→ 用原 prefab 自带爆炸组播爆发（对齐原版
        /// collided → explosion.Play()），弹体即隐、定时销毁；超时/其他命中 → 视图直接销毁（原版 Destroy）。</summary>
        private void HandleDestroyed(uint entityId, byte kind, bool hitBurst)
        {
            if (!_instances.TryGetValue(entityId, out var vis)) return;
            _instances.Remove(entityId);

            if (hitBurst && kind == (byte)WeaponKind.RocketLauncher && vis.Go != null)
            {
                BurstRocket(vis.Go);
                return;
            }
            if (vis.Go != null) Destroy(vis.Go);
        }

        private void DestroyInstance(uint entityId)
        {
            if (!_instances.TryGetValue(entityId, out var vis)) return;
            _instances.Remove(entityId);
            if (vis.Go != null) Destroy(vis.Go);
        }

        private void DestroyAllInstances()
        {
            foreach (var kv in _instances)
                if (kv.Value.Go != null) Destroy(kv.Value.Go);
            _instances.Clear();
        }

        // ---- 出膛/命中表现 ----

        /// <summary>出膛一次性动作（对齐原版 Start）：Tornado Play 自带 AudioSource；Rocket/IceBullet 无额外动作
        /// （尾焰/拖尾 playOnAwake 自带；Rocket 爆炸组在实例化时已 Pause，命中由 BurstRocket Play）。</summary>
        private static void KickLaunch(byte kind, GameObject go)
        {
            if (kind != (byte)WeaponKind.Tornado) return;
            try
            {
                var src = go.GetComponent<AudioSource>();
                if (src != null && !src.isPlaying) src.Play();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ProjectileView] Tornado 音效播放异常: {ex.Message}");
            }
        }

        /// <summary>Rocket 命中爆发：原 prefab 自带爆炸组 WFX_ExplosiveSmokeGround Big Alt（原版 Rocket.cs
        /// explosion 字段指向的组）脱离火箭播一次后自灭，弹体（模型/尾焰/灯光）即隐（对齐原版
        /// explosion.Play() + mesh.enabled=false + DestroyTime 后销毁）。悬空原脚本忽略，不自建近似特效。</summary>
        private static void BurstRocket(GameObject go)
        {
            try
            {
                var boom = FindChild(go.transform, "WFX_ExplosiveSmokeGround Big Alt")
                           ?? FindChild(go.transform, "CFX_Explosion_B_Smoke+Text")
                           ?? FindChild(go.transform, "Explosion"); // 兜底：任何爆炸子对象
                if (boom != null)
                {
                    boom.SetParent(null, true); // 爆炸组脱离弹体（世界根），弹体随爆炸后自毁
                    foreach (var ps in boom.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        if (ps == null) continue;
                        ps.Stop(true);
                        ps.Clear(true);
                        ps.Play(true); // 原版 explosion.Pause()→Play()：从起始播完整爆发
                    }
                    Destroy(boom.gameObject, 4f); // 粒子播完自灭（原 CFX_AutoDestructShuriken 悬空 → 手动定时）
                    go.SetActive(false);          // 弹体不可见（原版 mesh.enabled=false；尾焰/灯光随整机隐藏）
                    Destroy(go, 0.8f);            // 原版 Rocket.cs DestroyTime=0.8
                }
                else
                {
                    // 无爆炸子对象（bundle 缺资产兜底）：播自带粒子后整机自毁
                    foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        if (ps != null && !ps.isPlaying) ps.Play();
                    }
                    Destroy(go, 1.5f);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ProjectileView] Rocket 命中爆炸表现异常: {ex.Message}");
            }
        }

        private static Transform? FindChild(Transform root, string name)
        {
            foreach (Transform t in root)
            {
                if (t.name == name) return t;
                var nested = FindChild(t, name);
                if (nested != null) return nested;
            }
            return null;
        }

        // ---- 实例化准备：断链重链 + 物理中性化 + Rocket 爆炸组暂停 ----

        private static void PrepareInstance(GameObject go, byte kind)
        {
            NeutralizePhysics(go);        // 命中判定归逻辑（ECS）→ 视图实例不参与物理
            RelinkBrokenAssets(go, kind); // 原 prefab 引用断链 → 按名重链（日志声明）
            if (kind == (byte)WeaponKind.RocketLauncher)
                PauseRocketExplosion(go); // 原版 Rocket.cs Start：爆炸组 Pause，命中才播
        }

        /// <summary>视图实例物理中性化：原 prefab 自带 Collider/Rigidbody 服务于原版物理命中判定；ECS 版命中/
        /// 伤害由系统裁决（契约 §2 逻辑权威），保留物理体会推挤敌人/挡路并因每帧硬置位置而抖动 → 实例上关闭
        /// （渲染/粒子/灯光/音效不受影响）。</summary>
        private static void NeutralizePhysics(GameObject go)
        {
            try
            {
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }
                foreach (var col in go.GetComponentsInChildren<Collider>(true))
                    if (col != null) col.enabled = false;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ProjectileView] 视图物理中性化异常: {ex.Message}");
            }
        }

        /// <summary>Rocket 爆炸组暂停（对齐原版 Rocket.cs Start() explosion.Pause()：组自带粒子 playOnAwake，
        /// 发射时不播、命中由 BurstRocket 播完整爆发）。</summary>
        private static void PauseRocketExplosion(GameObject go)
        {
            try
            {
                var group = FindChild(go.transform, "WFX_ExplosiveSmokeGround Big Alt");
                if (group == null) return;
                var ps = group.GetComponent<ParticleSystem>();
                if (ps != null) ps.Pause(true); // 连带子树（Explosion/Smoke/Glow/Dot Sparkles…）
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ProjectileView] Rocket 爆炸组暂停异常: {ex.Message}");
            }
        }

        // ---- 断链重链（复刻宪法允许的原版资源缺失回退，日志声明） ----

        /// <summary>渲染体按对象名取对应重链素材（完好引用不干预；映射按原 prefab 序列化引用逐条比对得出）。</summary>
        private static (AssetId Mat, AssetId? Tex)? RelinkSlotFor(byte kind, string goName)
        {
            switch (kind)
            {
                case (byte)WeaponKind.RocketLauncher:
                    switch (goName)
                    {
                        case "Rocket": return (Mat: RocketBodyMatId, Tex: RocketBodyTexId);                    // 弹体（原 Rocket Pack Grey）
                        case "Fire": return (Mat: FlameMatId, Tex: FlameTexId);                                // 引擎火焰（Fire_B 组）
                        case "FireParticle": return (Mat: FlameParticleMatId, Tex: FlameParticleTexId);
                        case "smoke": return (Mat: FlameSmokeMatId, Tex: FlameSmokeTexId);
                        case "CFX_Explosion_B_Smoke+Text": return (Mat: CfxTriangleMatId, Tex: CfxTriangleTexId);
                        case "Bubble": return (Mat: CfxBubbleMatId, Tex: CfxBubbleTexId);
                        case "Lines": return (Mat: CfxPoofMatId, Tex: CfxPoofTexId);
                        case "Smoke": return (Mat: CfxSmokeMatId, Tex: CfxSmokeTexId);                          // CFX 组 Smoke（WFX 组 Smoke 引用完好不触发）
                        case "Text Boom": return (Mat: CfxBoomTextMatId, Tex: CfxBoomTextTexId);
                    }
                    return null;
                case (byte)WeaponKind.Tornado:
                    return goName == "CFX_Tornado" ? (Mat: TornadoFunnelMatId, Tex: TornadoFunnelTexId) : null;
                case (byte)WeaponKind.CryoPistol:
                    return goName == "IceBullet" ? (Mat: IceTrailMatId, Tex: null) : null; // 拖尾（根节点；原引用根目录 New Material.mat）
                default:
                    return null;
            }
        }

        /// <summary>投射物材质/贴图断链重链（日志声明）：prefab 内嵌材质引用 guid 迁移后失效（渲染体无材质）
        /// → 经 context.Resources.Load 按对象名取本 Mod 自带材质整条补链（AssetId 带扩展名精确命中，Rule 3）；
        /// 材质内贴图引用同样断链（mainTexture 空）→ 按名 SetTexture(_MainTex)。Rocket 弹体网格（MeshFilter
        /// 引原 Rocket9.fbx 断链）按名从原 FBX 取网格补。仅对空材质渲染体操作，不误伤完好引用。</summary>
        private static void RelinkBrokenAssets(GameObject root, byte kind)
        {
            if (WeaponMod.Context is null) return;
            var relinked = 0;
            try
            {
                // Rocket 弹体网格补链（MeshFilter 引 Rocket9.fbx，迁移失效 → 从原 FBX 取网格）
                if (kind == (byte)WeaponKind.RocketLauncher)
                {
                    var body = root.transform; // 根 GO 即 Rocket 弹体（含 MeshFilter/MeshRenderer/Rigidbody）
                    var mf = body.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh == null)
                    {
                        var fbx = LoadAsset(RocketBodyFbxId) as GameObject;
                        if (fbx != null)
                        {
                            var src = fbx.GetComponentInChildren<MeshFilter>();
                            if (src != null && src.sharedMesh != null)
                            {
                                mf.sharedMesh = src.sharedMesh;
                                Debug.Log($"[ProjectileView] Rocket 弹体网格按名重链 → {RocketBodyFbxId.Path}");
                                if (fbx != null) Destroy(fbx); // 只取网格，临时 FBX 实例即弃
                            }
                        }
                        if (mf.sharedMesh == null)
                            Debug.LogWarning("[ProjectileView] Rocket 弹体网格重链失败（Rocket9.fbx 缺失）→ 弹体不可见");
                    }
                }

                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is null || !HasBrokenMaterial(r)) continue;
                    var slot = RelinkSlotFor(kind, r.gameObject.name);
                    if (slot == null) continue; // 无对应重链素材：保持原样（日志见调用方）

                    var mat = LoadAsset(slot.Value.Mat) as Material;
                    if (mat == null) continue;

                    if (slot.Value.Tex is AssetId texId)
                    {
                        try
                        {
                            if (mat.mainTexture == null)
                            {
                                var tex = LoadAsset(texId) as Texture;
                                if (tex != null)
                                {
                                    mat.SetTexture("_MainTex", tex);
                                    Debug.Log($"[ProjectileView] 投射物贴图按名重链 → {texId.Path}");
                                }
                            }
                        }
                        catch (System.Exception) { /* 属性不存在（无 _MainTex 的 shader）：忽略 */ }
                    }
                    SetRendererMaterial(r, mat);
                    relinked++;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ProjectileView] 投射物重链异常 kind={KindName(kind)}: {ex.Message}");
                return;
            }
            if (relinked > 0)
                Debug.LogWarning($"[ProjectileView] 投射物材质/贴图断链 → 按名重链 {relinked} 个渲染体 kind={KindName(kind)}"
                                 + "（原版资源缺失回退，日志声明；未覆盖的断链渲染体保持原样）");
        }

        /// <summary>材质缺失（断链）判定：prefab 引用的材质资产在 Bundle 中不存在时渲染体材质为空。</summary>
        private static bool HasBrokenMaterial(Renderer r)
        {
            try
            {
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) return true;
                foreach (var m in mats)
                    if (m == null) return true;
                return false;
            }
            catch (System.Exception)
            {
                return true; // 无材质数组（索引越界）同样视为断链
            }
        }

        private static void SetRendererMaterial(Renderer r, Material mat)
        {
            try
            {
                var n = r.sharedMaterials.Length;
                if (n == 0) r.sharedMaterial = mat;
                else
                {
                    var mats = new Material[n];
                    for (var i = 0; i < n; i++) mats[i] = mat;
                    r.sharedMaterials = mats;
                }
            }
            catch (System.Exception)
            {
                // 个别渲染器只读场景（防御）：保持原样
            }
        }

        private static UnityEngine.Object? LoadAsset(AssetId id)
        {
            try { return WeaponMod.Context!.Resources.Load(id) as UnityEngine.Object; }
            catch (System.Exception ex) { Debug.LogWarning($"[ProjectileView] 重链资源加载异常 {id.Path}: {ex.Message}"); return null; }
        }

        /// <summary>经本 Mod context.Resources.Load 加载原投射物 prefab（Rule 3；失败返回 null）。</summary>
        private static GameObject? LoadPrefab(byte kind)
        {
            var ctx = WeaponMod.Context;
            if (ctx is null) return null;
            var id = PrefabId(kind);
            GameObject? prefab = null;
            try { prefab = ctx.Resources.Load(id) as GameObject; }
            catch (System.Exception ex) { Debug.LogWarning($"[ProjectileView] 投射物 prefab 加载异常 {id.Path}: {ex.Message}"); prefab = null; }
            if (prefab is null) Debug.LogWarning($"[ProjectileView] 原投射物 prefab 加载失败（{id.Path}）");
            return prefab;
        }

        private static AssetId PrefabId(byte kind) => kind switch
        {
            (byte)WeaponKind.RocketLauncher => RocketPrefabId,
            (byte)WeaponKind.Tornado => TornadoPrefabId,
            _ => IceBulletPrefabId,
        };

        private static string KindName(byte kind) => kind switch
        {
            (byte)WeaponKind.RocketLauncher => "Rocket",
            (byte)WeaponKind.Tornado => "Tornado",
            _ => "IceBullet",
        };
    }
}
