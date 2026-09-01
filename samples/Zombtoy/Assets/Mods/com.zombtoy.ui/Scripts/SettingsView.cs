using UnityEngine;
using UnityEngine.UI;

namespace Com.Zombtoy.Ui
{
    /// <summary>
    /// 设置视图（ui CONTRACT.md §1 窗口 ui:settings / §6 MVP 最小集）。
    /// 打开时机：菜单内按钮打开（MenuView.OnSettings）。
    /// MVP 最小集（契约 §6）：灵敏度 → 鼠标射线灵敏度系数，经 PlayerPrefs 本地持久化
    /// （键 UiSettings.SensitivityKey，对齐原版 MouseSensitivity 语义；音量等 v2 追加）。
    /// </summary>
    public sealed class SettingsView : MonoBehaviour
    {
        [Header("灵敏度（契约 §6 MVP：0.5..2.0 系数）")]
        public Slider sensitivitySlider = null!;
        public Text sensitivityValue = null!;

        private void OnEnable()
        {
            // 加载本地持久化（PlayerPrefs 是 Unity API，视图层读写；无头测试只校验 UiSettings 默认值）
            UiSettings.Sensitivity = PlayerPrefs.GetFloat(UiSettings.SensitivityKey, 1f);
            if (sensitivitySlider != null)
            {
                sensitivitySlider.minValue = 0.5f;
                sensitivitySlider.maxValue = 2f;
                sensitivitySlider.value = UiSettings.Sensitivity;
                sensitivitySlider.onValueChanged.AddListener(OnSensitivityChanged);
                OnSensitivityChanged(UiSettings.Sensitivity);
            }
        }

        private void OnDisable()
        {
            if (sensitivitySlider != null)
                sensitivitySlider.onValueChanged.RemoveListener(OnSensitivityChanged);
            PlayerPrefs.Save(); // 落盘
        }

        private void OnSensitivityChanged(float value)
        {
            UiSettings.Sensitivity = value; // 视图（鼠标射线灵敏度系数）与逻辑共享
            PlayerPrefs.SetFloat(UiSettings.SensitivityKey, value);
            if (sensitivityValue != null) sensitivityValue.text = value.ToString("F2");
        }
    }
}
