using Game.ECS;
using Game.Mod.Contract;
using UnityEngine;

namespace Com.Zombtoy.Player
{
    /// <summary>
    /// 玩家视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8）：
    /// - 输入：WASD+Shift → 写 PlayerInput（MoveX/MoveZ/Sprint）；鼠标射线打 Floor 层 → 写 Facing.Yaw（转身）
    /// - 视觉：优先加载**原游戏资源**（Rule 3：经本 Mod context.Resources.Load，不 Resources.Load/AssetDatabase）——
    ///   Models/Characters/Player.fbx 模型 + Animations/PlayerAC.controller；加载失败回退程序化胶囊（保持可玩不崩溃）
    /// - 表现：每帧读逻辑权威 Position3/Facing 同步 Transform（Rigidbody.MovePosition，契约 §1）
    /// - 相机：Camera.main 等距跟随（对齐原版 CameraFollow 行为；逻辑为本视图自写，不搬原版代码）
    /// 实体↔视图映射（summary §0.6）：视图持有 public uint EntityId，单机 Host 下绑定 PlayerMod.LocalPlayerEntityId。
    /// 逻辑权威在 ECS（系统/能力内），视图只写"请求"（PlayerInput/Facing）与读"状态"（Position3/Facing）。
    /// </summary>
    public sealed class PlayerView : MonoBehaviour
    {
        // ---- 原游戏资源 AssetId（Rule 3 / §8.2：路径 = Mod 目录内相对路径去扩展名） ----
        private static readonly AssetId PlayerModel = new(PlayerMod.ModIdValue, "Models/Characters/Player");
        private static readonly AssetId PlayerAc = new(PlayerMod.ModIdValue, "Animations/PlayerAC");
        // 自带材质（FBX 内嵌材质打包后无纹理 → 用本 Mod PlayerMaterial 重映射，Rule 3；纹理 GUID 已修复可解析）
        private static readonly AssetId PlayerMat = new(PlayerMod.ModIdValue, "Materials/PlayerMaterial");

        /// <summary>视图绑定的玩家实体（宿主/本视图 Awake 时从 PlayerMod 取得，summary §0.6）。</summary>
        public uint EntityId;

        /// <summary>等距相机偏移（主机验收视角：玩家后上方，敌人/模型近景可见）。</summary>
        public Vector3 CameraOffset = new(0f, 12f, -8f);

        /// <summary>鼠标转身命中的地面层（game Mod Level 场景铺 Floor 层；未配置时回退任意层）。</summary>
        public string FloorLayerName = "Floor";

        private Camera? _camera;
        private Rigidbody? _rigidbody;
        private bool _hasRigidbody;
        private int _floorMask;

        private void Awake()
        {
            if (EntityId == 0 && PlayerMod.LocalPlayerEntityId != 0)
                EntityId = PlayerMod.LocalPlayerEntityId;

            BuildVisuals(); // 宿主只建空 GameObject → 视图自建胶囊 + Rigidbody（不依赖 prefab）
            _rigidbody = GetComponent<Rigidbody>();
            _hasRigidbody = _rigidbody != null;
            _camera = Camera.main;
            if (_camera is null)
                Debug.LogWarning("[PlayerView] Camera.main 为空（宿主应创建主相机）");

            // Floor 层不存在时回退全层射线（宿主未配置该层也能转身）
            var layer = LayerMask.NameToLayer(FloorLayerName);
            _floorMask = layer >= 0 ? 1 << layer : ~0;
        }

        private void Update()
        {
            var world = PlayerMod.World;
            if (world is null || EntityId == 0) return;

            var entity = new Entity(EntityId);
            if (!world.Exists(entity)) return;

            DriveInput(world, entity);    // WASD+Shift + 鼠标转身 → 写请求组件（系统内合成逻辑速度，§0.6）
            SyncTransform(world, entity); // 逻辑权威 Position3/Facing → Transform
            DriveCamera(world, entity);   // Camera.main 等距跟随
        }

        /// <summary>自建视觉组件（幂等：已有渲染体/物理体时跳过——prefab 实例路径兼容）。
        /// 优先原模型（context.Resources.Load，Rule 3），失败回退青蓝胶囊。</summary>
        private void BuildVisuals()
        {
            // 注意：GetComponentInChildren 可能返回 fake-null（is null 不识别），用 Unity == null 判断
            if (GetComponentInChildren<Renderer>() == null && !TryBuildOriginalModel())
                BuildFallbackCapsule();

            // Rigidbody：无重力/受控（isKinematic=false + MovePosition 平滑；FreezeRotation 防倾倒）
            var existing = GetComponent<Rigidbody>();
            if (existing == null)
            {
                var rb = gameObject.AddComponent<Rigidbody>();
                rb.useGravity = false;
                rb.isKinematic = false; // 非运动学：MovePosition 走物理（贴墙受阻，视觉更合理）
                rb.constraints = RigidbodyConstraints.FreezeRotation;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
            }
        }

