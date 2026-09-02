using Game.ECS;
using Game.Mod.Contract;
using Game.Mod.Runtime;
using UnityEngine;

namespace Com.Zombtoy.Weapon
{
    /// <summary>
    /// 武器视图（MonoBehaviour 表现层，不参与无头测试，CONTRACT.md §8）：
    /// - Fire1 按下 → Physics.Raycast（Shootable 层，当前武器 Range）→ 命中 collider →
    ///   经 IEntityView（框架视图映射接口，Rule 12：零跨 Mod 类型引用）取 EnemyView.EntityId（未命中=0）
    ///   → 静态桥写 ShotRequest（含命中点/射程终点）→ FireSystem 校验 + 调 enemy:damage（契约 §3/§8）
    /// - 换弹 R 键 / 弹匣打空 → 静态桥写 WeaponAmmo.IsReloading=true + ReloadTimer=ReloadTime（契约 §3 注，
    ///   ReloadSystem 计时补弹，无耦合）
    /// - 1-4 键 → 静态桥写 SwitchRequest（WeaponSwitchSystem 校验/发布）
    /// - Fire2 → 静态桥写 TornadoRequest（副手，ProjectileSystem 生成 Tornado，M2，契约 §8）
    /// - 表现：LineRenderer 弹道线、枪口粒子/枪光、4 槽切换 UI（GunText）、HUD 弹药（AmmoChanged/WeaponSwitched 订阅）
    /// 权威判定全部在系统内（FireSystem/ProjectileSystem），本视图只写输入与表现。
    /// </summary>
    public sealed class WeaponView : MonoBehaviour
    {
        /// <summary>Shootable 层（命中判定，原版 LayerMask.GetMask("Shootable")）。在 Awake 计算（Unity 禁止字段初始化调 NameToLayer）。</summary>
        private int _shootableMask;

        private Camera? _camera;
        private ParticleSystem? _gunParticles;
        private LineRenderer? _gunLine;
        private Light? _gunLight;
        private AudioSource? _gunAudio;
        private float _effectsTimer;
        private GameObject? _heldWeapon;

        // ---- 原枪模资源 AssetId（Rule 3：经本 Mod context.Resources.Load；路径 = Mod 目录内相对路径去扩展名） ----
        // 槽位映射（契约 §9）：0 Machine Gun / 1 Shotgun / 2 Rocket Launcher / 3 Cryo Pistol
        // Cryo Pistol 无独立模型 → 用 Guns/MultiShot 多管枪占位；加载失败回退（不显示枪模，不影响逻辑）
        private static readonly AssetId[] GunPrefabIds =
        {
            new(WeaponMod.ModIdValue, "Guns/Machine Gun"),
            new(WeaponMod.ModIdValue, "Guns/Shotgun 1"),
            new(WeaponMod.ModIdValue, "RocketLauncher"),
            new(WeaponMod.ModIdValue, "Guns/MultiShot"),
        };

        // 表现（4 槽枪模 + UI，Inspector 可挂载，缺省走资源加载）
        public GameObject[] WeaponPrefabs = new GameObject[WeaponConfig.SlotCount];
        public string[] WeaponNames = { "Machine Gun", "Shotgun", "Rocket Launcher", "Cryo Pistol" };
        public UnityEngine.UI.Text? GunText;
        public UnityEngine.UI.Text? AmmoText;

        private void Awake()
        {
            _shootableMask = LayerMask.GetMask("Shootable");
            if (_shootableMask == 0) _shootableMask = ~0; // 工程未配置 Shootable 层（防御）：命中任意层
            _camera = GetComponentInChildren<Camera>() ?? Camera.main;
            _gunParticles = GetComponent<ParticleSystem>();
            // 注意：GetComponent 对内置组件可能返回 fake-null（is null/?? 不识别），必须用 Unity == null 判断
            _gunLine = GetComponent<LineRenderer>();
            if (_gunLine == null) _gunLine = gameObject.AddComponent<LineRenderer>(); // 自建弹道线（不依赖 prefab）
            ConfigureLine();
            _gunLight = GetComponent<Light>();
            if (_gunLight == null) _gunLight = gameObject.AddComponent<Light>();       // 自建枪口光（不依赖 prefab）
            ConfigureLight();
            _gunAudio = GetComponent<AudioSource>();
        }

