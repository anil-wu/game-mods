using UnityEngine;
using UnityEngine.UI;

namespace Com.Zombtoy.Ui
{
    /// <summary>
    /// HUD 视图（ui CONTRACT.md §1 窗口 ui:hud / §2 数据绑定）。
    /// 打开时机：game:StateChanged(Playing)（WindowRouter 路由）。本视图自建 Canvas 并按相位显隐
    /// （资源 prefab GUID 断链未修 → 视图自建视觉，任务约束）。
    /// 内容：血条/体力/分数/僵尸数/弹药/武器名（Text 自建，订阅 UiBindings.Changed 重绘；
    /// 数据源 UiBindings 快照由 UiHandlers 订阅各 owner 消息写入，契约 §2 解析器）。
    /// 受伤/治疗全屏闪屏：订阅 player:HealthChanged（经 UiBindings delta）——受击闪红、回血闪绿，
    /// 每帧 Lerp 淡出（对齐原版 PlayerHealth.damageImage/flashColour/healFlashColor/flashSpeed）。
    /// Esc → game:pause（契约 §3；暂停覆盖层/继续/回主菜单由此视图层承载）。
    /// </summary>
    public sealed class HudView : MonoBehaviour
    {
        [Header("条（prefab 绑定，MVP；资源未迁移时由本视图自建 Text 替代）")]
        public Image healthBar = null!;   // 血条 fillAmount = current/max（契约 §2）
        public Image staminaBar = null!;  // 体力条 fillAmount = current/max
        public GameObject staminaBarRoot = null!; // 满体力隐藏（对齐原版 StaminaSliderWhole）

        [Header("文本")]
        public Text scoreText = null!;    // "Score: N"
        public Text zombieText = null!;   // "Zombies: N"
        public Text ammoText = null!;     // "inMag/reserve"
        public Text gunText = null!;      // 武器名/字号（WeaponSwitched）

        private GameObject? _uiRoot;      // 自建 Canvas 根
        private Text _healthText = null!;
        private Text _staminaText = null!;
        private Text _scoreTextSelf = null!;
        private Text _zombieTextSelf = null!;
        private Text _ammoTextSelf = null!;
        private Text _gunTextSelf = null!;

        // ---- 受伤/治疗全屏闪屏（对齐原版 PlayerHealth damageImage 语义）----
        private Image _damageImage = null!;   // 全屏闪屏 Image（BuildUi 自建，canvas 顶层）
        private bool _flashDamaged;           // 本帧受伤（HealthChanged 下降 → 闪红）
        private bool _flashHealing;           // 本帧治疗（HealthChanged 上升 → 闪绿）
        private int _lastHealth = -1;         // 上次血量（delta 判受伤/治疗；-1 = 基线未建立不闪）

        // 原版 PlayerHealth 数值（复刻宪法：原样）：受伤闪红 flashColour (1,0,0,0.1)；
        // Update healing 分支写绿 (0,1,0,0.15)（healFlashColor 字段 alpha 0.1，但 Update 实际写 0.15，按实际表现复刻）；
        // 淡出因子 flashSpeed = 0.5（color = Lerp(color, clear, flashSpeed*deltaTime)）。
        private static readonly Color DamageFlashColour = new(1f, 0f, 0f, 0.1f);
        private static readonly Color HealFlashColour = new(0f, 1f, 0f, 0.15f);
        private const float FlashSpeed = 0.5f;

        private void Awake()
        {
            BuildUi(); // 自建 Canvas + 文本 + 全屏闪屏 Image（宿主只建空 GameObject）
        }

        private void OnEnable()
        {
            UiBindings.Changed += Refresh;
            if (_damageImage != null) _damageImage.color = Color.clear; // 重开窗口清残留闪色（原版换场景即清）
            Refresh();
        }

        private void OnDisable()
        {
            UiBindings.Changed -= Refresh;
        }

