using UnityEngine;
using UnityEngine.UI;

namespace Com.Zombtoy.Ui
{
    /// <summary>
    /// 主菜单视图（ui CONTRACT.md §1 窗口 ui:menu / §3 调用）。
    /// 打开时机：game:StateChanged(Menu)（WindowRouter 路由）。
    /// 内容：标题 + 开始/设置/排行榜按钮。MVP 方案（契约头部）：窗口 prefab 经 WindowManager 实例化
    /// （Rule 9，禁止直接 Instantiate 到全局 Canvas），本视图只做按钮回调与开窗。
    /// 跨 Mod 调用一律走 UiCommands（二进制 ModCall，Rule 14）：
    /// - 开始 → game:start（契约 §3）；- 设置/排行榜 → 打开本 Mod 对应窗口。
    /// </summary>
    public sealed class MenuView : MonoBehaviour
    {
        [Header("按钮（prefab 绑定，MVP）")]
        public Button startButton = null!;
        public Button settingsButton = null!;
        public Button leaderboardButton = null!;

        private void Start()
        {
            if (startButton != null) startButton.onClick.AddListener(OnStart);
            if (settingsButton != null) settingsButton.onClick.AddListener(OnSettings);
            if (leaderboardButton != null) leaderboardButton.onClick.AddListener(OnLeaderboard);
        }

        private void OnDestroy()
        {
            if (startButton != null) startButton.onClick.RemoveListener(OnStart);
            if (settingsButton != null) settingsButton.onClick.RemoveListener(OnSettings);
            if (leaderboardButton != null) leaderboardButton.onClick.RemoveListener(OnLeaderboard);
        }

        /// <summary>开始游戏 → game:start（契约 §3：ui → game 单向调用）。</summary>
        private void OnStart() => UiCommands.StartGame();

        /// <summary>设置 → 打开 ui:settings（契约 §1：菜单内打开）。</summary>
        private void OnSettings()
        {
            var ctx = UiMod.Context;
            if (ctx is null) return; // 已卸载（防御）
            ctx.Ui.Open(UiMod.SettingsWindow);
        }

        /// <summary>排行榜 → 打开 ui:leaderboard 并刷新榜单（契约 §3：leaderboard:refresh 入队异步）。</summary>
        private void OnLeaderboard()
        {
            var ctx = UiMod.Context;
            if (ctx is null) return;
            ctx.Ui.Open(UiMod.LeaderboardWindow);
            UiCommands.RefreshLeaderboard();
        }
    }
}
