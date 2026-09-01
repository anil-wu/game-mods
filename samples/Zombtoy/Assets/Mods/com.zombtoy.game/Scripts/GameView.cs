using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;
using UnityEngine;

namespace Com.Zombtoy.Game
{
    /// <summary>
    /// 相位视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8）：
    /// - 按相位展示：Menu/Result 相位隐藏 Level 环境；Playing/Paused/GameOver 相位展示（契约 §8 备注）。
    /// - Gameplay 时从本 Mod 资源域（context.Resources，Rule 3）加载 Level1 环境 prefab
    ///   （Prefabs/Environment.prefab：地板/墙/障碍 + Prefabs/Lights.prefab：光照）实例化；
    ///   天空盒用 AllSkyFree 材质（加载失败回退 Unity 默认天空盒，不阻塞游戏）。
    /// - 逻辑出生点由 enemy/item Mod 自持（契约 §8：Level 内 EnemySpawnPoints/ItemSpawnPoints 仅供视觉参考）。
    /// - 资源加载失败回退程序化地板（保证可玩），光照依赖宿主引导（AGENTS.md §8：宿主建主相机 + 方向光）。
    /// </summary>
    public sealed class GameView : MonoBehaviour
    {
        // 本 Mod 资源域内的资源路径（AssetId "{modId}:{path}" → bundle 内 "assets/mods/{modId}/{path}"，§15.1）
        private static readonly AssetId EnvironmentPrefab = new(GameMod.ModIdValue, "Prefabs/Environment");
        private static readonly AssetId LightsPrefab = new(GameMod.ModIdValue, "Prefabs/Lights");
        private static readonly AssetId SkyboxDay = new(GameMod.ModIdValue, "AllSkyFree/Cartoon Base BlueSky/Day_BlueSky_Nothing");

        private GameObject? _environment; // 环境根（Environment + Lights + 回退地板）
        private byte _lastPhase = byte.MaxValue;

        private void Awake()
        {
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
                EnsureEnvironment(); // 首次进入 Gameplay 相位时从本 Mod 资源重建 Level 环境
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

        /// <summary>重建 Level 环境（幂等：已有则激活）：Environment prefab（地板/墙/障碍）+ Lights prefab。</summary>
        private void EnsureEnvironment()
        {
            if (_environment != null)
            {
                _environment.SetActive(true);
                return;
            }

            var root = new GameObject("Zombtoy_Level1_Environment");

            // 1. 环境 prefab（地板/墙/障碍，资源迁移来源 replica_projects/Zombtoy/Assets/Level1，契约 §8）
            var env = LoadPrefab(EnvironmentPrefab);
            if (env != null)
            {
                var inst = Instantiate(env, root.transform);
                inst.name = "Level1_Environment";
            }
            else
            {
                BuildFallbackFloor(root.transform); // 资源缺失兜底：保证可玩（不阻塞游戏）
            }

            // 2. 光照 prefab（失败回退宿主引导方向光，AGENTS.md §8）
            var lights = LoadPrefab(LightsPrefab);
            if (lights != null)
            {
                var inst = Instantiate(lights, root.transform);
                inst.name = "Level1_Lights";
            }

            _environment = root;
        }

        /// <summary>经所属 Mod 的 ResourceScope 加载 Prefab（资源归属 Rule 3/§8.4；失败返回 null 由调用方兜底）。</summary>
        private static GameObject? LoadPrefab(AssetId id)
        {
            var ctx = GameMod.Context;
            if (ctx is null) { Debug.LogWarning($"[GameView] Context 未就绪，加载失败 {id}"); return null; }
            try
            {
                var obj = ctx.Resources.Load(id) as GameObject;
                Debug.Log(obj is null ? $"[GameView] 加载失败（回退程序化构建）{id}" : $"[GameView] 加载成功 {id}");
                return obj;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[GameView] 加载异常 {id}: {e.Message}");
                return null;
            }
        }

        /// <summary>加载天空盒材质（AllSkyFree 资源域，Rule 3；失败返回 null → 保持宿主默认天空盒）。</summary>
        private static Material? LoadSkybox()
        {
            var ctx = GameMod.Context;
            if (ctx is null) return null;
            try { return ctx.Resources.Load(SkyboxDay) as Material; }
            catch (System.Exception e) { Debug.LogError($"[GameView] 天空盒加载异常 {SkyboxDay}: {e.Message}"); return null; }
        }

        /// <summary>回退地板：资源 bundle 缺失时保证可玩（方形地板 + 四边矮墙，对齐 Level1 包围盒语义，内置管线 Standard 材质）。</summary>
        private static void BuildFallbackFloor(Transform parent)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Fallback_Floor";
            floor.transform.SetParent(parent);
            floor.transform.localScale = new Vector3(40f, 0.2f, 40f);
            floor.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            floor.GetComponent<Renderer>().material = StandardMaterial(new Color(0.45f, 0.45f, 0.45f));

            BuildWall(parent, new Vector3(0f, 1.5f, -20f), new Vector3(40f, 3f, 0.4f));
            BuildWall(parent, new Vector3(0f, 1.5f, 20f), new Vector3(40f, 3f, 0.4f));
            BuildWall(parent, new Vector3(-20f, 1.5f, 0f), new Vector3(0.4f, 3f, 40f));
            BuildWall(parent, new Vector3(20f, 1.5f, 0f), new Vector3(0.4f, 3f, 40f));
        }

        private static void BuildWall(Transform parent, Vector3 pos, Vector3 scale)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Fallback_Wall";
            wall.transform.SetParent(parent);
            wall.transform.localPosition = pos;
            wall.transform.localScale = scale;
            wall.GetComponent<Renderer>().material = StandardMaterial(new Color(0.3f, 0.3f, 0.32f));
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
