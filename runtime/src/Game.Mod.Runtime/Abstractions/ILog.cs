namespace Game.Mod.Runtime
{
    /// <summary>框架日志接口。</summary>
    public interface ILog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    /// <summary>空日志（默认）。</summary>
    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }
}