        /// <summary>
        /// 经 context.Resources.Load 加载原 Player.fbx 作为视觉（Rule 3，禁 Resources.Load/AssetDatabase）。
        /// 模型脚底自动对齐（逻辑 Position3.Y=1 为中心约定 → 脚底贴地），补 Collider + Animator(PlayerAC)。
        /// 返回 false = 资源缺失，调用方回退胶囊。
        /// </summary>
        private bool TryBuildOriginalModel()
        {
            var ctx = PlayerMod.Context;
            if (ctx is null) return false; // 未注册（防御）

            GameObject? asset = null;
            try { asset = ctx.Resources.Load(PlayerModel) as GameObject; }
            catch (System.Exception e) { Debug.LogWarning($"[PlayerView] 模型加载异常 {PlayerModel}: {e.Message}"); asset = null; }
            if (asset is null)
            {
                Debug.LogWarning($"[PlayerView] 原模型缺失（{PlayerModel}）→ 回退胶囊");
                return false;
            }

            var body = Object.Instantiate(asset);
            body.name = "PlayerBody";
            body.transform.SetParent(transform, false);

            // 脚底着地：逻辑 Position3.Y=1 为中心约定（root 在 Y=1）→ 模型脚底对齐世界 y=0：
            // localPosition.y = 0 - 1 - feetY（feetY 为模型脚底在模型空间的偏移，bounds 自动计算，不依赖导入约定）
            var feetY = ComputeFeetLocalY(body);
            body.transform.localPosition = new Vector3(0f, -1f - feetY, 0f);

            // 命中/碰撞（原版 CapsuleCollider 半径 0.5 高 ~1.55 脚底 0）：模型无 collider 时在根节点补一个
            // （根在 Y=1：collider 脚底 0 顶 ~1.6，对齐原版高度；供 MovePosition 与环境/墙体碰撞阻挡）
            if (GetComponent<Collider>() == null && body.GetComponentInChildren<Collider>() == null)
            {
                var col = gameObject.AddComponent<CapsuleCollider>();
                col.radius = 0.5f;
                col.height = 1.6f;
                col.center = new Vector3(0f, -0.2f, 0f);
            }

            // Animator 挂 PlayerAC.controller（Rule 3；Avatar 子资产 scope 不可达 → 保持绑定姿态，模型仍可见）
            var anim = body.GetComponent<Animator>();
            if (anim == null) anim = body.AddComponent<Animator>();
            anim.applyRootMotion = false;
            try { anim.runtimeAnimatorController = ctx.Resources.Load(PlayerAc) as RuntimeAnimatorController; }
            catch (System.Exception ex) { Debug.LogWarning($"[PlayerView] PlayerAC 加载异常: {ex.Message}"); }

            // 材质重映射：FBX 内嵌材质无纹理 → 用本 Mod PlayerMaterial（纹理 GUID 已修复；失败保持默认）
            Material? mat = null;
            try { mat = ctx.Resources.Load(PlayerMat) as Material; }
            catch (System.Exception me) { Debug.LogWarning($"[PlayerView] 材质加载异常 {PlayerMat}: {me.Message}"); mat = null; }
            if (mat != null)
            {
                foreach (var r in body.GetComponentsInChildren<Renderer>())
                    r.sharedMaterial = mat;
            }

            Debug.Log($"[PlayerView] 玩家模型已加载 → {PlayerModel}（材质 {PlayerMat}）");
            return true;
        }

        /// <summary>回退：程序化青蓝胶囊（2 单位高，中心对齐根；Position3.Y=1 = 胶囊中心，契约 §1）。</summary>
        private void BuildFallbackCapsule()
        {
            if (GetComponentInChildren<Renderer>() != null) return; // 幂等
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "PlayerBody";
            body.transform.SetParent(transform, false);
            body.transform.localScale = new Vector3(1f, 2f, 1f); // 2 单位高，中心对齐根（Position3.Y=1 = 胶囊中心）
            body.GetComponent<Renderer>().material = StandardMaterial(new Color(0.2f, 0.75f, 0.8f));
        }

        /// <summary>按渲染 bounds 算模型脚底在本地坐标系的 Y（不依赖 FBX 导入原点约定）。</summary>
        private static float ComputeFeetLocalY(GameObject model)
        {
            var renderers = model.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return 0f; // 无渲染体：保持根位置
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return model.transform.InverseTransformPoint(b.min).y;
        }

        // ---- 输入：WASD+Shift → PlayerInput；鼠标射线打 Floor → Facing ----

        private void DriveInput(World world, Entity entity)
        {
            var mx = Input.GetAxisRaw("Horizontal"); // A/D
            var mz = Input.GetAxisRaw("Vertical");   // W/S
            var sprint = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            world.Add(entity, new PlayerInput { MoveX = mx, MoveZ = mz, Sprint = sprint });

            // 鼠标射线打 Floor 层 → 转身（原版 PlayerMovement 机制：角色面朝鼠标落点）
            if (_camera is null) return;
            var ray = _camera.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 200f, _floorMask))
            {
                var pos = world.TryGet<Position3>(entity, out var p) ? p : default;
                // 绕 Y 朝向（度）：atan2(dx, dz)，与 Unity 角色前向 +Z 约定一致
                var dx = hit.point.x - pos.X;
                var dz = hit.point.z - pos.Z;
                world.Add(entity, new Facing { Yaw = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg });
            }
        }

        // ---- 表现：逻辑权威 Position3/Facing → Transform ----

        private void SyncTransform(World world, Entity entity)
        {
            if (!world.TryGet<Position3>(entity, out var pos)) return;
            var target = new Vector3(pos.X, pos.Y, pos.Z);

            if (_hasRigidbody)
                _rigidbody.MovePosition(target); // 视图驱动物理体平滑（契约 §1 Velocity3 注）
            else
                transform.position = target;

            if (world.TryGet<Facing>(entity, out var facing))
                transform.rotation = Quaternion.Euler(0f, facing.Yaw, 0f);
        }

        // ---- 相机：等距跟随 ----

        private void DriveCamera(World world, Entity entity)
        {
            if (_camera is null || !world.TryGet<Position3>(entity, out var pos)) return;
            var playerPos = new Vector3(pos.X, pos.Y, pos.Z);
            _camera.transform.position = playerPos + CameraOffset;
            _camera.transform.LookAt(playerPos);
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
