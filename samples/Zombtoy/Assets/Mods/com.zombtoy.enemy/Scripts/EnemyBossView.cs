using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;
using UnityEngine;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// 首领 Titan 地面锁定视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8；复刻宪法 §0）。
    /// 宿主 ModRuntime 自动实例化（InstantiateViews）→ 订阅 EnemyMod.OnBossStrike（落点 AOE 表现）+ 轮询
    /// EnemyBossLock 组件。职责（契约 §3 序 5）：
    /// - **独立 decal（准星）**：把 Titan prefab 自带的 GroundTarget 资产脱离原实例（不复刻原版 groundTarget
    ///   绑地板/Floor-collider 的 bug），作为世界独立准星对象，按 EnemyBossLock 显隐/落点每帧同步；
    /// - 蓄力完成（EnemyBossSystem 已结算 AOE 伤害）→ 在落点表现：从 Titan ShootPoint 实例化**原
    ///   EnemyRocket Variant.prefab**（AssetId 带扩展名，Rule 3）直线飞向落点（直线飞行、无追踪），到点引爆
    ///   自带 CFX 爆炸粒子（prefab 自带特效；悬空原脚本忽略），随后自毁。
    /// 准星/火箭均无逻辑实体（AOE 伤害由 EnemyBossSystem 逻辑即时结算，火箭是落点视觉）。
    /// </summary>
    public sealed class EnemyBossView : MonoBehaviour
    {
        private sealed class BossVisual
        {
            public EnemyView Enemy = null!;      // Titan 实例视图（锚点）
            public GameObject? Decal;            // 独立准星 decal（GroundTarget 资产脱离）
            public bool DecalDetached;           // 解离只做一次
        }

        private sealed class RocketFx
        {
            public GameObject Go = null!;
            public Vector3 From;
            public Vector3 Dir;
            public float Remaining;              // 剩余飞行距离
            public bool Exploding;               // 已引爆（等待销毁）
            public float ExplodeTimer;
        }

        private readonly Dictionary<uint, BossVisual> _bosses = new();
        private readonly List<RocketFx> _rockets = new();
        private int _seq;

        private void OnEnable()
        {
            EnemyMod.OnBossStrike += HandleBossStrike;
        }

        private void OnDisable()
        {
            EnemyMod.OnBossStrike -= HandleBossStrike;
            foreach (var kv in _bosses)
                if (kv.Value.Decal != null) Destroy(kv.Value.Decal);
            foreach (var r in _rockets)
                if (r.Go != null) Destroy(r.Go);
            _bosses.Clear();
            _rockets.Clear();
        }

        private void Update()
        {
            var world = EnemyMod.World;
            if (world is null) return;

            SyncBosses(world);
            AdvanceRockets(world);
        }

        // ---- Boss 准星同步（轮询 EnemyBossLock，契约 §3 序 5 状态 → decal） ----

        private void SyncBosses(World world)
        {
            foreach (var (id, lockState) in world.Store<EnemyBossLock>().All())
            {
                var e = new Entity(id);
                if (!world.Exists(e))
                {
                    RemoveBoss(id);
                    continue;
                }
                var dead = world.TryGet<EnemyFlags>(e, out var flags) && flags.IsDead;
                if (dead)
                {
                    // Titan 死亡：准星随 Boss 停用（下沉尸体表现由 EnemyView 负责）；实体销毁后整体清理
                    if (_bosses.TryGetValue(id, out var dying) && dying.Decal != null)
                        dying.Decal.SetActive(false);
                    continue;
                }

                if (!_bosses.TryGetValue(id, out var vis))
                {
                    if (!EnemyView.TryGet(id, out var enemyView)) continue; // 视图未生成/无头环境：等下帧
                    vis = new BossVisual { Enemy = enemyView };
                    _bosses[id] = vis;
                }
                if (!vis.DecalDetached) DetachDecal(vis);

                var decal = vis.Decal;
                if (decal != null)
                {
                    decal.SetActive(lockState.Tracking); // 准星只在射程内锁定（原版 SetActive(targetActive)）
                    if (lockState.Tracking)
                    {
                        // 落点 y 贴地（原版准星贴地横躺；本工程平台顶面 y≈0）
                        var p = decal.transform.position;
                        p.x = lockState.X;
                        p.z = lockState.Z;
                        p.y = 0.02f;
                        decal.transform.position = p;
                    }
                }
            }
        }

        /// <summary>把 Titan 实例内原 GroundTarget 准星资产解离为独立对象（世界根，不随 Titan 移动、
        /// 不绑地板；SetParent(null,true) 保持世界变换 = 原版视觉大小与横躺姿态）。</summary>
        private static void DetachDecal(BossVisual vis)
        {
            vis.DecalDetached = true;
            try
            {
                var t = vis.Enemy.transform.Find("GroundTarget");
                if (t == null)
                {
                    // 原 Titan prefab 缺失 GroundTarget（bundle 未装/资产改名）：准星不可见，仅记日志
                    Debug.LogWarning("[EnemyBossView] Titan 实例无 GroundTarget 准星子对象 → decal 缺失（原版资源缺失回退）");
                    return;
                }
                var decal = t.gameObject;
                decal.transform.SetParent(null, true); // 独立 decal（复刻宪法：不复刻原版绑地板 bug）
                vis.Decal = decal;
                decal.SetActive(false); // 默认隐藏，锁定中才显示
                // decal 材质断链同样按名重链（防御：原引用 BossGroundTargetMaterial，链接完好则不动）
                EnemyProjectileView.RelinkBrokenProjectileMaterials(decal, (byte)EnemyProjectileKind.Rocket);
                Debug.Log("[EnemyBossView] Titan 准星已解离为独立 decal（GroundTarget 资产，世界根）");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[EnemyBossView] decal 解离异常: {ex.Message}");
            }
        }

        private void RemoveBoss(uint id)
        {
            if (!_bosses.TryGetValue(id, out var vis)) return;
            _bosses.Remove(id);
            if (vis.Decal != null) Destroy(vis.Decal);
        }

        // ---- 落点火箭（EnemyBossSystem 蓄力完成 → OnBossStrike 表现；直线飞行，到点引爆自带特效） ----

        private void HandleBossStrike(uint enemyId, float lockX, float lockZ)
        {
            var world = EnemyMod.World;
            if (world is null || !world.Exists(new Entity(enemyId))) return;

            if (!_bosses.TryGetValue(enemyId, out var vis))
            {
                if (!EnemyView.TryGet(enemyId, out var enemyView)) return; // 视图未生成（无头/瞬杀）：无表现
                vis = new BossVisual { Enemy = enemyView };
                _bosses[enemyId] = vis;
                DetachDecal(vis);
            }

            var prefab = LoadPrefab(EnemyProjectileView.RocketPrefabId);
            if (prefab is null)
            {
                Debug.LogError($"[EnemyBossView] 原火箭 prefab 缺失（{EnemyProjectileView.RocketPrefabId}）→ 无落点火箭（原版资源缺失回退）");
                return;
            }

            // 出膛点：Titan ShootPoint 子对象（原版 shootPoint），缺失时退到 Titan 头部高度近似
            var launch = vis.Enemy.transform.position;
            var sp = vis.Enemy.transform.Find("ShootPoint");
            if (sp != null) launch = sp.position;
            else launch += Vector3.up * 4f;

            var target = new Vector3(lockX, 0.05f, lockZ);
            var dir = target - launch;
            var dist = dir.magnitude;
            if (dist < 0.01f) dist = 0.01f;
            dir /= dist;

            var go = Instantiate(prefab);
            go.name = $"EnemyRocket_{enemyId}_{++_seq}";
            go.transform.position = launch;
            go.transform.rotation = Quaternion.LookRotation(dir); // 直线飞行（原版无追踪）
            EnemyProjectileView.RelinkBrokenProjectileMaterials(go, (byte)EnemyProjectileKind.Rocket);
            Debug.Log($"[EnemyBossView] 落点火箭实例化 → {EnemyProjectileView.RocketPrefabId.Path}（自 {launch} 飞向 ({lockX:F1},{lockZ:F1})）");
            _rockets.Add(new RocketFx { Go = go, From = launch, Dir = dir, Remaining = dist });
        }

        private void AdvanceRockets(World world)
        {
            if (_rockets.Count == 0) return;
            var step = EnemyConfig.RocketVisualSpeed * Time.deltaTime;
            var stale = new List<RocketFx>();
            foreach (var fx in _rockets)
            {
                if (fx.Exploding)
                {
                    fx.ExplodeTimer -= Time.deltaTime;
                    if (fx.ExplodeTimer <= 0f) { stale.Add(fx); continue; }
                }
                else
                {
                    fx.Remaining -= step;
                    if (fx.Remaining <= 0f)
                    {
                        Explode(fx);
                        continue;
                    }
                    fx.Go.transform.position += fx.Dir * step;
                }
            }
            foreach (var fx in stale)
            {
                _rockets.Remove(fx);
                if (fx.Go != null) Destroy(fx.Go);
            }
        }

        /// <summary>到点引爆：原 Rocket 爆炸特效子对象（CFX_Explosion_B_Smoke+Text）脱离火箭播一次后自毁
        /// （对齐原版 EnemyProjectile：爆炸 PS 脱离父级 Play + 定时销毁），火箭本体立即销毁。</summary>
        private static void Explode(RocketFx fx)
        {
            fx.Exploding = true;
            fx.ExplodeTimer = 2.5f;
            try
            {
                var boom = FindChild(fx.Go.transform, "CFX_Explosion_B_Smoke+Text") ?? FindChild(fx.Go.transform, "Explosion");
                if (boom != null)
                {
                    boom.SetParent(null, true);
                    foreach (var ps in boom.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        if (ps != null) ps.Play();
                    }
                    Destroy(boom.gameObject, 3f);
                    fx.Go.SetActive(false); // 火箭本体消失（原版 mesh.enabled=false + 0.1s 销毁）
                }
                else
                {
                    // 无爆炸子对象（bundle 缺资产兜底）：播放自带粒子后整机自毁
                    foreach (var ps in fx.Go.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        if (ps != null && !ps.isPlaying) ps.Play();
                    }
                    fx.ExplodeTimer = 1.5f;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[EnemyBossView] 火箭引爆异常: {ex.Message}");
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

        private static GameObject? LoadPrefab(AssetId id)
        {
            var ctx = EnemyMod.Context;
            if (ctx is null) return null;
            GameObject? prefab = null;
            try { prefab = ctx.Resources.Load(id) as GameObject; }
            catch (System.Exception ex) { Debug.LogWarning($"[EnemyBossView] 火箭 prefab 加载异常 {id}: {ex.Message}"); prefab = null; }
            if (prefab is null) Debug.LogWarning($"[EnemyBossView] 原火箭 prefab 加载失败（{id.Path}）");
            return prefab;
        }
    }
}