        private void Update()
        {
            // Esc → game:pause（契约 §3；HudView 承载暂停入口，逻辑相位与 Time.timeScale 解耦，game 契约 §3 注）
            if (Input.GetKeyDown(KeyCode.Escape))
                UiCommands.Pause();

            // 按相位显隐（Playing/Paused 显示；Menu/GameOver/Result 隐藏）
            if (_uiRoot != null)
            {
                var phase = UiBindings.Phase;
                _uiRoot.SetActive(phase == (byte)GamePhase.Playing || phase == (byte)GamePhase.Paused);
            }

            UpdateHealthFlash(); // 受伤闪红/回血闪绿（每帧淡出，对齐原版 PlayerHealth.Update）
        }

        /// <summary>
        /// 受伤/治疗全屏闪屏（对齐原版 PlayerHealth.Update 的 damaged/healing 两分支）：
        /// 标志位所在帧直接写满色（受伤→红 flashColour、治疗→绿 healFlashColor），随后各帧
        /// color = Lerp(color, Color.clear, flashSpeed*deltaTime) 向透明淡出；帧末清标志（原版同构）。
        /// 标志位由 Refresh（player:HealthChanged 快照 delta）写入，见 Refresh()。
        /// </summary>
        private void UpdateHealthFlash()
        {
            if (_uiRoot != null && _uiRoot.activeSelf)
            {
                if (_flashDamaged)
                    _damageImage.color = DamageFlashColour;
                else
                    _damageImage.color = Color.Lerp(_damageImage.color, Color.clear, FlashSpeed * Time.deltaTime);
                if (_flashHealing)
                    _damageImage.color = HealFlashColour;
                else
                    _damageImage.color = Color.Lerp(_damageImage.color, Color.clear, FlashSpeed * Time.deltaTime);
            }
            else if (_damageImage != null)
            {
                _damageImage.color = Color.clear; // 非 Playing/Paused：清残留闪色（防跨局残留）
            }
            _flashDamaged = false;
            _flashHealing = false;
        }

        private void Refresh()
        {
            // 自建文本（首选）；外部 prefab 绑定字段存在时同步（双写兼容）
            if (healthBar != null && UiBindings.MaxHealth > 0)
                healthBar.fillAmount = Mathf.Clamp01((float)UiBindings.Health / UiBindings.MaxHealth);
            if (staminaBar != null && UiBindings.MaxStamina > 0f)
                staminaBar.fillAmount = Mathf.Clamp01(UiBindings.Stamina / UiBindings.MaxStamina);
            if (staminaBarRoot != null)
                staminaBarRoot.SetActive(UiBindings.Stamina < UiBindings.MaxStamina - 0.01f); // 满体力隐藏
            if (scoreText != null) scoreText.text = $"Score: {UiBindings.Score}";
            if (zombieText != null) zombieText.text = $"Zombies: {UiBindings.ZombieCount}";
            if (ammoText != null) ammoText.text = $"{UiBindings.InMag}/{UiBindings.Reserve}";
            if (gunText != null) gunText.text = WeaponNames.Name(UiBindings.WeaponSlot);

            if (_healthText != null) _healthText.text = $"HP: {UiBindings.Health}/{UiBindings.MaxHealth}";
            if (_staminaText != null) _staminaText.text = $"Stamina: {UiBindings.Stamina:F0}%";
            if (_scoreTextSelf != null) _scoreTextSelf.text = $"Score: {UiBindings.Score}";
            if (_zombieTextSelf != null) _zombieTextSelf.text = $"Zombies: {UiBindings.ZombieCount}";
            if (_ammoTextSelf != null) _ammoTextSelf.text = $"{UiBindings.InMag}/{UiBindings.Reserve}";
            if (_gunTextSelf != null) _gunTextSelf.text = WeaponNames.Name(UiBindings.WeaponSlot);

            // 受伤/治疗闪屏触发：HealthChanged 快照下降 = 受伤（闪红）；上升 = 治疗（闪绿）。
            // 基线（_lastHealth < 0）只记录不闪；上一值 = 0（死亡/重开局边界，原版新场景不闪）不判治疗。
            if (_lastHealth >= 0)
            {
                if (UiBindings.Health < _lastHealth)
                {
                    _flashDamaged = true;
                    Debug.Log($"[HudView] 玩家受击闪红触发 HP {_lastHealth}→{UiBindings.Health}（damageImage = {DamageFlashColour}，日志声明）");
                }
                else if (UiBindings.Health > _lastHealth && _lastHealth > 0)
                {
                    _flashHealing = true;
                    Debug.Log($"[HudView] 玩家回血闪绿触发 HP {_lastHealth}→{UiBindings.Health}（damageImage = {HealFlashColour}，日志声明）");
                }
            }
            _lastHealth = UiBindings.Health;
        }

