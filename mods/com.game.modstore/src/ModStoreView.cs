using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Com.Game.ModStore
{
    /// <summary>
    /// Mod 商店视图（MonoBehaviour，OnGUI 占位 UI）：
    /// 呈现 mod_server 上的 Mod 列表 → 一键"安装并启动"。
    /// 由运行时通用实例化；UI.Mod 接入 UGUI 绑定后应迁移为正式窗口（§10）。
    /// </summary>
    public sealed class ModStoreView : MonoBehaviour
    {
        private RemoteModInfo[] _mods = Array.Empty<RemoteModInfo>();
        private string _serverUrl = "";
        private string _busy = "";
        private Vector2 _scroll;

        private void Awake()
        {
            // Unity 侧安装目录：persistentDataPath/mods（StreamingAssets 在各平台不可写）
            ModStoreMod.Service.LocalModsDir =
                System.IO.Path.Combine(Application.persistentDataPath, "mods");
            _serverUrl = ModStoreMod.Service.ServerBaseUrl;
        }

        private void OnGUI()
        {
            const float w = 460f;
            GUILayout.BeginArea(new Rect(Screen.width - w - 12, 12, w, Screen.height - 24), GUI.skin.box);
            GUILayout.Label("<b>Mod 商店</b>");
            GUILayout.Space(4);

            // 服务器地址 + 刷新
            GUILayout.BeginHorizontal();
            GUILayout.Label("服务器", GUILayout.Width(44));
            _serverUrl = GUILayout.TextField(_serverUrl, GUILayout.MinWidth(220));
            GUI.enabled = _busy.Length == 0;
            if (GUILayout.Button("刷新列表", GUILayout.Width(80)))
                Run(() => ModStoreMod.Service.ListRemoteMods(), mods => _mods = mods);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // 列表
            _scroll = GUILayout.BeginScrollView(_scroll);
            foreach (var mod in _mods)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                var loaded = ModStoreMod.Service is not null &&
                             FindLoaded(mod.Id.Value);
                GUILayout.Label(mod.ToString(), GUILayout.MinWidth(220));
                GUILayout.FlexibleSpace();
                if (loaded)
                {
                    GUILayout.Label("已加载", GUILayout.Width(60));
                }
                else
                {
                    GUI.enabled = _busy.Length == 0;
                    if (GUILayout.Button("安装并启动", GUILayout.Width(90)))
                        Run(() => ModStoreMod.Service.InstallAndStart(mod.Id, mod.Version), _ => { });
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

        private static bool FindLoaded(string modId)
        {
            // 经商店服务所在上下文查询（视图与服务同属一个 Mod 程序集）
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
                // ContinueWith 回到调用线程由 Unity 主循环保证（OnGUI 每帧读状态）
                if (t.IsFaulted)
                    _busy = $"失败: {t.Exception?.GetBaseException().Message}";
                else
                {
                    _busy = "";
                    onDone(t.Result);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
