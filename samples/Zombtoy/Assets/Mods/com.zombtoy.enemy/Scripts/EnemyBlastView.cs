using Game.Mod.Contract;
using UnityEngine;

namespace Com.Zombtoy.Enemy
{
    /// <summary>
    /// ZomDuck 死亡自爆爆炸视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8；复刻宪法 §0）。
    /// 宿主 ModRuntime 自动实例化本视图（InstantiateViews）→ OnEnable 订阅 EnemyMod.OnEnemyBlast
    /// （Rule 20 对称撤销）：
    /// - 收到爆炸事件（逻辑在 EnemyMod.DetonateZomDuck 致死分支已同步结算：玩家/周围敌人 AOE 伤害，契约 §3 序 7）
    ///   后在爆点实例化**原 CFX 爆炸 prefab**（CFX_Explosion_B_Smoke+Text.prefab——与 ZomDuckk.prefab 内嵌
    ///   explosionParticles / EnemyRocket Variant 引爆特效同款，AssetId 带扩展名精确命中，Rule 3），播放自带粒子，
    ///   定时自毁；不建自建 ParticleSystem 近似物（复刻宪法：原版资源）。
    /// - 材质断链（bundle 迁移 guid 失效）按名重链（EnemyProjectileView.RelinkBrokenProjectileMaterials，
    ///   日志声明，原版资源缺失回退）；prefab 确实缺失 → 无爆炸视觉（逻辑伤害不受影响，仅记日志）。
    /// </summary>
    public sealed class EnemyBlastView : MonoBehaviour
    {
        // 原 CFX 爆炸 prefab AssetId（Rule 3：路径 = Mod 目录内相对路径，带扩展名 .prefab）
        public static readonly AssetId ExplosionPrefabId = new(EnemyMod.ModIdValue,
            "Imported/JMO Assets/Cartoon FX/CFX Prefabs/Explosions/CFX_Explosion_B_Smoke+Text.prefab");

        /// <summary>爆炸对象存活时长（播放完成后销毁；原版 Destroy(gameObject, 2f) 语义近似，粒子短于此时长）。</summary>
        private const float DestroyAfter = 3f;

        private void OnEnable() => EnemyMod.OnEnemyBlast += HandleBlast;
        private void OnDisable() => EnemyMod.OnEnemyBlast -= HandleBlast;

        private void HandleBlast(uint enemyId, float x, float y, float z)
        {
            var prefab = LoadPrefab(ExplosionPrefabId);
            if (prefab is null)
            {
                Debug.LogError($"[EnemyBlastView] 原爆炸 prefab 缺失（{ExplosionPrefabId}）→ 无爆炸视觉（原版资源缺失回退，逻辑伤害已结算）");
                return;
            }

            var go = Instantiate(prefab);
            go.name = $"EnemyBlast_{enemyId}";
            go.transform.position = new Vector3(x, y, z);
            EnemyProjectileView.RelinkBrokenProjectileMaterials(go, (byte)EnemyProjectileKind.Rocket); // 断链重链（同火箭引爆特效资产）
            KickParticles(go);
            Debug.Log($"[EnemyBlastView] 死亡自爆爆炸 → {ExplosionPrefabId} @ ({x:F1},{y:F1},{z:F1})（原 CFX 特效：粒子/Text 自带）");
            Destroy(go, DestroyAfter);
        }

        /// <summary>播放爆炸粒子（prefab 自带特效；悬空原脚本忽略，不驱动）。</summary>
        private static void KickParticles(GameObject root)
        {
            try
            {
                foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps != null) ps.Play();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[EnemyBlastView] 爆炸粒子触发异常: {ex.Message}");
            }
        }

        /// <summary>经本 Mod context.Resources.Load 加载原爆炸 prefab（Rule 3；失败返回 null）。</summary>
        private static GameObject? LoadPrefab(AssetId id)
        {
            var ctx = EnemyMod.Context;
            if (ctx is null) return null;
            GameObject? prefab = null;
            try { prefab = ctx.Resources.Load(id) as GameObject; }
            catch (System.Exception ex) { Debug.LogWarning($"[EnemyBlastView] 爆炸 prefab 加载异常 {id}: {ex.Message}"); prefab = null; }
            if (prefab is null) Debug.LogWarning($"[EnemyBlastView] 原爆炸 prefab 加载失败（{id.Path}）");
            return prefab;
        }
    }
}
