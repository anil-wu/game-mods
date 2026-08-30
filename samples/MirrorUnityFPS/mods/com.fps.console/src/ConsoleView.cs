using UnityEngine;

namespace Com.Fps.Console
{
    /// <summary>控制台视图（OnGUI）：` 键呼出输入行，Enter 发送，结果回显。</summary>
    public sealed class ConsoleView : MonoBehaviour
    {
        private bool _open;
        private string _input = "";
        private string _output = "";

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.BackQuote))
            {
                _open = !_open;
                if (_open)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }

            if (ConsoleMod.LastOutputTicks != 0 && ConsoleMod.LastOutput != _output)
                _output = ConsoleMod.LastOutput;
        }

        private void OnGUI()
        {
            if (!_open) return;

            var r = new Rect(Screen.width / 2f - 250f, 60f, 500f, 120f);
            GUILayout.BeginArea(r, GUI.skin.box);
            GUILayout.Label("控制台（` 关闭）");

            GUI.SetNextControlName("ConsoleInput");
            _input = GUILayout.TextField(_input);
            if (GUI.GetNameOfFocusedControl() != "ConsoleInput") GUI.FocusControl("ConsoleInput");

            if ((Event.current.isKey && Event.current.keyCode == KeyCode.Return) ||
                GUILayout.Button("执行"))
            {
                if (_input.Trim().Length > 0)
                {
                    var ctx = ConsoleMod.Context;
                    if (ctx is not null && ctx.Network.IsActive)
                        ctx.Network.SendToServer(new CommandProtocol().Id, new CommandMessage(_input));
                    _input = "";
                }
            }

            if (_output.Length > 0)
                GUILayout.Label(_output);
            GUILayout.EndArea();
        }
    }
}
