using UnityEngine;
using UnityEngine.UI;

namespace Com.Zombtoy.Ui
{
    /// <summary>
    /// 结算视图（ui CONTRACT.md §1 窗口 ui:result / §2 数据绑定 / §3 调用）。
    /// 打开时机：score:GameOver（UiHandlers.OnScoreGameOver 打开 + 刷新榜单）。
    /// 内容：最终分数（finalScore）/ 击杀数（kills）/ "New High Score!"（isNewHigh）/ 榜单（TopChanged）/
    /// 再玩一次（game:start）/ 回主菜单（game:to_menu）。
    /// 数据源：UiBindings 快照（ResultSummary/LeaderboardRows 解析器写入）。
    /// </summary>
    public sealed class ResultView : MonoBehaviour
    {
        [Header("结算文本（契约 §2）")]
        public Text finalScoreText = null!; // "Final Score: N"
        public Text killsText = null!;      // "Kills: N"
        public Text newHighText = null!;    // "New High Score!"（IsNewHigh 时显示）
        public Text leaderboardText = null!; // 榜单（TopChanged rows）

        [Header("按钮（契约 §3）")]
        public Button playAgainButton = null!; // 再玩一次 → game:start
        public Button menuButton = null!;      // 回主菜单 → game:to_menu

        private void OnEnable()
        {
            UiBindings.Changed += Refresh;
            if (!UiBindings.HasGameOver)
                UiCommands.RefreshResult(); // 初始填充（契约 §2：score:get_state + leaderboard:refresh）
            Refresh();
        }

        private void OnDisable()
        {
            UiBindings.Changed -= Refresh;
        }

        private void Start()
        {
            if (playAgainButton != null) playAgainButton.onClick.AddListener(OnPlayAgain);
            if (menuButton != null) menuButton.onClick.AddListener(OnMenu);
        }

        private void OnDestroy()
        {
            if (playAgainButton != null) playAgainButton.onClick.RemoveListener(OnPlayAgain);
            if (menuButton != null) menuButton.onClick.RemoveListener(OnMenu);
        }

        /// <summary>再玩一次 → game:start（契约 §3；开局编排由 game 能力实现，二进制 ModCall）。</summary>
        private void OnPlayAgain() => UiCommands.StartGame();

        /// <summary>回主菜单 → game:to_menu（契约 §3）。</summary>
        private void OnMenu() => UiCommands.ToMenu();

        private void Refresh()
        {
            if (finalScoreText != null) finalScoreText.text = $"Final Score: {UiBindings.FinalScore}";
            if (killsText != null) killsText.text = $"Kills: {UiBindings.GameOverKills}";
            if (newHighText != null) newHighText.gameObject.SetActive(UiBindings.IsNewHigh);
            if (leaderboardText != null) leaderboardText.text = LeaderboardText.Format(UiBindings.Rows);
        }
    }
}
