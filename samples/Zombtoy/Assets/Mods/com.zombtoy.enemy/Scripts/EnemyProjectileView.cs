using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;
using UnityEngine;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 敌人投射物视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8；复刻宪法 §0）。
    /// 宿主 ModRuntime 自动实例化本视图（InstantiateViews）→ OnEnable 订阅 EnemyMod.OnProjectileSpawned/
    /// OnProjectileDestroyed（Rule 20 对称撤销）：
    /// - 按 Kind 经 context.Resources.Load 加载**原投射物 prefab**（Rule 3，AssetId 带扩展名 .prefab 精确命中）：
    ///   Fireball → EnemyProjectile.prefab（Clown 火球）；Rocket → EnemyRocket Variant.prefab（Titan 落点火箭，
    ///   由 EnemyBossView 在 Boss 开火时实例化，见 OnBossStrike——本视图不处理 Rocket 事件）。
    /// - Instantiate 后每帧按逻辑 EnemyProjectile 实体同步位置/朝向（逻辑权威，契约 §2 语义），
    ///   实体命中/超时/销毁 → 视图销毁；出膛时触发 prefab 自带粒子（对齐原版 EnemyProjectile.Start 的
    ///   GetComponentInChildren&lt;ParticleSystem&gt;().Play()，悬空原脚本忽略）。
    /// - prefab 材质/贴图断链（迁移后 guid 失效）→ 按名重链（扩展名精确匹配 + SetTexture，日志声明，原版资源缺失回退）。
    /// </summary>
    public sealed class EnemyProjectileView : MonoBehaviour
    {
        // ---- 原投射物 prefab AssetId（Rule 3 / §8.2：路径 = Mod 目录内相对路径，带扩展名 .prefab） ----
        public static readonly AssetId FireballPrefabId = new(EnemyMod.ModIdValue, "EnemyProjectile.prefab");
        public static readonly AssetId RocketPrefabId = new(EnemyMod.ModIdValue, "EnemyRocket Variant.prefab");

        // 断链重链资产（原版 prefab 引用的材质/贴图 guid 迁移后失效；素材在本 Mod Bundle 内，按名取）
        private static readonly AssetId TrailMaterialId = new(EnemyMod.ModIdValue, "New Material.mat"); // EnemyProjectile 拖尾（原版同名引用）
        private static readonly AssetId FireMaterialId = new(EnemyMod.ModIdValue,
            "Flames Of The Phoenix/Materials/Fire_Material_ShortAnimation.mat");
        private static readonly AssetId FireTextureId = new(EnemyMod.ModIdValue,
            "Flames Of The Phoenix/Textures/Fire_Texture_ShortAnimation.png");
        private static readonly AssetId SmokeMaterialId = new(EnemyMod.ModIdValue,
            "Flames Of The Phoenix/Materials/Smoke_Material_ShortAnimation.mat");
        private static readonly AssetId SmokeTextureId = new(EnemyMod.ModIdValue,
            "Flames Of The Phoenix/Textures/Smoke_Texture_ShortAnimation.png");

        private sealed class ProjectileVisual
        {
            public GameObject Go = null!;
            public bool ParticlesKicked; // 出膛粒子只触发一次（原版 Start 语义）
        }

        private readonly Dictionary<uint, ProjectileVisual> _instances = new();

        private void OnEnable()
        {
            EnemyMod.OnProjectileSpawned += HandleSpawned;
            EnemyMod.OnProjectileDestroyed += HandleDestroyed;
        }

        private void OnDisable()
        {
            EnemyMod.OnProjectileSpawned -= HandleSpawned;
            EnemyMod.OnProjectileDestroyed -= HandleDestroyed;
            DestroyAllInstances();
        }

        private void Update()
        {
            var world = EnemyMod.World;
            if (world is null) return;

            // 同步/清理：实体销毁（命中/超时/reset 清场/卸载）→ 视图销毁
            if (_instances.Count == 0) return;
            var stale = new List<uint>();
            foreach (var kv in _instances)
            {
                var e = new Entity(kv.Key);
                if (!world.Exists(e) || !world.TryGet<EnemyProjectile>(e, out var p))
                {
                    stale.Add(kv.Key);
                    continue;
                }
                var vis = kv.Value;
                var go = vis.Go;
                if (go == null) { stale.Add(kv.Key); continue; }

                // 逻辑位置/朝向 → 视图（直线飞行，前向 = 飞行方向；原版 prefab 前进轴 +Z）
                go.transform.position = new Vector3(p.X, p.Y, p.Z);
                var fwd = new Vector3(p.DirX, p.DirY, p.DirZ);
                if (fwd.sqrMagnitude > 0.0001f) go.transform.rotation = Quaternion.LookRotation(fwd);

                if (!vis.ParticlesKicked)
                {
                    vis.ParticlesKicked = true;
                    KickTrailParticles(go); // 出膛粒子（原版 EnemyProjectile.Start）
                }
            }
            foreach (var id in stale) DestroyInstance(id);
        }

        private void HandleSpawned(uint entityId, byte kind)
        {
            if (kind != (byte)EnemyProjectileKind.Fireball) return; // Rocket 由 EnemyBossView 经 OnBossStrike 表现
            if (_instances.ContainsKey(entityId)) return;

            var prefab = LoadPrefab(FireballPrefabId);
            if (prefab is null)
            {
                // 原 prefab 缺失（bundle 未装）：逻辑实体仍按无视觉飞行（超时/命中照常），仅记日志
                Debug.LogError($"[EnemyProjectileView] 原投射物 prefab 缺失（{FireballPrefabId}）→ 无视觉（原版资源缺失回退）");
                return;
            }

            var go = Instantiate(prefab);
            go.name = $"EnemyProjectile_{entityId}";
            RelinkBrokenProjectileMaterials(go, kind);
            Debug.Log($"[EnemyProjectileView] 火球投射物实例化 → {FireballPrefabId}（原 prefab：粒子/拖尾/灯光自带）");
            _instances[entityId] = new ProjectileVisual { Go = go };
        }

        private void HandleDestroyed(uint entityId) => DestroyInstance(entityId);

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

        /// <summary>出膛粒子：对齐原版 EnemyProjectile.Start（GetComponentInChildren&lt;ParticleSystem&gt;().Play()），
        /// 悬空原脚本组件忽略、不驱动。</summary>
        private static void KickTrailParticles(GameObject go)
        {
            try
            {
                var ps = go.GetComponentInChildren<ParticleSystem>(true);
                if (ps != null && !ps.isPlaying) ps.Play();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[EnemyProjectileView] 出膛粒子触发异常: {ex.Message}");
            }
        }

        /// <summary>经本 Mod context.Resources.Load 加载原投射物 prefab（Rule 3；失败返回 null）。</summary>
        private static GameObject? LoadPrefab(AssetId id)
        {
            var ctx = EnemyMod.Context;
            if (ctx is null) return null;
            GameObject? prefab = null;
            try { prefab = ctx.Resources.Load(id) as GameObject; }
            catch (System.Exception ex) { Debug.LogWarning($"[EnemyProjectileView] 投射物 prefab 加载异常 {id}: {ex.Message}"); prefab = null; }
            if (prefab is null) Debug.LogWarning($"[EnemyProjectileView] 原投射物 prefab 加载失败（{id.Path}）");
            return prefab;
        }

        /// <summary>
        /// 投射物材质/贴图断链重链（复刻宪法允许的原版资源缺失回退，日志声明）：prefab 内嵌材质引用 guid
        /// 迁移后失效（渲染体无材质/材质无贴图）→ 经 context.Resources.Load 按对象名取本 Mod 自带材质整条补链
        /// （AssetId 带扩展名精确命中，Rule 3；贴图缺失 SetTexture，属性名 _MainTex）。
        /// 对象名含 Fire/Smoke/Ball 的才是投射物效果渲染体，不误伤灯光/碰撞等非渲染组件。EnemyBossView（火箭）复用。
        /// </summary>
        public static void RelinkBrokenProjectileMaterials(GameObject root, byte kind)
        {
            if (EnemyMod.Context is null) return;
            var relinked = 0;
            try
            {
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is null || !HasBrokenMaterial(r)) continue;

                    var lower = r.gameObject.name.ToLowerInvariant();
                    // 拖尾（EnemyProjectile 根，TrailRenderer）：原引用根目录 New Material.mat
                    Material? mat = null;
                    Texture? tex = null;
                    if (r is TrailRenderer || lower.Contains("projectile"))
                    {
                        mat = LoadAsset(TrailMaterialId) as Material;
                    }
                    else if (lower.Contains("smoke"))
                    {
                        mat = LoadAsset(SmokeMaterialId) as Material;
                        tex = LoadAsset(SmokeTextureId) as Texture;
                    }
                    else if (lower.Contains("fire") || lower.Contains("ball"))
                    {
                        mat = LoadAsset(FireMaterialId) as Material;
                        tex = LoadAsset(FireTextureId) as Texture;
                    }
                    if (mat is null) continue; // 无对应重链资产：保持原样（日志见调用方）

                    SetRendererMaterial(r, mat);
                    if (tex != null && mat.mainTexture is null)
                    {
                        try { mat.SetTexture("_MainTex", tex); }
                        catch (System.Exception) { /* 属性不存在（Unlit/Color 等）：忽略 */ }
                    }
                    relinked++;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[EnemyProjectileView] 材质重链异常 kind={kind}: {ex.Message}");
                return;
            }
            if (relinked > 0)
                Debug.LogWarning($"[EnemyProjectileView] 投射物材质/贴图断链 → 按名重链 {relinked} 个渲染体 kind={kind}"
                                 + "（原版资源缺失回退，日志声明；未覆盖的断链渲染体保持默认色）");
        }

        /// <summary>材质缺失/全空（断链）判定：prefab 引用的材质资产在 Bundle 中不存在时渲染体材质为空。</summary>
        private static bool HasBrokenMaterial(Renderer r)
        {
            try
            {
                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) return true;
                foreach (var m in mats)
                    if (m is null) return true;
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
            try { return EnemyMod.Context!.Resources.Load(id) as UnityEngine.Object; }
            catch (System.Exception ex) { Debug.LogWarning($"[EnemyProjectileView] 重链资源加载异常 {id}: {ex.Message}"); return null; }
        }
    }
}