        /// <summary>自建 HUD（Canvas + 左上信息列 + 右下弹药列；ScreenSpaceOverlay 不进相机截图）。</summary>
        private void BuildUi()
        {
            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasGo.AddComponent<GraphicRaycaster>();
            _uiRoot = canvasGo;

            // 左上信息列（血条/体力/分数/僵尸数）
            _healthText = CreateText(canvasGo.transform, "Health", "", 28, new Vector2(180f, -36f), TextAnchor.MiddleLeft);
            _staminaText = CreateText(canvasGo.transform, "Stamina", "", 24, new Vector2(180f, -80f), TextAnchor.MiddleLeft);
            _scoreTextSelf = CreateText(canvasGo.transform, "Score", "", 24, new Vector2(180f, -124f), TextAnchor.MiddleLeft);
            _zombieTextSelf = CreateText(canvasGo.transform, "Zombies", "", 24, new Vector2(180f, -168f), TextAnchor.MiddleLeft);

            // 右下弹药列（弹药 + 武器名）
            _ammoTextSelf = CreateText(canvasGo.transform, "Ammo", "", 28, new Vector2(1740f, -80f), TextAnchor.MiddleRight);
            _gunTextSelf = CreateText(canvasGo.transform, "Weapon", "", 24, new Vector2(1740f, -124f), TextAnchor.MiddleRight);

            // 全屏受伤/治疗闪屏（canvas 顶层，最后创建 = 绘制在最上；原版 PlayerHealth.damageImage）
            _damageImage = CreateFlashImage(canvasGo.transform);
            Debug.Log("[HudView] 受击闪红/回血闪绿全屏闪屏已接入（player:HealthChanged delta → damageImage，"
                      + $"原版 PlayerHealth.damaged/healing 分支：红 {DamageFlashColour} / 绿 {HealFlashColour} / flashSpeed {FlashSpeed}）");
        }

        /// <summary>全屏闪屏 Image（默认白图染色 = 原版 damageImage 形态；raycastTarget=false 不挡交互）。</summary>
        private static Image CreateFlashImage(Transform parent)
        {
            var go = new GameObject("DamageFlash");
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = Color.clear;
            img.raycastTarget = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return img;
        }

        private static Text CreateText(Transform parent, string name, string content, int fontSize, Vector2 anchoredPos, TextAnchor anchor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.text = content;
            text.fontSize = fontSize;
            text.color = new Color(0.95f, 0.97f, 1f);
            text.font = BuiltinFont();
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(420f, 48f);
            rt.anchoredPosition = anchoredPos;
            return text;
        }

        private static Font BuiltinFont()
        {
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); // 2022.3 内置字体
            if (f is null) f = Resources.GetBuiltinResource<Font>("Arial.ttf"); // 旧版兜底
            return f;
        }
    }

    /// <summary>武器名映射（HUD GunText 展示，对齐原版武器名；槽位编号见 weapon CONTRACT §9 表）。</summary>
    public static class WeaponNames
    {
        public static string Name(uint slot) => slot switch
        {
            0 => "Machine Gun",
            1 => "Shotgun",
            2 => "Rocket Launcher",
            3 => "Cryo Pistol",
            _ => "Weapon",
        };
    }
}
