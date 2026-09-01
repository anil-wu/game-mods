using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Com.Zombtoy.Ui
{
    /// <summary>
    /// 主菜单视图（ui CONTRACT.md §1 窗口 ui:menu / §3 调用）。
    /// 打开时机：game:StateChanged(Menu)（WindowRouter 路由）。本视图自建 Canvas 并按相位显隐
    /// （资源 prefab GUID 断链未修，WindowManager 无法实例化窗口 → 视图自建视觉，任务约束）。
    /// 内容：标题 + 开始按钮（自建 Text/Button/Canvas，AddComponent 程序化，不依赖 prefab）。
    /// 跨 Mod 调用一律走 UiCommands（二进制 ModCall，Rule 14）：开始 → game:start（契约 §3）。
    /// 批处理（截图/冒烟，Application.isBatchMode）无人点击：短暂停留后自动开局，供验收截图进 Playing 相位。
    /// </summary>
    public sealed class MenuView : MonoBehaviour
    {
        [Header("按钮（prefab 绑定，MVP；资源未迁移时由本视图自建）")]
        public Button startButton = null!;
        public Button settingsButton = null!;
        public Button leaderboardButton = null!;

        private GameObject? _uiRoot; // 自建 Canvas 根
        private bool _autoStarted;
        private int _frames;

        private void Awake()
        {
            BuildUi(); // 自建 Canvas + 标题 + 开始按钮（宿主只建空 GameObject）
        }

        private void Start()
        {
            if (startButton != null) startButton.onClick.AddListener(OnStart);
            if (settingsButton != null) settingsButton.onClick.AddListener(OnSettings);
            if (leaderboardButton != null) leaderboardButton.onClick.AddListener(OnLeaderboard);
        }

        private void Update()
        {
            // 按相位显隐（Menu 显示，其他隐藏；UiBindings.Phase 由 game:StateChanged 订阅写入）
            if (_uiRoot != null)
                _uiRoot.SetActive(UiBindings.Phase == (byte)GamePhase.Menu);

            // 批处理截图/冒烟：无人工点击，短暂停留后自动开局（编辑器/真机交互不受影响）
            if (Application.isBatchMode && !_autoStarted && ++_frames > 30)
            {
                _autoStarted = true;
                OnStart();
            }
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

        // ---- 自建视觉（不依赖 prefab；ScreenSpaceOverlay 不进相机截图，仅交互窗口） ----

        private void BuildUi()
        {
            // EventSystem（ugui 点击交互需要；宿主场景无时自建）
            if (EventSystem.current is null)
            {
                var es = new GameObject("EventSystem");
                es.transform.SetParent(transform, false);
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            // Canvas（ScreenSpaceOverlay + CanvasScaler + GraphicRaycaster）
            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasGo.AddComponent<GraphicRaycaster>();
            _uiRoot = canvasGo;

            // 深色底（菜单背景，便于阅读；全屏铺满）
            var bg = new GameObject("Background");
            bg.transform.SetParent(canvasGo.transform, false);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(0.07f, 0.1f, 0.14f, 0.92f);
            var bgRt = bg.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;

            // 标题
            CreateText(canvasGo.transform, "Title", "ZOMBTOY", 72, new Vector2(0f, 180f), new Color(0.85f, 0.95f, 1f));

            // 开始按钮（Image 底 + Button + 文本）
            var btnGo = new GameObject("StartButton");
            btnGo.transform.SetParent(canvasGo.transform, false);
            var btnImg = btnGo.AddComponent<Image>();
            btnImg.color = new Color(0.2f, 0.55f, 0.35f);
            startButton = btnGo.AddComponent<Button>();
            var btnRt = btnGo.GetComponent<RectTransform>();
            btnRt.sizeDelta = new Vector2(340f, 76f);
            btnRt.anchoredPosition = new Vector2(0f, 0f);
            CreateText(btnGo.transform, "Label", "开始游戏", 34, Vector2.zero, Color.white);
        }

        /// <summary>建 Text（内置字体，MiddleCenter 对齐；默认字号/尺寸，anchoredPosition 定位）。</summary>
        private static Text CreateText(Transform parent, string name, string content, int fontSize, Vector2 anchoredPos, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.text = content;
            text.fontSize = fontSize;
            text.color = color;
            text.font = BuiltinFont();
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(720f, 120f);
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
}
