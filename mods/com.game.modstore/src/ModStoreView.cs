using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Com.Game.ModStore
{
    /// <summary>
    /// Mod 商店视图（MonoBehaviour，OnGUI 占位 UI）：
    /// 折叠 = 右上角商店图标；点击图标 = 打开居中列表；列表右上角 × 关闭。
    /// 由运行时通用实例化；UI.Mod 接入 UGUI 绑定后应迁移为正式窗口（§10）。
    /// </summary>
    public sealed class ModStoreView : MonoBehaviour
    {
        private const float IconSize = 48f;
        private const float WindowWidth = 560f;
        private const float WindowHeight = 440f;

        private RemoteModInfo[] _mods = Array.Empty<RemoteModInfo>();
        private string _serverUrl = "";
        private string _busy = "";
        private Vector2 _scroll;
        private bool _open;
        private Texture2D? _icon;

        private void Awake()
        {
            // Unity 侧安装目录：persistentDataPath/mods（StreamingAssets 在各平台不可写）
            ModStoreMod.Service.LocalModsDir =
                System.IO.Path.Combine(Application.persistentDataPath, "mods");
            _serverUrl = ModStoreMod.Service.ServerBaseUrl;
        }

        private void OnGUI()
        {
            if (!_open)
            {
                DrawStoreIcon();
                return;
            }
            DrawStoreWindow();
        }

        // ---- 折叠态：右上角商店图标 ----

        private void DrawStoreIcon()
        {
            _icon ??= CreateStoreIcon();
            var rect = new Rect(Screen.width - IconSize - 12f, 12f, IconSize, IconSize);
            if (GUI.Button(rect, _icon))
                _open = true;
        }

        // ---- 展开态：居中列表窗口 ----

        private void DrawStoreWindow()
        {
            var rect = new Rect(
                (Screen.width - WindowWidth) / 2f,
                (Screen.height - WindowHeight) / 2f,
                WindowWidth, WindowHeight);

            GUILayout.BeginArea(rect, GUI.skin.box);

            // 标题栏 + 右上角关闭按钮
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Mod 商店</b>");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", GUILayout.Width(28)))
            {
                _open = false;
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                return;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            // 服务器地址 + 刷新
            GUILayout.BeginHorizontal();
            GUILayout.Label("服务器", GUILayout.Width(44));
            _serverUrl = GUILayout.TextField(_serverUrl, GUILayout.MinWidth(260));
            GUI.enabled = _busy.Length == 0;
            if (GUILayout.Button("刷新列表", GUILayout.Width(80)))
                Run(() => ModStoreMod.Service.ListRemoteMods(), mods => _mods = mods);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // 列表
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(WindowHeight - 130f));
            foreach (var mod in _mods)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                var loaded = FindLoaded(mod.Id.Value);
                GUILayout.Label(mod.ToString(), GUILayout.MinWidth(240));
                GUILayout.FlexibleSpace();
                if (loaded)
                {
                    GUILayout.Label("已加载", GUILayout.Width(60));
                }
                else
                {
                    GUI.enabled = _busy.Length == 0;
                    if (GUILayout.Button("安装并启动", GUILayout.Width(90)))
                        InstallThenStartOnMainThread(mod.Id, mod.Version);
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }
            if (_mods.Length == 0)
                GUILayout.Label("点击 [刷新列表] 获取服务器 Mod");
            GUILayout.EndScrollView();

            // 状态行
            var status = _busy.Length > 0 ? _busy : ModStoreMod.Service?.Status ?? "";
            GUILayout.Label(status);
            GUILayout.EndArea();
        }

        // ---- 逻辑（与之前一致） ----

        private static bool FindLoaded(string modId)
        {
            return ModStoreMod.Service is not null
                && global::Game.Mod.Contract.ModId.TryParse(modId, out var id)
                && ModStoreMod.Service.IsModLoaded(id);
        }

        private void Run<T>(Func<Task<T>> action, Action<T> onDone)
        {
            _busy = "请稍候…";
            var task = Task.Run(action);
            task.ContinueWith(t =>
            {
                // ContinueWith 经 UnitySynchronizationContext 回到主线程
                if (t.IsFaulted)
                    _busy = $"失败: {t.Exception?.GetBaseException().Message}";
                else
                {
                    _busy = "";
                    onDone(t.Result);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// 两阶段安装启动：下载/解包（纯 IO）放后台线程；
        /// StartInstalled 必须回主线程——ModManager.LoadFromDirectory 会触发 ModLoaded →
        /// 运行时实例化 Mod 视图（new GameObject），Unity API 只能在主线程调用。
        /// </summary>
        private void InstallThenStartOnMainThread(global::Game.Mod.Contract.ModId id, global::Game.Mod.Contract.ModVersion version)
        {
            _busy = "下载安装…";
            var task = Task.Run(() => ModStoreMod.Service.InstallWithClosure(id, version));
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _busy = $"失败: {t.Exception?.GetBaseException().Message}";
                    return;
                }
                try
                {
                    ModStoreMod.Service.StartInstalled(id); // 主线程（视图实例化安全）
                    _busy = "";
                }
                catch (Exception e)
                {
                    _busy = $"失败: {e.GetBaseException().Message}";
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---- 程序化商店图标（像素购物车，无需美术资源） ----

        private static Texture2D CreateStoreIcon()
        {
            const int size = 48;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
            };

            var bg = new Color(0.16f, 0.47f, 0.86f, 1f);
            var fg = Color.white;
            var pixels = new Color[size * size];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = bg;

            // 12x10 像素购物车
            string[] cart =
            {
                "##..........",
                "##..........",
                "##########..",
                ".########...",
                ".#.#.#.#.#..",
                ".########...",
                "..######....",
                "............",
                "..##..##....",
                "..##..##....",
            };

            const int scale = 3;
            var offsetX = (size - 12 * scale) / 2;
            var offsetY = (size - 10 * scale) / 2;
            for (var y = 0; y < cart.Length; y++)
            {
                for (var x = 0; x < cart[y].Length; x++)
                {
                    if (cart[y][x] != '#') continue;
                    for (var dy = 0; dy < scale; dy++)
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var px = offsetX + x * scale + dx;
                        var py = offsetY + y * scale + dy;
                        if (px >= 0 && px < size && py >= 0 && py < size)
                            pixels[(size - 1 - py) * size + px] = fg; // 纹理 Y 轴向上
                    }
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
