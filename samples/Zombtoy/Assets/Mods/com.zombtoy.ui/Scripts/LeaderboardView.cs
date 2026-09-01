using UnityEngine;
using UnityEngine.UI;

namespace Com.Zombtoy.Ui
{
    /// <summary>
    /// 排行榜视图（任务 5 窗口之一；ui CONTRACT.md §3：leaderboard:refresh + TopChanged 订阅刷新）。
    /// 打开时机：菜单"排行榜"按钮（MenuView.OnLeaderboard）打开并刷新；Result 结算窗口内榜单复用 LeaderboardText 格式。
    /// 数据源：UiBindings.Rows（UiHandlers.OnTopChanged 解析 LeaderboardRows 写入，契约 §2）。
    /// 榜单行：[rank u32, score i32, name string]（leaderboard CONTRACT §2/§3）。
    /// </summary>
    public sealed class LeaderboardView : MonoBehaviour
    {
        [Header("榜单文本")]
        public Text rowsText = null!;

        private void OnEnable()
        {
            UiBindings.Changed += Refresh;
            UiCommands.RefreshLeaderboard(); // 打开时刷新（契约 §3：入队异步 → TopChanged → 本视图重绘）
            Refresh();
        }

        private void OnDisable()
        {
            UiBindings.Changed -= Refresh;
        }

        private void Refresh()
        {
            if (rowsText != null) rowsText.text = LeaderboardText.Format(UiBindings.Rows);
        }
    }

    /// <summary>榜单行格式化（ResultView 与 LeaderboardView 共用；纯表现，无逻辑）。</summary>
    public static class LeaderboardText
    {
        /// <summary>rows → "1. alice - 5000\n2. bob - 3000"；空榜/未加载 → "暂无数据"。</summary>
        public static string Format(object?[] rows)
        {
            if (rows is null || rows.Length == 0) return "暂无数据";
            var sb = new System.Text.StringBuilder();
            foreach (var row in rows)
            {
                if (!LeaderboardRows.TryReadRow(row, out var rank, out var score, out var name)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(rank).Append(". ").Append(name).Append(" - ").Append(score);
            }
            return sb.Length == 0 ? "暂无数据" : sb.ToString();
        }
    }
}
