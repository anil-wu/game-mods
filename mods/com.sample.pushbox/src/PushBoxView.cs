using Game.ECS;
using UnityEngine;

namespace Com.Sample.PushBox
{
    /// <summary>
    /// 推箱子 Mod 自带的视图（MonoBehaviour）：输入 → 网络协议（Host 走完整回环管线，§11.3）→
    /// 读取本地状态 → OnGUI 渲染。由运行时通用实例化，运行时无需感知本 Mod。
    /// </summary>
    public sealed class PushBoxView : MonoBehaviour
    {
        private static readonly PushInputProtocol InputProtocol = new();

        private uint _sessionId;
        private uint _boxId;
        private float _boxX;
        private bool _won;
        private PlayerSide _winner;
        private bool _quitPopup;

        private void Awake()
        {
            // 找到 Session 单例实体，拿到箱子引用
            foreach (var (entity, session) in PushBoxMod.World.Store<Session>().All())
            {
                _sessionId = entity;
                _boxId = session.Box;
                break;
            }
        }

        private void Update()
        {
            // Esc：呼出 / 关闭退出游戏弹窗
            if (Input.GetKeyDown(KeyCode.Escape))
                _quitPopup = !_quitPopup;

            // 输入：A=左玩家推(→)，D=右玩家推(←)
            // Client 唯一出口 SendToServer（§11.7）；Host 本地客户端也走完整协议回环，不短路（§11.3）
            if (!_quitPopup && PushBoxMod.Context.Network.IsActive)
            {
                PushBoxMod.Context.Network.SendToServer(
                    InputProtocol.Id, new PushInput(PlayerSide.Left, Input.GetKey(KeyCode.A)));
                PushBoxMod.Context.Network.SendToServer(
                    InputProtocol.Id, new PushInput(PlayerSide.Right, Input.GetKey(KeyCode.D)));
            }

            // 读取状态（Server 权威结果经回环广播落地为本地状态，§12.9 模式 3）
            var session = PushBoxMod.World.Get<Session>(new Entity(_sessionId));
            _won = session.Phase == GamePhase.Won || PushBoxMod.ClientWinner.HasValue;
            _winner = PushBoxMod.ClientWinner ?? session.Winner;
            _boxX = PushBoxMod.World.Get<Position>(new Entity(_boxId)).X;
        }

        private void OnGUI()
        {
            float w = Screen.width * 0.8f;
            float cx = Screen.width / 2f;
            float cy = Screen.height / 2f;
            float left = cx - w / 2f;
            float right = cx + w / 2f;
            float scale = w / 20f; // 场地 [-10,10] → 像素

            // 场地线
            GUI.Box(new Rect(left, cy - 3, w, 6), "");

            // A 区（左）/ B 区（右）
            var old = GUI.color;
            GUI.color = new Color(1f, 0.35f, 0.35f, 0.5f);
            GUI.Box(new Rect(left, cy - 28, 3 * scale, 56), "A 区");
            GUI.color = new Color(0.35f, 0.5f, 1f, 0.5f);
            GUI.Box(new Rect(cx + 7 * scale, cy - 28, 3 * scale, 56), "B 区");
            GUI.color = old;

            // 玩家标记
            GUI.Label(new Rect(left - 60, cy - 40, 60, 30), "A(左)");
            GUI.Label(new Rect(right + 10, cy - 40, 60, 30), "B(右)");

            // 箱子
            float bx = cx + _boxX * scale;
            GUI.color = Color.yellow;
            GUI.Box(new Rect(bx - 15, cy - 15, 30, 30), "箱");
            GUI.color = old;

            // 状态
            string status = _won
                ? $"游戏结束！胜者: {(_winner == PlayerSide.Left ? "A(左)" : "B(右)")}"
                : "按 A=左玩家推(→)   按 D=右玩家推(←)   Esc=退出游戏";
            GUI.Label(new Rect(20, 20, 700, 30), status);

            // 退出游戏弹窗（Esc 呼出）
            if (_quitPopup)
                DrawQuitPopup();
        }

        /// <summary>退出游戏确认弹窗（居中）。</summary>
        private void DrawQuitPopup()
        {
            const float w = 340f, h = 150f;
            GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h), GUI.skin.box);
            GUILayout.FlexibleSpace();
            GUILayout.Label("<b>退出推箱子？</b>", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
            GUILayout.Label("将卸载本游戏并返回大厅（Mod 商店）。", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("退出游戏", GUILayout.Width(100), GUILayout.Height(32)))
            {
                _quitPopup = false;
                QuitToLobby();
            }
            GUILayout.Space(16);
            if (GUILayout.Button("取消", GUILayout.Width(100), GUILayout.Height(32)))
                _quitPopup = false;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.EndArea();
        }

        /// <summary>
        /// 退出推箱子 = 热卸载本 Mod 返回大厅：经合法通道（context.Mods，§12.10）发起，
        /// 按 §7 管线镜像销毁——协议/系统/事件/视图全部清理；主 Mod 与框架 Mod（pinned）存活。
        /// </summary>
        private static void QuitToLobby()
        {
            var context = PushBoxMod.Context; // 先取引用：Unregister 后静态桥会被清空
            context.Mods.Unload(PushBoxMod.ModIdValue);
        }
    }
}
