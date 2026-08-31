using UnityEngine;

namespace Com.Fps.MapGen
{
    /// <summary>
    /// 地图视图：复刻参考验收图 1.webp 的体素风建筑场景（第七次 vision 核验的精确颜色/形态）。
    /// 单块亮蓝瓷砖平台 + 蓝灰双塔建筑（左塔城垛+拱门、右楼金色烟囱、大台阶）+ 4 个橄榄棕/金板条箱。
    /// 无树；深蓝渐变天空由宿主相机/天空面片提供。
    /// </summary>
    public sealed class MapGenView : MonoBehaviour
    {
        // 参考图精确 RGB（/255，vision 逐像素取样）
        private static readonly Color WallLit = new(120f / 255f, 150f / 255f, 180f / 255f);      // 建筑亮面蓝灰
        private static readonly Color WallShadow = new(82f / 255f, 102f / 255f, 123f / 255f);    // 建筑阴影面
        private static readonly Color Stairs = new(179f / 255f, 200f / 255f, 211f / 255f);       // 台阶浅灰
        private static readonly Color DoorDark = new(81f / 255f, 99f / 255f, 118f / 255f);       // 门洞阴影
        private static readonly Color CrateOlive = new(78f / 255f, 66f / 255f, 38f / 255f);      // 板条箱橄榄棕
        private static readonly Color CrateGold = new(172f / 255f, 138f / 255f, 71f / 255f);     // 板条箱金色
        private static readonly Color Platform = new(83f / 255f, 158f / 255f, 203f / 255f);      // 亮蓝平台

        private bool _built;

        private void Update()
        {
            if (_built) return;
            _built = true;
            Rebuild();
        }

        private void Rebuild()
        {
            // 1) 单块亮蓝瓷砖平台
            Cube("Platform", new Vector3(0f, -0.1f, 0f), new Vector3(30f, 0.2f, 30f), Platform);

            // 2) 蓝灰双塔建筑
            BuildLeftTower();
            BuildRightBuilding();
            BuildStairs();

            // 3) 4 个板条箱
            BuildCrates();
        }

        private void BuildLeftTower()
        {
            // 左塔（较高）：主体 8×12×8，顶部城垛凹口，正面拱形门洞
            Cube("LeftTower", new Vector3(-4f, 6f, 0f), new Vector3(8f, 12f, 8f), WallLit);
            // 城垛：顶部四角小齿
            Cube("Cren1", new Vector3(-7f, 12.6f, -3f), new Vector3(2f, 1.2f, 2f), WallLit);
            Cube("Cren2", new Vector3(-1f, 12.6f, -3f), new Vector3(2f, 1.2f, 2f), WallLit);
            Cube("Cren3", new Vector3(-7f, 12.6f, 3f), new Vector3(2f, 1.2f, 2f), WallLit);
            Cube("Cren4", new Vector3(-1f, 12.6f, 3f), new Vector3(2f, 1.2f, 2f), WallLit);
            // 拱形门洞（深色开口，正面 z=-4）
            Cube("ArchDoor", new Vector3(-4f, 1.5f, -4.1f), new Vector3(2.5f, 3f, 0.3f), DoorDark);
        }

        private void BuildRightBuilding()
        {
            // 右楼（较矮、较宽）：主体 10×8×8，蓝灰（非白）
            Cube("RightBldg", new Vector3(6f, 4f, 0f), new Vector3(10f, 8f, 8f), new Color(121f / 255f, 141f / 255f, 158f / 255f));
            // 右上角金色烟囱/屋顶小箱
            Cube("Chimney", new Vector3(9f, 8.6f, 0f), new Vector3(2f, 1.2f, 2f), CrateGold);
        }

        private void BuildStairs()
        {
            // 两楼之间的大台阶（浅灰，向右下延伸，8 级，在建筑前方）
            for (var i = 0; i < 8; i++)
            {
                var h = i + 1;
                Cube("Step" + i, new Vector3(1f + i * 0.9f, h * 0.5f - 0.25f, -3.5f), new Vector3(1.6f, 0.5f, 2.5f), Stairs);
            }
        }

        private void BuildCrates()
        {
            // 远左侧小箱（金橄榄，建筑左前方）
            Cube("CrateFar", new Vector3(-9f, 0.8f, -3f), new Vector3(1.6f, 1.6f, 1.6f), CrateGold);
            // 中央大箱（L 形双层：橄榄主体 + 金顶，建筑前方）
            Cube("CrateBig", new Vector3(-2f, 1.2f, -3f), new Vector3(2.4f, 2.4f, 2.4f), CrateOlive);
            Cube("CrateBigTop", new Vector3(-2f, 2.6f, -3f), new Vector3(2.6f, 0.5f, 2.6f), CrateGold);
            // 右楼基座箱（橄榄棕，右前方）
            Cube("CrateRight", new Vector3(5f, 1f, -3f), new Vector3(2f, 2f, 2f), CrateOlive);
        }

        private void Cube(string name, Vector3 pos, Vector3 size, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = color };
        }
    }
}
