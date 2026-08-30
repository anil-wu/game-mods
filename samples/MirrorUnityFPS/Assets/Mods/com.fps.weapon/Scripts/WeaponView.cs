using System.Collections.Generic;
using Game.Mod.Contract;
using UnityEngine;

namespace Com.Fps.Weapon
{
    /// <summary>
    /// 武器视图（忠实资源版）：第一人称枪模（经所属 Mod ResourceScope 加载）+ 准星 + 弹药 HUD + 弹道线。
    /// 表现只读复制/事件状态，权威判定在 Server（§11.10）。
    /// </summary>
    public sealed class WeaponView : MonoBehaviour
    {
        private static readonly FireProtocol FireProto = new();
        private static readonly ReloadProtocol ReloadProto = new();
        private static readonly AssetId FpsPistol = new(WeaponMod.ModIdValue,
            "Content/Weapon/Range/Pistol/Prefab/Pistol FPS Local");

        private float _clientFireCooldown;
        private long _seenShotTicks;
        private GameObject? _heldWeapon;
        private readonly List<(LineRenderer Line, float Ttl)> _tracers = new();

        private void Start()
        {
            try
            {
                if (WeaponMod.Context?.Resources.Load(FpsPistol) is GameObject prefab)
                {
                    _heldWeapon = Instantiate(prefab);
                    NormalizeHeight(_heldWeapon, 0.55f); // 枪+手第一人称合理尺寸（原 2.4m 过大）
                }
            }
            catch (System.Exception) { }
        }

        /// <summary>按包围盒高度归一化缩放（不同 FBX 导入缩放差异巨大）。</summary>
        private static void NormalizeHeight(GameObject go, float targetHeight)
        {
            var bounds = new Bounds(go.transform.position, Vector3.zero);
            var any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) { bounds.Encapsulate(r.bounds); any = true; }
            if (!any || bounds.size.y <= 0.0001f) return;
            var scale = targetHeight / bounds.size.y;
            go.transform.localScale = Vector3.Scale(go.transform.localScale, new Vector3(scale, scale, scale));
        }

        private void LateUpdate()
        {
            // 枪模挂到相机下（第一人称）
            if (_heldWeapon is not null && Camera.main is not null)
            {
                _heldWeapon.transform.SetParent(Camera.main.transform, false);
                _heldWeapon.transform.localPosition = new Vector3(0.25f, -0.2f, 0.5f);
            }
        }

        private void Update()
        {
            var ctx = WeaponMod.Context;
            if (ctx is null || !ctx.Network.IsActive) return;

            _clientFireCooldown -= Time.deltaTime;

            // 开火（按住连发，客户端节流；服务端冷却为准）
            if (Input.GetMouseButton(0) && Cursor.lockState == CursorLockMode.Locked && _clientFireCooldown <= 0f)
            {
                _clientFireCooldown = 0.15f;
                var yaw = Camera.main is not null ? Camera.main.transform.eulerAngles.y : 0f;
                ctx.Network.SendToServer(FireProto.Id, new FireRequest(yaw));
            }

            // 换弹
            if (Input.GetKeyDown(KeyCode.R))
                ctx.Network.SendToServer(ReloadProto.Id, ReloadRequest.Instance);

            // 弹道线（事件驱动，0.1s 消逝）
            if (WeaponMod.LastShot is { } shot && WeaponMod.LastShotTicks != _seenShotTicks)
            {
                _seenShotTicks = WeaponMod.LastShotTicks;
                SpawnTracer(shot);
            }
            UpdateTracers();
        }

        private void SpawnTracer(in ShotEvent shot)
        {
            var go = new GameObject("Tracer");
            var line = go.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.startWidth = 0.03f;
            line.endWidth = 0.01f;
            line.material = new Material(Shader.Find("Sprites/Default"))
            {
                color = shot.HitNetId != 0 ? Color.red : Color.yellow,
            };

            // 起点：射手位置（经能力调用，Rule 12 零跨 Mod 类型引用；取不到则用相机）
            var start = Camera.main is not null ? Camera.main.transform.position : Vector3.zero;
            var ctx = WeaponMod.Context!;
            if (ctx.Network.Replication.TryGetEntity(shot.ShooterNetId, out var shooterEntity))
            {
                var buf = ctx.Mods.Call(WeaponMod.PlayerModId, WeaponMod.GetPositionCap,
                    Game.Mod.Contract.Wire.DataCodec.Write(new object?[] { shooterEntity.Id }));
                var row = Game.Mod.Contract.Wire.DataCodec.Read(new Game.Mod.Contract.Wire.PayloadReader(buf));
                if (row.Length >= 4 &&
                    row[1] is float x && row[2] is float y && row[3] is float z)
                    start = new Vector3(x, y + WeaponConfig.EyeHeight, z);
            }

            line.SetPosition(0, start);
            line.SetPosition(1, new Vector3(shot.HitX, shot.HitY, shot.HitZ));
            _tracers.Add((line, 0.1f));
        }

        private void UpdateTracers()
        {
            for (var i = _tracers.Count - 1; i >= 0; i--)
            {
                var (line, ttl) = _tracers[i];
                ttl -= Time.deltaTime;
                if (ttl <= 0f)
                {
                    if (line is not null) Destroy(line.gameObject);
                    _tracers.RemoveAt(i);
                }
                else
                {
                    _tracers[i] = (line, ttl);
                }
            }
        }

        private void OnGUI()
        {
            // 准星
            var cx = Screen.width / 2f;
            var cy = Screen.height / 2f;
            GUI.Label(new Rect(cx - 6, cy - 10, 20, 20), "+");

            // 弹药 HUD（本地玩家武器状态）
            var localNetId = LocalNetId();
            if (localNetId != 0 && WeaponMod.ClientWeaponStates.TryGetValue(localNetId, out var state))
            {
                GUI.Label(new Rect(Screen.width - 160, Screen.height - 60, 150, 30),
                    state.Reloading ? "换弹中…" : $"弹药: {state.Ammo} / {state.MaxAmmo}");
            }

            // 命中反馈
            if (WeaponMod.LastShot is { Killed: true } &&
                (System.DateTime.UtcNow.Ticks - WeaponMod.LastShotTicks) < 1_000_000) // 0.1s
            {
                GUI.Label(new Rect(cx - 40, cy + 20, 120, 24), "击 杀 !");
            }
        }

        /// <summary>本地玩家 NetworkId（经能力调用 + Replication 映射，零跨 Mod 静态引用）。</summary>
        private static uint LocalNetId()
        {
            var ctx = WeaponMod.Context;
            if (ctx is null) return 0u;
            var entityId = WeaponMod.GetLocalShooterEntityId(ctx);
            if (entityId == 0) return 0u;
            return ctx.Network.Replication.TryGetNetworkId(new Game.ECS.Entity(entityId), out var netId) ? netId : 0u;
        }
    }
}
