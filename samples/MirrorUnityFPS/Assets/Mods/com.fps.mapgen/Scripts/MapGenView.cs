using UnityEngine;

namespace Com.Fps.MapGen
{
    /// <summary>地图视图（代码原语）：按客户端 seed 确定性生成障碍物立方体。</summary>
    public sealed class MapGenView : MonoBehaviour
    {
        private bool _built;
        private int _builtSeed;

        private void Update()
        {
            if (_built && _builtSeed == MapGenMod.ClientSeed) return;
            Rebuild();
        }

        private void Rebuild()
        {
            // 清除旧障碍物
            foreach (var child in GetComponentsInChildren<Transform>())
            {
                if (child != transform) Destroy(child.gameObject);
            }

            var boxes = MapGenMod.GenerateBoxes(MapGenMod.ClientSeed);
            foreach (var (x, y, z, sx, sy, sz) in boxes)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "Obstacle";
                box.transform.SetParent(transform);
                box.transform.position = new Vector3(x, y, z);
                box.transform.localScale = new Vector3(sx, sy, sz);
                box.GetComponent<Renderer>().material.color = new Color(0.55f, 0.45f, 0.35f);
            }

            _built = true;
            _builtSeed = MapGenMod.ClientSeed;
        }
    }
}
