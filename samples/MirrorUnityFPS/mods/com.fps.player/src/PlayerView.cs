using System.Collections.Generic;
using Game.ECS;
using Game.Mod.Contract;
using UnityEngine;

namespace Com.Fps.Player
{
    /// <summary>
    /// 玩家视图（M1，忠实资源版）：
    /// - 本地玩家：第一人称相机（跟随复制位置 + 鼠标偏航/俯仰）
    /// - 远程玩家/靶标：科幻兵模型（经所属 Mod 的 ResourceScope 加载 Prefab，Rule 3/§8.4），面朝 Facing
    /// 移动动画（AnimatorController 接线）在 M1 后补；移动/朝向权威在 Server（§11.10）。
    /// </summary>
    public sealed class PlayerView : MonoBehaviour
    {
        private static readonly AssetId BlueSoldier = new(PlayerMod.ModIdValue,
            "Model/Futuristic_soldier/Prefabs/Sci-fi_character_unity_blue Variant");
        private static readonly AssetId RedSoldier = new(PlayerMod.ModIdValue,
            "Model/Futuristic_soldier/Prefabs/Sci-fi_character_unity_red Variant");

        private static readonly InputProtocol InputProto = new();

        private readonly Dictionary<uint, GameObject> _bodies = new(); // entityId → 士兵模型
        private GameObject? _bluePrefab;
        private GameObject? _redPrefab;
        private Camera? _camera;
        private float _yaw;
        private float _pitch;

        private void Awake()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "FpsFloor";
            floor.transform.localScale = new Vector3(2f, 1f, 2f);
            floor.GetComponent<Renderer>().material.color = new Color(0.28f, 0.32f, 0.28f);

            _camera = Camera.main;
            if (_camera is null)
            {
                var go = new GameObject("FpsCamera");
                _camera = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
        }

        private void Update()
        {
            var world = PlayerMod.World;
            if (world is null) return;
            DriveLocalInput();
            SyncBodies();
            DriveCamera();
        }

        // ---- 本地输入 → player:input（C2S，服务端权威） ----

        private void DriveLocalInput()
        {
            var ctx = PlayerMod.Context;
            if (ctx is null || !ctx.Network.IsActive) return;

            if (Input.GetMouseButtonDown(0))
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                _yaw += Input.GetAxis("Mouse X") * 2.2f;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * 2.2f, -80f, 80f);
            }

            var moveX = Input.GetAxisRaw("Horizontal");
            var moveZ = Input.GetAxisRaw("Vertical");
            var jump = Input.GetKey(KeyCode.Space);
            ctx.Network.SendToServer(InputProto.Id, new InputState(moveX, moveZ, _yaw, jump));
        }

        // ---- 复制状态 → 士兵模型 ----

        private void SyncBodies()
        {
            var world = PlayerMod.World;
            foreach (var (entityId, pos) in world.Store<Position3>().All())
            {
                var e = new Entity(entityId);
                if (world.Has<ReplicaTag>(e)) continue; // 取服务端权威侧
                if (entityId == PlayerMod.LocalPlayerEntityId) continue; // 本地玩家第一人称

                if (!_bodies.TryGetValue(entityId, out var body))
                {
                    var isDummy = world.TryGet<PlayerTag>(e, out var tag) && tag.IsDummy;
                    var prefab = isDummy ? GetRedSoldier() : GetBlueSoldier();
                    body = prefab is not null ? Instantiate(prefab) : GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    body.name = $"Player_{entityId}";
                    _bodies[entityId] = body;
                }

                body.transform.position = new Vector3(pos.X, pos.Y, pos.Z);
                var yaw = world.TryGet<Facing>(e, out var facing) ? facing.Yaw : 0f;
                body.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                var alive = world.TryGet<Health>(e, out var hp) && hp.Current > 0;
                if (!alive) body.transform.rotation = Quaternion.Euler(85f, yaw, 0f); // 倒地
            }
        }

        private void DriveCamera()
        {
            if (_camera is null || PlayerMod.LocalPlayerEntityId == 0) return;
            var world = PlayerMod.World;
            var local = new Entity(PlayerMod.LocalPlayerEntityId);
            if (!world.TryGet<Position3>(local, out var pos)) return;
            _camera.transform.position = new Vector3(pos.X, pos.Y + 0.6f, pos.Z);
            _camera.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private GameObject? GetBlueSoldier()
        {
            if (_bluePrefab is null)
                _bluePrefab = LoadPrefab(BlueSoldier);
            return _bluePrefab;
        }

        private GameObject? GetRedSoldier()
        {
            if (_redPrefab is null)
                _redPrefab = LoadPrefab(RedSoldier);
            return _redPrefab;
        }

        /// <summary>经所属 Mod 的 ResourceScope 加载 Prefab（资源归属 Rule 3/§8.4）。</summary>
        private static GameObject? LoadPrefab(AssetId id)
        {
            var ctx = PlayerMod.Context;
            if (ctx is null) return null;
            try { return ctx.Resources.Load(id) as GameObject; }
            catch (System.Exception) { return null; }
        }

        private void OnGUI()
        {
            var world = PlayerMod.World;
            if (world is null || PlayerMod.LocalPlayerEntityId == 0) return;
            var local = new Entity(PlayerMod.LocalPlayerEntityId);
            if (world.TryGet<Health>(local, out var hp))
                GUI.Label(new Rect(20, 20, 300, 30), $"HP: {hp.Current} / {hp.Max}");
            GUI.Label(new Rect(20, 44, 500, 30), "WASD 移动 / 空格跳 / 鼠标视角（点击锁定）/ Esc 释放");
        }
    }
}
