using System.Collections.Generic;
using Game.Mod.Contract;
using UnityEngine;

namespace Com.Fps.MapGen
{
    /// <summary>
    /// 地图视图：复刻原 MirrorUnityFPS 的 Map Gen v2 程序化世界（seed=5）。
    /// 4 块 base 平台（100×20，沿 Z 间隔 20），每块随机基地类型（city/forest/future）→
    /// 各 spawn 5 栋建筑 + 5 棵树（真实预制体，自然尺寸，不缩放）。
    /// 复刻样本的 Random.InitState(5) + RandomSpawn（含重叠检查）序列。
    /// </summary>
    public sealed class MapGenView : MonoBehaviour
    {
        private const int Seed = 5;
        private const int BaseCount = 4;
        private const float BaseGap = 20f;
        private const float BaseWidth = 100f;
        private const float BaseHeight = 20f;
        private const int BuildingCount = 5;
        private const int TreeCount = 5;
        private const float OverlapRange = 12f;

        private static readonly string BasePath = "Content/World Generation/World Base/Base";

        private static readonly string[][] BuildingPaths =
        {
            new[]
            {
                "Content/World Props/Building/City Building/Bulding A",
                "Content/World Props/Building/City Building/Bulding B",
                "Content/World Props/Building/City Building/Bulding C",
            },
            new[]
            {
                "Content/World Props/Building/Forest Building/Bulding A",
                "Content/World Props/Building/Forest Building/Bulding B",
                "Content/World Props/Building/Forest Building/Bulding C",
            },
            new[]
            {
                "Content/World Props/Building/Future Building/Bulding A",
                "Content/World Props/Building/Future Building/Bulding B",
                "Content/World Props/Building/Future Building/Bulding C",
            },
        };

        private static readonly string[][] TreePaths =
        {
            new[]
            {
                "Content/World Props/Tree/City/Tree A",
                "Content/World Props/Tree/City/Tree B",
                "Content/World Props/Tree/City/Tree C",
            },
            new[]
            {
                "Content/World Props/Tree/Forest/Tree A",
                "Content/World Props/Tree/Forest/Tree B",
                "Content/World Props/Tree/Forest/Tree C",
            },
            new[]
            {
                "Content/World Props/Tree/Future/Tree A",
                "Content/World Props/Tree/Future/Tree B",
                "Content/World Props/Tree/Future/Tree C",
            },
        };

        private bool _built;

        private void Update()
        {
            if (_built) return;
            _built = true;
            Rebuild();
        }

        private void Rebuild()
        {
            Random.InitState(Seed); // 复刻样本 seed=5
            var spawned = new List<Vector3>();

            for (var b = 0; b < BaseCount; b++)
            {
                var basePos = new Vector3(0f, 0f, b * BaseGap);
                SpawnBasePlatform(basePos); // base 平台（蓝色地面，参考 (65,155,210)）

                var type = Random.Range(0, 3); // city/forest/future
                SpawnRandom(BuildingCount, BuildingPaths[type], basePos, spawned);
                SpawnRandom(TreeCount, TreePaths[type], basePos, spawned);
            }
        }

        /// <summary>base 平台：蓝色平面（100×20），样本 basePrefab 材质缺失故用简单平台替代。</summary>
        private void SpawnBasePlatform(Vector3 basePos)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Base";
            go.transform.SetParent(transform, false);
            go.transform.position = basePos + new Vector3(0f, -0.1f, 0f);
            go.transform.localScale = new Vector3(BaseWidth, 0.2f, BaseHeight);
            var mat = new Material(Shader.Find("Standard"))
                { color = new Color(65f / 255f, 155f / 255f, 210f / 255f) };
            go.GetComponent<Renderer>().sharedMaterial = mat;
        }

        /// <summary>复刻样本 RandomSpawn：posX/posZ → 重叠检查 → prefabIndex。</summary>
        private void SpawnRandom(int count, string[] paths, Vector3 basePos, List<Vector3> spawned)
        {
            for (var i = 0; i < count; i++)
            {
                var posX = BaseWidth / 2f * Random.Range(-1f, 1f);
                var posZ = BaseHeight / 2f * Random.Range(-1f, 1f);
                var pos = basePos + new Vector3(posX, 0f, posZ);

                // 重叠检查（样本用 OverlapSphere 12m；简化距离判定）
                var overlap = false;
                foreach (var s in spawned)
                    if (Vector3.Distance(s, pos) < OverlapRange) { overlap = true; break; }
                if (overlap) continue;

                var index = Random.Range(0, paths.Length);
                Spawn(paths[index], pos, Quaternion.identity, spawned);
            }
        }

        private void Spawn(string path, Vector3 pos, Quaternion rot, List<Vector3> spawned)
        {
            try
            {
                var prefab = MapGenMod.Context?.Resources.Load(new AssetId(MapGenMod.ModIdValue, path)) as GameObject;
                if (prefab is null) { Debug.LogWarning($"[MapGenView] 缺失预制体: {path}"); return; }
                var go = Instantiate(prefab, pos, rot, transform);
                go.name = prefab.name;
                spawned.Add(pos);
            }
            catch (System.Exception) { }
        }
    }
}