        /// <summary>弹道线配置（LineRenderer：2 点折线 + 黄色细线，原版 PlayerShooting 表现形态）。</summary>
        private void ConfigureLine()
        {
            if (_gunLine == null) return;
            _gunLine.positionCount = 2;
            _gunLine.startWidth = 0.03f;
            _gunLine.endWidth = 0.03f;
            _gunLine.startColor = new Color(1f, 0.95f, 0.4f);
            _gunLine.endColor = new Color(1f, 0.7f, 0.2f);
            _gunLine.numCapVertices = 4;
            _gunLine.material = StandardMaterial(new Color(1f, 0.9f, 0.4f));
            _gunLine.enabled = false;
        }

        /// <summary>枪口光配置（Point 点光，橙色枪口闪光，开火时短暂点亮）。</summary>
        private void ConfigureLight()
        {
            if (_gunLight == null) return;
            _gunLight.type = LightType.Point;
            _gunLight.range = 8f;
            _gunLight.intensity = 1.6f;
            _gunLight.color = new Color(1f, 0.75f, 0.35f);
            _gunLight.enabled = false;
        }

        private static Material StandardMaterial(Color color)
        {
            var shader = Shader.Find("Standard");
            var mat = shader != null ? new Material(shader) : new Material(Shader.Find("Diffuse"));
            mat.color = color;
            return mat;
        }

        private void Start()
        {
            // 订阅本 Mod 消息（HUD 弹药 / 武器名；视图只读表现，不参与权威逻辑）
            var ctx = WeaponMod.Context;
            if (ctx is not null)
            {
                ctx.Messages.Subscribe(WeaponMod.AmmoChangedEvent, (in Game.Messaging.MessageEnvelope _, Game.Mod.Contract.Wire.PayloadReader reader) =>
                {
                    if (AmmoText is null) return;
                    if (!reader.TryReadUInt32(1, out var slot)) return;
                    if (slot != CurrentSlot()) return; // 只显示当前槽
                    if (!reader.TryReadInt32(2, out var inMag)) return;
                    if (!reader.TryReadInt32(3, out var reserve)) return;
                    AmmoText.text = $"{inMag}/{reserve}";
                });
                ctx.Messages.Subscribe(WeaponMod.WeaponSwitchedEvent, (in Game.Messaging.MessageEnvelope _, Game.Mod.Contract.Wire.PayloadReader reader) =>
                {
                    if (reader.TryReadUInt32(1, out var slot))
                    {
                        if (GunText != null && slot < WeaponNames.Length)
                            GunText.text = WeaponNames[slot];
                        ShowWeapon((int)slot); // 枪模展示（契约 §8；不依赖 GunText 是否存在）
                    }
                });
            }
            RefreshHud(); // 初始填充（weapon:get_state，契约 §4）
        }

