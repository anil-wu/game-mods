using System.Collections.Generic;
using Game.ECS;
using UnityEngine;

namespace Com.Fps.Player
{
    /// <summary>
    /// 玩家视图（MonoBehaviour，代码原语，零美术资源）：
    /// 平面场地 + 胶囊体玩家 + 第一人称相机（本地玩家）+ 输入上报（WASD/跳跃/鼠标偏航）。
    /// 由运行时通用实例化；表现只读复制状态，权威在 Server（§11.10）。
    /// </summary>
    public sealed class PlayerView : MonoBehaviour
    {
        private readonly Dictionary<uint, GameObject> _capsules = new(); // entityId → 胶囊体
        private Camera? _camera;
        private float _yaw;
        private float _pitch;
        private static readonly InputProtocol InputProto = new();

        private void Awake()
        {
            // 场地（10x 缩放的平面）
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "FpsFloor";
            floor.transform.localScale = new Vector3(2f, 1f, 2f);
            floor.GetComponent<Renderer>().material.color = new Color(0.3f, 0.35f, 0.3f);

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
            if (PlayerMod.World is null) return;
            SyncCapsules();
            DriveLocalInput();
            DriveCamera();
        }

        // ---- 复制状态 → 胶囊体 ----

        private void SyncCapsules()
        {
            var world = PlayerMod.World;
            foreach (var (entityId, pos) in world.Store<Position3>().All())
            {
                var entity = new Entity(entityId);
                if (world.Has<ReplicaTag>(entity)) continue; // Host：表现取服务端权威侧（副本是回环镜像）
                if (!_capsules.TryGetValue(entityId, out var capsule))
                {
                    capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    capsule.name = $"Player_{entityId}";
                    var isDummy = world.TryGet<PlayerTag>(entity, out var tag) && tag.IsDummy;
                    capsule.GetComponent<Renderer>().material.color =
                        entityId == PlayerMod.LocalPlayerEntityId ? new Color(0.2f, 0.8f, 0.2f)
                        : isDummy ? new Color(0.85f, 0.25f, 0.25f)
                        : new Color(0.25f, 0.45f, 0.85f);
                    _capsules[entityId] = capsule;
                }

                capsule.transform.position = new Vector3(pos.X, pos.Y, pos.Z);
                var alive = world.TryGet<Health>(entity, out var hp) && hp.Current > 0;
                // 死亡倒地
                capsule.transform.rotation = alive
                    ? Quaternion.identity
                    : Quaternion.Euler(85f, 0f, 0f);
            }
        }

        // ---- 本地输入 → player:input（C2S，§11.7 Client 唯一出口） ----

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

        // ---- 第一人称相机（跟本地玩家复制实体） ----

        private void DriveCamera()
        {
            if (_camera is null) return;
            var world = PlayerMod.World;
            if (PlayerMod.LocalPlayerEntityId == 0) return;

            var local = new Entity(PlayerMod.LocalPlayerEntityId);
            if (!world.TryGet<Position3>(local, out var pos)) return;

            _camera.transform.position = new Vector3(pos.X, pos.Y + 0.6f, pos.Z);
            _camera.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void OnGUI()
        {
            var world = PlayerMod.World;
            if (world is null || PlayerMod.LocalPlayerEntityId == 0) return;
            var local = new Entity(PlayerMod.LocalPlayerEntityId);
            if (world.TryGet<Health>(local, out var hp))
                GUI.Label(new Rect(20, 20, 300, 30), $"HP: {hp.Current} / {hp.Max}");
            GUI.Label(new Rect(20, 44, 500, 30), "WASD 移动 / 空格跳 / 鼠标视角（点击锁定）/ Esc 释放光标");
        }
    }
}
