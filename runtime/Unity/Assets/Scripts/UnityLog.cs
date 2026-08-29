using Game.Mod.Runtime;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>Unity 日志适配（ILog → Debug.Log）。</summary>
    public sealed class UnityLog : ILog
    {
        public void Info(string message) => Debug.Log(message);
        public void Warn(string message) => Debug.LogWarning(message);
        public void Error(string message) => Debug.LogError(message);
    }
}
