using UnityEngine;
using UnityEngine.UI;

namespace Com.Zombtoy.Ui
{
    /// <summary>
    /// HUD 视图（ui CONTRACT.md §1 窗口 ui:hud / §2 数据绑定）。
    /// 打开时机：game:StateChanged(Playing)（WindowRouter 路由）。
    /// 内容：血条（HealthChanged）/ 体力条（StaminaChanged，满时隐藏对齐原版 StaminaSliderWhole）/
    /// 分数（ScoreChanged）/ 僵尸数（ZombieCountChanged）/ 弹药（AmmoChanged "inMag/reserve"）/
    /// 武器名（WeaponSwitched，GunText）。
    /// 数据源：UiBindings 快照（订阅 handler 写入，契约 §2 解析器）；Changed 事件挂接重绘。
    /// Esc → game:pause（契约 §3；暂停覆盖层/继续/回主菜单由此视图层承载，任务 5 窗口范围无独立 ui:pause 窗口）。
    /// </summary>
    public sealed class HudView : MonoBehaviour
    {
        [Header("条")]
        public Image healthBar = null!;   // 血条 fillAmount = current/max（契约 §2）
        public Image staminaBar = null!;  // 体力条 fillAmount = current/max
        public GameObject staminaBarRoot = null!; // 满体力隐藏（对齐原版 StaminaSliderWhole）

        [Header("文本")]
        public Text scoreText = null!;    // "Score: N"
        public Text zombieText = null!;   // "Zombies: N"
        public Text ammoText = null!;     // "inMag/reserve"
        public Text gunText = null!;      // 武器名/字号（WeaponSwitched）

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
        }

        private void Refresh()
        {
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
