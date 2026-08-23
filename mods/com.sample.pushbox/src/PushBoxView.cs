using Game.ECS;
using UnityEngine;

namespace Com.Sample.PushBox
{
    /// <summary>
    /// 推箱子 Mod 自带的视图（MonoBehaviour）：输入 → 服务端权威 tick → 读取状态 → OnGUI 渲染。
    /// 由 runtime 的 ModLoaderService 通用实例化，运行时无需感知本 Mod。
    /// </summary>
    public sealed class PushBoxView : MonoBehaviour
    {
        private uint _sessionId;
        private uint _boxId;
        private float _boxX;
        private bool _won;
        private PlayerSide _winner;

        private void Awake()
        {
            // 找到 Session 单例实体，拿到箱子引用
            foreach (var (entity, session) in ModEntry.World.Store<Session>().All())
            {
                _sessionId = entity;
                _boxId = session.Box;
                break;
            }
        }

        private void Update()
        {
            // 输入：A=左玩家推(→)，D=右玩家推(←)
            ModEntry.Messages.SendTo(ModEntry.ModId, new PushInputMsg { Side = PlayerSide.Left, Pushing = Input.GetKey(KeyCode.A) });
            ModEntry.Messages.SendTo(ModEntry.ModId, new PushInputMsg { Side = PlayerSide.Right, Pushing = Input.GetKey(KeyCode.D) });

            // 服务端权威 tick
            ModEntry.Systems.UpdateAll(ModEntry.World, Time.deltaTime);

            // 读取状态
            var session = ModEntry.World.Get<Session>(new Entity(_sessionId));
            _won = session.Phase == GamePhase.Won;
            _winner = session.Winner;
            _boxX = ModEntry.World.Get<Position>(new Entity(_boxId)).X;
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
                : "按 A=左玩家推(→)   按 D=右玩家推(←)";
            GUI.Label(new Rect(20, 20, 600, 30), status);
        }
    }
}
