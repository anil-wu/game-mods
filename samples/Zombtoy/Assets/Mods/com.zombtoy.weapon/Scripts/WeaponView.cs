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
    /// - 表现：原枪 prefab 自带特效组件（复刻宪法 §0：对齐原版 PlayerShooting 在枪 GO 上
    ///   GetComponent<ParticleSystem/LineRenderer/Light/AudioSource> 并直接用），
    ///   Fire() 驱动它们播放/划线/点亮；4 槽切换 UI（GunText）、HUD 弹药（AmmoChanged/WeaponSwitched 订阅）
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

        // 枪口粒子材质/贴图防御（原枪 prefab 自带粒子确实断链/丢贴图时才按名补链，Rule 3；
        // 注意：.meta GUID 会被 Unity 重导覆盖，AssetId 必须带扩展名 + mat.SetTexture 按名补链）。
        // 4 槽枪模枪口粒子 PSR 用的材质是 FlareParticleMaterial（原贴图 = Unity 内置白，非工程资产 → 补链专案）；
        // MuzzleFlash 材质/贴图仅作「材质槽整槽断链（null）」时的按名回退（现有行为，原版资源，日志声明）。
        private static readonly AssetId MuzzleFlashMat = new(WeaponMod.ModIdValue,
            "Imported/EffectTexturesAndPrefabs/Materials/MuzzleFlash.mat");
        private static readonly AssetId MuzzleFlashTex = new(WeaponMod.ModIdValue,
            "Imported/EffectTexturesAndPrefabs/Textures/MuzzleFlash.tga");

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
            // 不再自建 LineRenderer/Light/ParticleSystem/AudioSource（复刻宪法 §0：禁自建近似物）——
            // 特效组件在 ShowWeapon 实例化原枪 prefab 后 GetComponent 绑定（BindWeaponEffects，原版 PlayerShooting 用法）
        }

        /// <summary>
        /// 从 _heldWeapon（原枪 prefab 实例）取它自带的特效组件（对齐原版 PlayerShooting.Awake
        /// 在枪 GO 上 GetComponent&lt;ParticleSystem/LineRenderer/Light/AudioSource&gt;）。
        /// Machine Gun / Shotgun 1 特效在根 GO；MultiShot（Cryo 占位枪）特效在枪管子树，
        /// 故用 GetComponentInChildren（自含子树，前三个槽行为与 GetComponent 一致，Rule 13 无跨边界）。
        /// 每次换枪重新绑定（特效随枪实例走）；枪模确缺/该槽枪无某组件时为 null，Fire 里按 Unity == null 跳过。
        /// </summary>
        private void BindWeaponEffects(int slot)
        {
            if (_heldWeapon == null) return;
            // 注意：GetComponent(InChildren) 对内置组件可能返回 fake-null（is null/?? 不识别），必须用 Unity == null 判断
            _gunParticles = _heldWeapon.GetComponentInChildren<ParticleSystem>();
            _gunLine = _heldWeapon.GetComponentInChildren<LineRenderer>();
            _gunLight = _heldWeapon.GetComponentInChildren<Light>();
            _gunAudio = _heldWeapon.GetComponentInChildren<AudioSource>();
            Debug.Log($"[WeaponView] 特效组件来自原枪 prefab slot={slot}: " +
                      $"ParticleSystem={_gunParticles != null} LineRenderer={_gunLine != null} " +
                      $"Light={_gunLight != null} AudioSource={_gunAudio != null}");
            // 枪口火花贴图验证 + 按名补链（含 inactive 粒子渲染体；原材质完好则不干预）
            RelinkGunParticleTextures();
        }

        /// <summary>
        /// 枪口火花贴图验证 + 按名补链（原枪 prefab 实例粒子方块防御；复刻宪法允许的原版资源缺失回退，日志声明）。
        /// 遍历 _heldWeapon 全部 ParticleSystemRenderer（GetComponentsInChildren(true) **含 inactive**——换枪隐藏/未激活的
        /// 枪口粒子一并覆盖，参考 enemy 粒子补链先例）逐材质槽检查：
        /// - 贴图完好（mainTexture 非空）不干预；
        /// - 断链（mainTexture==null → 粒子渲染成方块）按材质 m_Name → 原贴图表补链（见
        ///   RelinkMuzzleTextureByMaterialName：FlareParticleMaterial 为现 4 槽枪口粒子唯一材质，原贴图 = Unity
        ///   内置白 → 以 Texture2D.whiteTexture 还原，即原版资源非自建近似物）；
        /// - 材质槽整槽断链（null，资产缺失）→ 回退加载 MuzzleFlash.mat 整槽替换（现有行为，日志声明）。
        /// 补链写共享材质实例：同材质多个渲染体/后续换枪一并恢复（幂等——已修/完好直接跳过）。
        /// </summary>
        private void RelinkGunParticleTextures()
        {
            var ctx = WeaponMod.Context;
            if (_heldWeapon == null || ctx == null) return; // 未注册/无枪实例（防御）
            var renderers = _heldWeapon.GetComponentsInChildren<ParticleSystemRenderer>(true);
            var relinked = 0;
            Material? fallback = null;
            foreach (var psr in renderers)
            {
                if (psr == null) continue; // fake-null（组件随旧枪实例销毁）
                var mats = psr.sharedMaterials;
                var touched = false;
                for (var i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) // 注意：Unity 对象必须用重载 == null 判（fake-null 不认 is null）
                    {
                        // 材质槽整槽断链（资产缺失 → 渲染方块）：按名回退 MuzzleFlash 材质整槽替换（原版资源）
                        if (fallback == null) fallback = LoadMuzzleFallbackMaterial(ctx);
                        if (fallback == null)
                        {
                            Debug.LogWarning("[WeaponView] 枪口粒子材质槽断链且按名回退失败 → 保持原样（该槽枪口粒子可能不显示）");
                            continue;
                        }
                        mats[i] = fallback;
                        touched = true;
                        relinked++;
                        Debug.LogWarning($"[WeaponView] 枪口粒子材质槽断链 → 按名回退 {MuzzleFlashMat.Path}（原版资源缺失回退，日志声明）");
                        continue;
                    }
                    if (mat.mainTexture != null) continue; // 原枪粒子材质/贴图完好：不干预
                    if (!RelinkMuzzleTextureByMaterialName(mat)) continue; // 非表内材质：保持原样
                    relinked++;
                }
                if (touched) psr.sharedMaterials = mats; // 整槽替换需提交（SetTexture 已写共享材质实例）
            }
            if (relinked > 0)
                Debug.LogWarning($"[WeaponView] 枪口火花贴图断链 → 按名补链 {relinked} 个枪口粒子渲染体材质槽（原版资源缺失回退，日志声明）");
            else
                Debug.Log($"[WeaponView] 枪口火花贴图验证：{renderers.Length} 个枪口粒子渲染体（含 inactive）材质/贴图完好，无需补链");
        }

        /// <summary>按材质名补链枪口粒子贴图（表：材质 m_Name → 原贴图，参考 enemy 粒子表先例）。
        /// FlareParticleMaterial（Machine Gun / Shotgun 1 / MultiShot 枪口粒子唯一材质，原版工程 .mat 的 _MainTex
        /// = Unity 内置白贴图，无工程纹理资产）→ whiteTexture 还原（即原贴图）；其余表内工程贴图（AssetId 带
        /// 扩展名精确命中，Rule 3）按名加载补链。返回是否已补链。</summary>
        private static bool RelinkMuzzleTextureByMaterialName(Material mat)
        {
            switch (mat.name)
            {
                case "FlareParticleMaterial":
                    mat.SetTexture("_MainTex", Texture2D.whiteTexture);
                    Debug.Log("[WeaponView] 枪口火花贴图已按名补链 FlareParticleMaterial → 内置白贴图（原版 .mat _MainTex，日志声明）");
                    return true;
                default:
                    return false; // 非枪口粒子材质（本 Mod 未用项不进表，同 enemy 表原则）：不干预保持原样
            }
        }

        /// <summary>材质槽断链时按名回退加载 MuzzleFlash 材质并补其原贴图（Rule 3；AssetId 带扩展名精确命中）。</summary>
        private static Material? LoadMuzzleFallbackMaterial(IModContext ctx)
        {
            Material? fallback = null;
            Texture? tex = null;
            try
            {
                fallback = ctx.Resources.Load(MuzzleFlashMat) as Material;
                tex = ctx.Resources.Load(MuzzleFlashTex) as Texture;
            }
            catch (System.Exception) { fallback = null; tex = null; }
            if (fallback != null && tex != null)
                fallback.SetTexture("_MainTex", tex); // 材质内 _MainTex GUID 断链时按名补链（幂等）
            return fallback;
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

            // 批处理截图/冒烟（无人按键）：自动演示三类投射物（火箭筒/冰手枪/龙卷风），供 QA 截图窗口内
            // 真实生成——日志确认加载原 Rocket/IceBullet/Tornado prefab + 画面可见；真实游玩不受影响
            // （对齐 MenuView 批处理自动开局先例）。
            if (Application.isBatchMode) BatchDemoDrive();

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
                // Unity == null 语义（组件可能随旧枪实例销毁成 fake-null）
                if (_gunLine != null) _gunLine.enabled = false;
                if (_gunLight != null) _gunLight.enabled = false;
            }
        }

        // ---- 输入落地（静态桥，契约 §8） ----

        private void Fire() => Fire(null, 0u);

        /// <summary>开火（aimed 为空 = 真人/编辑器相机射线瞄准；批处理演示 aim = 最近存活敌人落点，
        /// hitEnemy = 该敌人实体（射线武器直伤用；火箭/冰弹不依赖，系统自行追踪/命中判定））。</summary>
        private void Fire(Vector3? aimed, uint hitEnemy)
        {
            var current = CurrentSlot();
            if (current >= WeaponConfig.SlotCount) return;
            var def = WeaponConfig.Slots[current];

            var origin = MuzzleOrigin();
            var dir = _camera is not null ? _camera.transform.forward : transform.forward;
            if (aimed.HasValue)
            {
                dir = aimed.Value - origin;
                if (dir.sqrMagnitude < 0.0001f)
                    dir = _camera is not null ? _camera.transform.forward : transform.forward;
                else dir.Normalize();
            }

            uint hitEntity = 0;
            var hitPoint = origin + dir * def.Range;
            var isHit = false;
            if (!aimed.HasValue)
            {
                // 真人路径：Physics.Raycast（Shootable 层，当前武器 Range）→ 命中 collider →
                // 经 IEntityView 取 EnemyView.EntityId（未命中=0）
                if (Physics.Raycast(origin, dir, out var hit, def.Range, _shootableMask))
                {
                    hitPoint = hit.point;
                    isHit = true;
                    foreach (var mb in hit.collider.GetComponentsInParent<MonoBehaviour>())
                    {
                        if (mb is IEntityView view) { hitEntity = view.EntityId; break; }
                    }
                }
            }
            else
            {
                // 批处理演示路径：直接以目标敌人为命中点（HitEntityId 供射线武器直伤）
                hitPoint = aimed.Value;
                isHit = hitEnemy != 0;
                hitEntity = hitEnemy;
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

            // 表现：弹道线 / 枪口粒子 / 枪光 / 音效——此时组件来自原枪 prefab 实例（BindWeaponEffects），
            // 调用形态对齐原版 PlayerShooting.Shoot（Stop/Play/SetPosition/enabled），缺组件按 Unity == null 跳过
            _effectsTimer = 0.2f;
            if (_gunLine != null)
            {
                _gunLine.enabled = true;
                _gunLine.SetPosition(0, origin);
                _gunLine.SetPosition(1, hitPoint);
            }
            if (_gunParticles != null) { _gunParticles.Stop(); _gunParticles.Play(); }
            if (_gunLight != null)
            {
                _gunLight.transform.position = origin; // 枪口位置（玩家前方射线原点）
                _gunLight.enabled = true;
            }
            if (_gunAudio != null) _gunAudio.Play();
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

        // ---- 批处理截图/冒烟自动发射驱动（仅 Application.isBatchMode） ----
        private float _batchElapsed;
        private bool _batchTornadoDone, _batchRocketDone, _batchCryoDone, _batchBackDone;

        /// <summary>
        /// 批处理（截图验收，无人按键）自动演示三类投射物，对齐 MenuView 批处理自动开局先例：
        /// 按**游戏时间**排程（截图窗口 10s：开局 3s 首批刷敌后）——
        /// 3.2s 副手龙卷风（Fire2，一次性）→ 3.8s 切火箭筒（槽 2）3.9~4.7s 按住发射（1s 冷却→1 发）→
        /// 5.2s 切冰手枪（槽 3）5.3~6.4s 按住发射（0.4s 冷却→2~3 发）→ 7s 复位机关枪（槽 0）。
        /// 瞄准取最近存活敌人（enemy:get_all，二进制 ModCall Rule 14，形状见 enemy CONTRACT §4）；
        /// 无敌人时相机前方发射。所有开火走真实 ShotRequest/TornadoRequest → 系统权威逻辑（契约 §3），
        /// 真实游玩（非批处理）不受影响。
        /// </summary>
        private void BatchDemoDrive()
        {
            if (_batchBackDone) return;
            _batchElapsed += Time.deltaTime;
            var t = _batchElapsed;

            if (!_batchTornadoDone && t >= 3.2f)
            {
                _batchTornadoDone = true;
                WriteTornado(); // Fire2 → TornadoRequest → ProjectileSystem 生成 Tornado（M2）
            }

            if (!_batchRocketDone && t >= 3.8f)
            {
                _batchRocketDone = true;
                WriteSwitch(2); // 火箭筒（槽 2）
            }
            if (_batchRocketDone && !_batchCryoDone && t >= 3.9f && t <= 4.7f)
            {
                var aim = BatchAimTarget();
                if (aim.HasValue) Fire(aim.Value.Point, aim.Value.Id);
                else Fire(); // 无敌人：相机前向发射（火箭超时自毁，逻辑照常）
            }

            if (!_batchCryoDone && t >= 5.2f)
            {
                _batchCryoDone = true;
                WriteSwitch(3); // 冰手枪（槽 3）
            }
            if (_batchCryoDone && !_batchBackDone && t >= 5.3f && t <= 6.4f)
            {
                var aim = BatchAimTarget();
                if (aim.HasValue) Fire(aim.Value.Point, aim.Value.Id);
                else Fire();
            }

            if (!_batchBackDone && t >= 7.0f)
            {
                _batchBackDone = true;
                WriteSwitch(0); // 演示结束复位机关枪（契约 §8 槽位）
            }
        }

        /// <summary>最近存活敌人瞄准点（enemy:get_all 行解析，Rule 19 各自重复定义；失败返回 null）。</summary>
        private (Vector3 Point, uint Id)? BatchAimTarget()
        {
            var ctx = WeaponMod.Context;
            if (ctx is null) return null;
            try
            {
                var buf = ctx.Mods.Call(WeaponMod.EnemyModId, WeaponMod.EnemyGetAllCap,
                    Game.Mod.Contract.Wire.DataCodec.Write(null));
                var rows = Game.Mod.Contract.Wire.DataCodec.Read(new Game.Mod.Contract.Wire.PayloadReader(buf));
                var origin = MuzzleOrigin();
                Vector3 best = default;
                var bestId = 0u;
                var bestD = float.MaxValue;
                foreach (var row in rows)
                {
                    if (!CapabilityShapes.TryReadGetAllRow(row, out var id, out var x, out var y, out var z,
                            out _, out var alive, out _, out _)) continue;
                    if (!alive) continue;
                    var d = (new Vector3(x, y, z) - origin).sqrMagnitude;
                    if (d < bestD && d < 70f * 70f) { bestD = d; best = new Vector3(x, y, z); bestId = id; }
                }
                if (bestId == 0u) return null;
                return (best, bestId);
            }
            catch (System.Exception)
            {
                return null; // enemy 未加载（卸载竞态）：本次无目标 → 相机前向发射
            }
        }

        // ---- 表现辅助 ----

        /// <summary>展示当前槽枪模：优先 Inspector WeaponPrefabs，缺省经 context.Resources.Load 加载原枪模（Rule 3）。</summary>
        private void ShowWeapon(int index)
        {
            if (_heldWeapon is not null) Destroy(_heldWeapon);
            // 解绑旧实例特效（旧枪已销毁，置空避免 fake-null 引用；新枪实例化后重新绑定）
            _heldWeapon = null;
            _gunParticles = null;
            _gunLine = null;
            _gunLight = null;
            _gunAudio = null;
            if (index < 0 || index >= WeaponPrefabs.Length) return;
            var gun = WeaponPrefabs[index] != null ? WeaponPrefabs[index] : LoadGun(index);
            if (gun is null) return; // 原枪模缺失：不显示（不影响权威逻辑）
            _heldWeapon = Instantiate(gun);
            if (_heldWeapon is not null && _camera is not null)
            {
                _heldWeapon.transform.SetParent(_camera.transform, false);
                _heldWeapon.transform.localPosition = new Vector3(0.25f, -0.2f, 0.5f);
            }
            BindWeaponEffects(index); // 从原枪 prefab 实例取自带特效组件（复刻宪法：不用自建近似物）
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
