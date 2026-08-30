using Game.Mod.Contract;
using UnityEngine;

namespace Com.Fps.MapGen
{
    /// <summary>地图视图（忠实资源版）：按客户端 seed 确定性布置建筑 Prefab（经所属 Mod ResourceScope）。</summary>
    public sealed class MapGenView : MonoBehaviour
    {
        private static readonly AssetId[] Buildings =
        {
            new(MapGenMod.ModIdValue, "Content/World Props/Building/City Building/Bulding A"),
            new(MapGenMod.ModIdValue, "Content/World Props/Building/City Building/Bulding B"),
            new(MapGenMod.ModIdValue, "Content/World Props/Building/City Building/Bulding C"),
        };

        private bool _built;
        private int _builtSeed;

        private void Update()
        {
            if (_built && _builtSeed == MapGenMod.ClientSeed) return;
            Rebuild();
        }

        private void Rebuild()
        {
            foreach (var child in GetComponentsInChildren<Transform>())
                if (child != transform) Destroy(child.gameObject);

            var boxes = MapGenMod.GenerateBoxes(MapGenMod.ClientSeed);
            var i = 0;
            foreach (var (x, y, z, _, _, _) in boxes)
            {
                var prefab = LoadPrefab(Buildings[i++ % Buildings.Length]);
                var go = prefab is not null ? Instantiate(prefab) : GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Building";
                go.transform.SetParent(transform);
                go.transform.position = new Vector3(x, y, z);
                if (prefab is null) go.GetComponent<Renderer>().material.color = new Color(0.55f, 0.45f, 0.35f);
            }

            _built = true;
            _builtSeed = MapGenMod.ClientSeed;
        }

        private static GameObject? LoadPrefab(AssetId id)
        {
            try { return MapGenMod.Context?.Resources.Load(id) as GameObject; }
            catch (System.Exception) { return null; }
        }
    }
}