        private void Update()
        {
            var ctx = WeaponMod.Context;
            if (ctx is null || WeaponMod.World is null || WeaponMod.ActiveEntityId == 0) return;

            if (PlayerDead()) return; // 玩家死亡不开火/不切枪（对齐原版 Inventory/PlayerShooting）

            // 1-4 键切换（契约 §8）
            for (var i = 0; i < WeaponConfig.SlotCount; i++)
            {
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)))
                    WriteSwitch((byte)i);
            }

            // R 键换弹（契约 §8：写换弹请求；ReloadSystem 计时补弹）
            if (Input.GetKeyDown(KeyCode.R)) StartReload();

            // Fire1 开火（按住连发；权威校验/冷却/弹药在 FireSystem，契约 §3）
            if (Input.GetButton("Fire1")) Fire();

            // Fire2 龙卷风（副手，M2，契约 §8）
            if (Input.GetButtonDown("Fire2")) WriteTornado();

            // 弹匣打空自动进入换弹（视图负责，契约 §3 注）
            if (TryGetCurrentAmmo(out var ammo) && ammo.InMag == 0 && !ammo.IsReloading && ammo.Reserve > 0)
                StartReload();

            // 特效计时（枪光/弹道线消逝，对齐原版 DisableEffects）
            _effectsTimer -= Time.deltaTime;
            if (_effectsTimer <= 0f)
            {
                if (_gunLine is not null) _gunLine.enabled = false;
                if (_gunLight is not null) _gunLight.enabled = false;
            }
        }

        // ---- 输入落地（静态桥，契约 §8） ----

        private void Fire()
        {
            var current = CurrentSlot();
            if (current >= WeaponConfig.SlotCount) return;
            var def = WeaponConfig.Slots[current];

            var origin = MuzzleOrigin();
            var dir = _camera is not null ? _camera.transform.forward : transform.forward;

            uint hitEntity = 0;
            var hitPoint = origin + dir * def.Range;
            var isHit = false;
            if (Physics.Raycast(origin, dir, out var hit, def.Range, _shootableMask))
            {
                hitPoint = hit.point;
                isHit = true;
                // 跨 Mod 视图映射（Rule 12）：经框架 IEntityView 读目标实体 id，零类型引用/反射/全局发现
                foreach (var mb in hit.collider.GetComponentsInParent<MonoBehaviour>())
                {
                    if (mb is IEntityView view) { hitEntity = view.EntityId; break; }
                }
            }

            // 视图写射击请求（含命中点；ShooterId 由 FireSystem 回填 WeaponOwner）
            var domain = new Entity(WeaponMod.ActiveEntityId);
            WeaponMod.World.Add(domain, new ShotRequest
            {
                ShooterId = 0,
                HitEntityId = hitEntity,
                IsHit = isHit,
                HitX = hitPoint.x, HitY = hitPoint.y, HitZ = hitPoint.z,
                Kind = (byte)def.Kind,
            });

            // 表现：弹道线 / 枪口粒子 / 枪光 / 音效（原版 PlayerShooting 表现）
            _effectsTimer = 0.2f;
            if (_gunLine is not null)
            {
                _gunLine.enabled = true;
                _gunLine.SetPosition(0, origin);
                _gunLine.SetPosition(1, hitPoint);
            }
            if (_gunParticles is not null) { _gunParticles.Stop(); _gunParticles.Play(); }
            if (_gunLight is not null)
            {
                _gunLight.transform.position = origin; // 枪口位置（玩家前方射线原点）
                _gunLight.enabled = true;
            }
            if (_gunAudio is not null) _gunAudio.Play();
        }

        private void WriteSwitch(byte index)
        {
            var domain = new Entity(WeaponMod.ActiveEntityId);
            WeaponMod.World.Add(domain, new SwitchRequest { Index = index });
        }

        private void StartReload()
        {
            var current = CurrentSlot();
            if (current >= WeaponConfig.SlotCount) return;
            var slotEntity = new Entity(WeaponMod.SlotEntityIds[current]);
            if (!WeaponMod.World.TryGet<WeaponAmmo>(slotEntity, out var ammo)) return;
            if (ammo.IsReloading || ammo.InMag >= ammo.MaxMag || ammo.Reserve <= 0) return;
            ammo.IsReloading = true;
            ammo.ReloadTimer = WeaponConfig.Slots[current].ReloadTime;
            WeaponMod.World.Add(slotEntity, ammo);
        }

        private void WriteTornado()
        {
            var origin = MuzzleOrigin();
            var dir = _camera is not null ? _camera.transform.forward : transform.forward;
            var domain = new Entity(WeaponMod.ActiveEntityId);
            WeaponMod.World.Add(domain, new TornadoRequest
            {
                X = origin.x + dir.x * 2f,
                Y = origin.y,
                Z = origin.z + dir.z * 2f,
                DirX = dir.x,
                DirZ = dir.z,
            });
        }

        // ---- 表现辅助 ----

        /// <summary>展示当前槽枪模：优先 Inspector WeaponPrefabs，缺省经 context.Resources.Load 加载原枪模（Rule 3）。</summary>
        private void ShowWeapon(int index)
        {
            if (_heldWeapon is not null) Destroy(_heldWeapon);
            if (index < 0 || index >= WeaponPrefabs.Length) return;
            var gun = WeaponPrefabs[index] != null ? WeaponPrefabs[index] : LoadGun(index);
            if (gun is null) return; // 原枪模缺失：不显示（不影响权威逻辑）
            _heldWeapon = Instantiate(gun);
            if (_heldWeapon is not null && _camera is not null)
            {
                _heldWeapon.transform.SetParent(_camera.transform, false);
                _heldWeapon.transform.localPosition = new Vector3(0.25f, -0.2f, 0.5f);
            }
        }

        /// <summary>经 context.Resources.Load 加载原枪模 prefab（Rule 3；失败返回 null，调用方不显示）。</summary>
        private static GameObject? LoadGun(int index)
        {
            if (index < 0 || index >= GunPrefabIds.Length) return null;
            var ctx = WeaponMod.Context;
            if (ctx is null) return null;
            GameObject? gun = null;
            try { gun = ctx.Resources.Load(GunPrefabIds[index]) as GameObject; }
            catch (System.Exception e) { Debug.LogWarning($"[WeaponView] 枪模加载异常 {GunPrefabIds[index]}: {e.Message}"); gun = null; }
            if (gun is null) Debug.LogWarning($"[WeaponView] 枪模缺失（{GunPrefabIds[index]}）→ 不显示（不影响权威逻辑）");
            else Debug.Log($"[WeaponView] 枪模已加载 slot={index} → {GunPrefabIds[index]}");
            return gun;
        }

        private void RefreshHud()
        {
            var ctx = WeaponMod.Context;
            if (ctx is null) return;
            // weapon:get_state（二进制 ModCall，Rule 14）：初始填充 HUD 弹药与武器名
            var buf = ctx.Mods.Call(WeaponMod.ModIdValue, WeaponMod.GetStateCap,
                Game.Mod.Contract.Wire.DataCodec.Write(null));
            var row = Game.Mod.Contract.Wire.DataCodec.Read(new Game.Mod.Contract.Wire.PayloadReader(buf));
            if (row.Length >= 5 && row[0] is uint slot && row[2] is int inMag && row[3] is int reserve)
            {
                if (AmmoText is not null) AmmoText.text = $"{inMag}/{reserve}";
                if (GunText != null && slot < WeaponNames.Length)
                    GunText.text = WeaponNames[slot];
                ShowWeapon((int)slot); // 初始枪模展示（契约 §8：weapon:get_state 后挂原枪模；不依赖 GunText）
            }
        }

        private uint CurrentSlot()
        {
            var domain = new Entity(WeaponMod.ActiveEntityId);
            if (WeaponMod.World is null || !WeaponMod.World.TryGet<WeaponActive>(domain, out var active)) return 0;
            return active.CurrentIndex;
        }

        private bool TryGetCurrentAmmo(out WeaponAmmo ammo)
        {
            ammo = default;
            var current = CurrentSlot();
            if (current >= WeaponConfig.SlotCount) return false;
            var slotEntity = new Entity(WeaponMod.SlotEntityIds[current]);
            return WeaponMod.World is not null && WeaponMod.World.TryGet<WeaponAmmo>(slotEntity, out ammo);
        }

        /// <summary>玩家死亡判定（视图层 UX 守卫；权威判定在 FireSystem，契约 §3 序 1）。取不到按死亡处理。</summary>
        private bool PlayerDead()
        {
            var ctx = WeaponMod.Context;
            if (ctx is null) return false;
            var pos = WeaponMod.TryGetPlayerPosition(ctx);
            return pos is null || !pos.Value.Alive;
        }

        private Vector3 MuzzleOrigin()
        {
            // 枪口原点（对齐 FireSystem：玩家位置 + 枪口高度）；取不到用相机
            var ctx = WeaponMod.Context;
            if (ctx is not null && WeaponMod.PlayerEntityId != 0)
            {
                var pos = WeaponMod.TryGetPlayerPosition(ctx);
                if (pos is not null) return new Vector3(pos.Value.X, pos.Value.Y + WeaponConfig.MuzzleHeight, pos.Value.Z);
            }
            return _camera is not null ? _camera.transform.position : transform.position;
        }
    }
}
