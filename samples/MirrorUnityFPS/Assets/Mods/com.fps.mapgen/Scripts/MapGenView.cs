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
            foreach (var (x, _, z, _, sy, _) in boxes)
            {
                var prefab = LoadPrefab(Buildings[i++ % Buildings.Length]);
                var go = prefab is not null ? Instantiate(prefab) : GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Building";
                go.transform.SetParent(transform);
                // 按盒高度缩放（与障碍盒尺寸一致，不再固定 8m）；base 贴地
                if (prefab is not null) NormalizeHeight(go, sy);
                else go.transform.localScale = new Vector3(sy, sy, sy);
                go.transform.position = new Vector3(x, 0f, z);
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

        private static void NormalizeHeight(GameObject go, float targetHeight)
        {
            var bounds = new Bounds(go.transform.position, Vector3.zero);
            var any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) { bounds.Encapsulate(r.bounds); any = true; }
            if (!any || bounds.size.y <= 0.0001f) return;
            var scale = targetHeight / bounds.size.y;
            go.transform.localScale = Vector3.Scale(go.transform.localScale, new Vector3(scale, scale, scale));
        }
    }
}
