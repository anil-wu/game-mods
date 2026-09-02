using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Com.Zombtoy.Game
{
    /// <summary>
    /// 相位视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8）：
    /// - 按相位展示：Menu/Result 相位隐藏 Level 环境；Playing/Paused/GameOver 相位展示（契约 §8 备注）。
    /// - 视觉：优先加载**原游戏资源**（Rule 3：经本 Mod context.Resources.Load，不 Resources.Load/AssetDatabase）——
    ///   Prefabs/Environment.prefab 原 Level1 环境；加载失败回退程序化地板+四周围墙（保持可玩不崩溃）。
    /// - 鼠标转身射线（契约 §8）：原环境 Floor 无 collider（仅动画），加载后补一块不可见 Floor 层命中面；
    ///   并把名为 Floor 的对象子树切到 Floor 层（工程未配置该层时保持默认层，PlayerView 回退任意层）。
    /// - 光照：方向光由宿主引导提供（AGENTS.md §8：EnsureUrpMainCamera 建主相机 + 方向光），本视图不重复创建。
    /// - 相机：等距俯视视角（Camera.main 放玩家后上方看向中心；PlayerView 每帧接管跟随，本视图仅首次构建兜底摆位）。
    /// - 天空盒：尝试加载 AllSkyFree 材质（失败回退 Unity 默认，不阻塞游戏）。
    /// - 逻辑出生点由 enemy/item Mod 自持（契约 §8：Level 内 EnemySpawnPoints/ItemSpawnPoints 仅供视觉参考）。
    /// </summary>
    public sealed class GameView : MonoBehaviour
    {
        private static readonly AssetId SkyboxDay = new(GameMod.ModIdValue, "AllSkyFree/Cartoon Base BlueSky/Day_BlueSky_Nothing");
        private static readonly AssetId EnvironmentPrefab = new(GameMod.ModIdValue, "Prefabs/Environment");

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

        /// <summary>重建 Level 环境（幂等：已有则激活）：优先原 Environment.prefab，失败回退程序化地板+围墙。</summary>
        private void EnsureEnvironment()
        {
            if (_environment != null)
            {
                _environment.SetActive(true);
                return;
            }

            _environment = BuildEnvironment();
            BakeNavMesh(_environment); // 供敌人 NavMeshAgent 追击（FBX 地板不可读，用平面代理烘焙）
            FrameIsoCamera(); // 等距俯视兜底摆位（PlayerView 随后每帧接管 Camera.main 跟随）
        }

        /// <summary>给地板烘焙 NavMesh（敌人 NavMeshAgent 追击）。FBX 地板网格不可读 → 用不可见平面代理烘焙（Rule 3 不引入新资源）。</summary>
        private static void BakeNavMesh(GameObject root)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = "NavMeshFloorProxy";
            go.transform.SetParent(root.transform, false);
            go.transform.localScale = new Vector3(4.5f, 1f, 4.5f); // 45×45 平面（原 Level1 平台 ~47×47）
            go.transform.localPosition = Vector3.zero;
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.enabled = false;

            var source = new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Mesh,
                sourceObject = go.GetComponent<MeshFilter>().sharedMesh,
                transform = go.transform.localToWorldMatrix,
                area = 0
            };
            var bounds = new Bounds(Vector3.zero, new Vector3(45f, 5f, 45f));
            var data = NavMeshBuilder.BuildNavMeshData(NavMesh.GetSettingsByID(0),
                new List<NavMeshBuildSource> { source }, bounds, Vector3.zero, Quaternion.identity);
            if (data != null)
            {
                NavMesh.AddNavMeshData(data);
                Debug.Log("[GameView] NavMesh 已烘焙（45×45 平面代理）");
            }
        }

        /// <summary>
        /// 构建 Level 环境：经 context.Resources.Load 加载原 Environment.prefab（Rule 3，禁 Resources.Load/AssetDatabase）；
        /// 加载失败回退程序化蓝灰地板+四周围墙（bundle 缺失时保证可玩）。
        /// </summary>
        private static GameObject BuildEnvironment()
        {
            var ctx = GameMod.Context;
            GameObject? env = null;
            if (ctx is not null)
            {
                try { env = ctx.Resources.Load(EnvironmentPrefab) as GameObject; }
                catch (System.Exception e) { Debug.LogError($"[GameView] 环境加载异常 {EnvironmentPrefab}: {e.Message}"); env = null; }
            }

            GameObject root;
            if (env is null)
            {
                Debug.LogWarning($"[GameView] 原环境缺失（{EnvironmentPrefab}）→ 回退程序化地板");
                root = new GameObject("Zombtoy_Level1_Environment");
                BuildProgrammaticFloor(root.transform);
                return root;
            }

            root = Object.Instantiate(env);
            root.name = "Zombtoy_Level1_Environment";
            FixMigratedLevelPosition(root);  // 迁移后平台整体抬升 ≈23.6 → 回落 y=0（原版 Level1 平台顶面贴地）
            FixMigratedLevelWalls(root);     // 迁移后 Wall/Sides 变 70m 实心盒遮挡全场 → 隐藏（原版为薄墙）
            RemapEnvironmentMaterials(root, ctx);         // 内嵌材质无纹理 → 本 Mod Materials/ 按名重映射（Rule 3）
            EnsureFloorLayer(root.transform);          // 原环境 Floor 切到 Floor 层（鼠标转身射线）
            EnsureRaycastSurface(root);                // 鼠标转身射线命中面兜底
            Debug.Log($"[GameView] 原环境已加载 → {EnvironmentPrefab}");
            return root;
        }

        /// <summary>
        /// 迁移缩放修复：原版 Wall/Sides/Stars 是薄墙/装饰，迁移后重导入比例失真成覆盖整个场地的实心大盒
        /// （Wall 72×41×72 跨 z=-0.8..71、Sides 70×23×70 盖满 ±35、Stars 68×30×68 同）→ 隐藏这几类结构体
        /// （Base/Planks 地板保留）。
        /// </summary>
        private static void FixMigratedLevelWalls(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>())
            {
                if (t.name == "Wall" || t.name == "Sides" || t.name == "Stars")
                {
                    t.gameObject.SetActive(false);
                    Debug.Log($"[GameView] 迁移缩放失真结构已隐藏: {t.name}");
                }
            }
        }

        /// <summary>
        /// 环境材质重映射：FBX/prefab 内嵌材质打包后无纹理 → 用本 Mod Materials/&lt;对象名&gt;Material.mat 替换
        /// （Rule 3；纹理 GUID 已修复。Collider 类对象/无对应材质的保持默认，不阻塞）。
        /// </summary>
        private static void RemapEnvironmentMaterials(GameObject root, global::Game.Mod.Runtime.IModContext? ctx)
        {
            if (ctx is null) return;
            var assigned = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                var name = r.gameObject.name.Replace("Collider", "");
                if (name == "Stars") name = "Star"; // 材质资产名是 StarMaterial（对象是 Stars）
                if (name == "Base" || name == "Sides" || name == "Floor" || name == "LevelExtent")
                    continue; // 平台结构/无对应材质：保持默认
                Material? mat = null;
                try
                {
                    mat = ctx.Resources.Load(new global::Game.Mod.Contract.AssetId(GameMod.ModIdValue, $"Materials/{name}Material")) as Material;
                }
                catch (System.Exception) { mat = null; }
                if (mat == null) continue;
                r.sharedMaterial = mat;
                assigned++;
            }
            if (assigned > 0)
                Debug.Log($"[GameView] 环境材质重映射 {assigned} 个渲染体 → Materials/*Material");
        }

        /// <summary>
        /// 迁移坐标修复：原工程 Level1 平台在 y=0，迁移后 prefab 内部坐标整体抬升（≈23.6，Floor.fbx
        /// 重导入比例差异）→ 把最大水平台面（Base/Planks，47×47）顶面回落 y=0，敌人/道具按逻辑 y=0 站立。
        /// </summary>
        private static void FixMigratedLevelPosition(GameObject root)
        {
            var top = float.MinValue;
            var found = false;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                if (r.bounds.size.y < 2f && r.bounds.size.x > 20f && r.bounds.size.z > 20f)
                {
                    if (r.bounds.max.y > top) { top = r.bounds.max.y; }
                    found = true;
                }
            }
            if (!found) return; // 无大平台（程序化兜底已覆盖）
            root.transform.localPosition = new Vector3(0f, -top, 0f);
            Debug.Log($"[GameView] 平台顶面回落 y=0（偏移 {top:F2}）");
        }

        /// <summary>把名为 Floor 的对象及其子树切到 Floor 层（原 Level1 环境 Floor 在默认层，鼠标转身射线契约 §8）。</summary>
        private static void EnsureFloorLayer(Transform root)
        {
            if (FloorLayer < 0) return; // 工程未配置 Floor 层：保持原样，PlayerView 回退任意层
            if (root.name == "Floor")
            {
                SetLayerRecursive(root.gameObject, FloorLayer);
                return;
            }
            foreach (Transform child in root)
                EnsureFloorLayer(child);
        }

        /// <summary>原环境 Floor 无 collider（仅 Transform+Animator）→ 补一块不可见 Floor 层命中面，供玩家鼠标转身射线。</summary>
        private static void EnsureRaycastSurface(GameObject root)
        {
            if (FloorLayer < 0) return;
            var probe = new GameObject("FloorRaycastSurface");
            probe.transform.SetParent(root.transform, false);
            probe.layer = FloorLayer;
            var box = probe.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -0.25f, 0f);
            box.size = new Vector3(50f, 0.2f, 50f); // 覆盖原 Level1 包围盒（±24 内），顶部 y≈-0.15 不阻挡角色
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
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
