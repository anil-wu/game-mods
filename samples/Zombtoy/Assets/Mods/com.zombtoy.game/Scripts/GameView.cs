using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;
using UnityEngine;

namespace Com.Zombtoy.Game
{
    /// <summary>
    /// 相位视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8）：
    /// - 按相位展示：Menu/Result 相位隐藏 Level 环境；Playing/Paused/GameOver 相位展示（契约 §8 备注）。
    /// - 视觉自建（任务：视图自建视觉，不依赖 prefab）：PrimitiveType 图元 + 内置 Standard 材质——
    ///   蓝灰地板（Cube，layer=Floor 供玩家鼠标转身射线）+ 四周围墙（Cube）。
    /// - 光照：方向光由宿主引导提供（AGENTS.md §8：EnsureUrpMainCamera 建主相机 + 方向光），本视图不重复创建。
    /// - 相机：等距俯视视角（Camera.main 放玩家后上方看向中心；PlayerView 每帧接管跟随，本视图仅首次构建兜底摆位）。
    /// - 天空盒：尝试加载 AllSkyFree 材质（失败回退 Unity 默认，不阻塞游戏）。
    /// - 资源 prefab GUID 断链未修（材质缺失渲染全灰），环境一律程序化自建（任务：视图自建视觉，不依赖 prefab）。
    /// - 逻辑出生点由 enemy/item Mod 自持（契约 §8：Level 内 EnemySpawnPoints/ItemSpawnPoints 仅供视觉参考）。
    /// </summary>
    public sealed class GameView : MonoBehaviour
    {
        private static readonly AssetId SkyboxDay = new(GameMod.ModIdValue, "AllSkyFree/Cartoon Base BlueSky/Day_BlueSky_Nothing");

        /// <summary>Floor 层（玩家鼠标转身射线命中层；工程未配置时 -1 → 地板保持默认层）。Awake 里解析（Unity 禁止字段初始化调 NameToLayer）。</summary>
        private static int FloorLayer = -1;

        private GameObject? _environment; // 环境根（美术环境 + 程序化兜底）
        private byte _lastPhase = byte.MaxValue;

        private void Awake()
        {
            FloorLayer = LayerMask.NameToLayer("Floor"); // Unity 禁止字段初始化调 NameToLayer
            RenderSettings.skybox = LoadSkybox(); // 天空盒（AllSkyFree；失败回退默认，不阻塞）
        }

        private void Update()
        {
            var world = GameMod.World;
            if (world is null || GameMod.GameEntityId == 0) return;
            var e = new Entity(GameMod.GameEntityId);
            if (!world.TryGet<GameState>(e, out var state)) return;

            if (_lastPhase == state.Phase) return; // 相位未变：不重复开关

            // 按相位展示（契约 §8：Menu/Result 隐藏；Playing/Paused/GameOver 展示）
            var visible = state.Phase != (byte)GamePhase.Menu && state.Phase != (byte)GamePhase.Result;
            if (visible)
            {
                EnsureEnvironment(); // 首次进入 Gameplay 相位时自建 Level 环境
            }
            else if (_environment != null)
            {
                _environment.SetActive(false);
            }
            _lastPhase = state.Phase;
        }

        private void OnDestroy()
        {
            if (_environment != null) Destroy(_environment);
            _environment = null;
        }

        /// <summary>重建 Level 环境（幂等：已有则激活）：程序化蓝灰地板 + 四周围墙（任务：视图自建视觉，不依赖 prefab）。</summary>
        private void EnsureEnvironment()
        {
            if (_environment != null)
            {
                _environment.SetActive(true);
                return;
            }

            var root = new GameObject("Zombtoy_Level1_Environment");
            BuildProgrammaticFloor(root.transform); // 程序化地板 + 四周围墙（prefab GUID 断链不依赖）
            _environment = root;

            FrameIsoCamera(); // 等距俯视兜底摆位（PlayerView 随后每帧接管 Camera.main 跟随）
        }

        /// <summary>加载天空盒材质（AllSkyFree 资源域，Rule 3；失败返回 null → 保持宿主默认天空盒）。</summary>
        private static Material? LoadSkybox()
        {
            var ctx = GameMod.Context;
            if (ctx is null) return null;
            try { return ctx.Resources.Load(SkyboxDay) as Material; }
            catch (System.Exception e) { Debug.LogError($"[GameView] 天空盒加载异常 {SkyboxDay}: {e.Message}"); return null; }
        }

        /// <summary>
        /// 程序化环境（资源 bundle 缺失/GUID 断链时兜底，保证可玩）：蓝灰地板 + 四边矮墙（对齐 Level1 包围盒语义，
        /// 内置管线 Standard 材质）。地板/墙置于 Floor 层（玩家鼠标射线转身命中；工程未配置该层时保持默认层）。
        /// </summary>
        private static void BuildProgrammaticFloor(Transform parent)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            if (FloorLayer >= 0) floor.layer = FloorLayer;
            floor.transform.SetParent(parent);
            floor.transform.localScale = new Vector3(40f, 0.2f, 40f);
            floor.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            floor.GetComponent<Renderer>().material = StandardMaterial(new Color(0.45f, 0.56f, 0.66f)); // 蓝灰地板

            BuildWall(parent, new Vector3(0f, 1.5f, -20f), new Vector3(40f, 3f, 0.4f));
            BuildWall(parent, new Vector3(0f, 1.5f, 20f), new Vector3(40f, 3f, 0.4f));
            BuildWall(parent, new Vector3(-20f, 1.5f, 0f), new Vector3(0.4f, 3f, 40f));
            BuildWall(parent, new Vector3(20f, 1.5f, 0f), new Vector3(0.4f, 3f, 40f));
        }

        private static void BuildWall(Transform parent, Vector3 pos, Vector3 scale)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Fallback_Wall";
            if (FloorLayer >= 0) wall.layer = FloorLayer;
            wall.transform.SetParent(parent);
            wall.transform.localPosition = pos;
            wall.transform.localScale = scale;
            wall.GetComponent<Renderer>().material = StandardMaterial(new Color(0.3f, 0.34f, 0.4f));
        }

        /// <summary>等距俯视相机兜底摆位（Camera.main 在玩家后上方看向场地中心；PlayerView 每帧接管）。</summary>
        private static void FrameIsoCamera()
        {
            var cam = Camera.main;
            if (cam is null) return; // 宿主未建相机（防御）
            cam.transform.position = new Vector3(0f, 12f, -8f);
            cam.transform.LookAt(new Vector3(0f, 1f, 0f));
        }

        private static Material StandardMaterial(Color color)
        {
            var shader = Shader.Find("Standard");
            var mat = shader != null ? new Material(shader) : new Material(Shader.Find("Diffuse"));
            mat.color = color;
            return mat;
        }
    }
}
