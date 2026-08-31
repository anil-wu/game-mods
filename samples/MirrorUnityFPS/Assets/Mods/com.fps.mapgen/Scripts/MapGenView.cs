using System.Collections.Generic;
using UnityEngine;

namespace Com.Fps.MapGen
{
    /// <summary>
    /// 地图视图（blockout 关卡版）：按参考验收图 1.webp 复刻的 Unity 网格块体风格场景。
    /// 蓝色地面 + 灰色 L 形主体建筑（前墙门洞+顶部缺口、后翼墙、外挂大台阶、白色斜坡板）+ 散落棕箱掩体。
    /// 带程序化网格线材质；固定布局（确定性）。
    /// </summary>
    public sealed class MapGenView : MonoBehaviour
    {
        // 参考图 RGB（/255）
        private static readonly Color FloorBlue = new(65f / 255f, 155f / 255f, 210f / 255f);
        private static readonly Color WallGray = new(150f / 255f, 155f / 255f, 165f / 255f);
        private static readonly Color WallGrayDark = new(108f / 255f, 112f / 255f, 120f / 255f);
        private static readonly Color BoxBrown = new(130f / 255f, 95f / 255f, 45f / 255f);
        private static readonly Color RampWhite = new(230f / 255f, 232f / 255f, 238f / 255f);

        private readonly Dictionary<Color, Material> _matCache = new();

        private void Update()
        {
            if (_built) return;
            Rebuild();
        }

        private bool _built;

        private void Rebuild()
        {
            _built = true;

            // 1) 蓝色地面
            Cube("Floor", new Vector3(0f, -0.1f, 0f), new Vector3(40f, 0.2f, 40f), FloorBlue);

            // 2) 灰色 L 形主体建筑
            BuildFrontWall();
            BuildBackWing();
            BuildStairs();
            Cube("Ramp", new Vector3(5.2f, 0.06f, 11.5f), new Vector3(2.6f, 0.12f, 2.6f), RampWhite); // 台阶脚白色斜坡板（放大）

            // 3) 棕箱掩体
            BuildBoxes();
        }

        private void BuildFrontWall()
        {
            // 前墙：10 宽 × 9 高 × 0.5 厚，中心 x=0, z=10；门洞 3×3（左下，通透 + 深色门框）；顶部中央缺口 2×1.5
            const float z = 10f;
            Cube("FrontL", new Vector3(-3.25f, 1.5f, z), new Vector3(3.5f, 3f, 0.5f), WallGray);
            Cube("FrontR", new Vector3(3.25f, 1.5f, z), new Vector3(3.5f, 3f, 0.5f), WallGray);
            Cube("TopL", new Vector3(-3f, 6f, z), new Vector3(4f, 6f, 0.5f), WallGray);
            Cube("TopR", new Vector3(3f, 6f, z), new Vector3(4f, 6f, 0.5f), WallGray);
            Cube("Lintel", new Vector3(0f, 5.25f, z), new Vector3(2f, 4.5f, 0.5f), WallGray);
            // 门洞深色门框（上沿 + 两侧竖条），门洞本身 3×3 间隙即通透过道
            Cube("DoorTop", new Vector3(0f, 3.15f, z - 0.1f), new Vector3(3.2f, 0.25f, 0.7f), WallGrayDark);
            Cube("DoorL", new Vector3(-1.62f, 1.5f, z - 0.1f), new Vector3(0.25f, 3f, 0.7f), WallGrayDark);
            Cube("DoorR", new Vector3(1.62f, 1.5f, z - 0.1f), new Vector3(0.25f, 3f, 0.7f), WallGrayDark);
        }

        private void BuildBackWing()
        {
            // 后翼墙：与前墙垂直成 L（沿 z 向后延伸，连接在前墙右端），更高（10 格，全场最高）
            Cube("BackWing", new Vector3(4.8f, 5f, 15f), new Vector3(0.8f, 10f, 10f), WallGray);
        }

        private void BuildStairs()
        {
            // 外挂大台阶：10 级，沿后翼墙侧面（x=5.2 边）从地面爬升至墙顶（y=10），每级踏步 0.9 宽×1 高，交替深灰做阴影缝
            const int steps = 10;
            for (var i = 0; i < steps; i++)
            {
                var h = i + 1;
                Cube("Step" + i, new Vector3(5.2f, h - 0.5f, 11f + i * 0.9f), new Vector3(0.9f, 1f, 0.9f), i % 2 == 0 ? WallGray : WallGrayDark);
            }
        }

        private void BuildBoxes()
        {
            // 左远角 1 小箱（最远、最小，画面左边缘）
            Cube("BoxFar", new Vector3(-13f, 0.7f, 9f), new Vector3(1.4f, 1.4f, 1.4f), BoxBrown);
            // 左中近景：横向掩体矮墙（高矮不一，非斜向堆叠）
            Cube("BoxL1", new Vector3(-6.5f, 1.4f, 2.5f), new Vector3(2.8f, 2.8f, 1.2f), BoxBrown);
            Cube("BoxL2", new Vector3(-3.6f, 0.8f, 2.5f), new Vector3(1.6f, 1.6f, 1.6f), BoxBrown);
            Cube("BoxL3", new Vector3(-6.5f, 0.8f, 4.6f), new Vector3(1.6f, 1.6f, 1.6f), BoxBrown);
            // 右侧前景 1 箱（台阶脚附近）
            Cube("BoxR", new Vector3(10f, 1f, 6f), new Vector3(2f, 2f, 2f), BoxBrown);
            // 建筑顶部 1 箱（后翼墙顶）
            Cube("BoxTop", new Vector3(4.8f, 10.8f, 13f), new Vector3(1.6f, 1.6f, 1.6f), BoxBrown);
        }

        private void Cube(string name, Vector3 pos, Vector3 size, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = GridMaterial(color);
        }

        /// <summary>程序化网格块体材质：Standard（受光/背光有体积）+ 白色网格纹理 + 颜色染色。</summary>
        private Material GridMaterial(Color baseColor)
        {
            if (_matCache.TryGetValue(baseColor, out var cached)) return cached;
            const int size = 32, cell = 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var line = (x % cell == 0) || (y % cell == 0);
                    tex.SetPixel(x, y, line ? new Color(0.5f, 0.5f, 0.5f, 1f) : Color.white);
                }
            tex.Apply();
            var mat = new Material(Shader.Find("Standard")) { color = baseColor, mainTexture = tex };
            _matCache[baseColor] = mat;
            return mat;
        }
    }
}
