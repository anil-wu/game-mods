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

        private void Awake()
        {
            BuildUi(); // 自建 Canvas + 文本（宿主只建空 GameObject）
        }

        private void OnEnable()
        {
            UiBindings.Changed += Refresh;
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
