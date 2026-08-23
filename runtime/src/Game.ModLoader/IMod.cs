using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;

namespace Game.ModLoader
{
    /// <summary>框架日志接口。</summary>
    public interface ILog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    /// <summary>
    /// Mod 加载上下文：框架注入给 Mod 的运行时服务。
    /// </summary>
    public interface IModContext
    {
        ModId ModId { get; }
        ModVersion Version { get; }
        World World { get; }
        SystemGroup Systems { get; }
        MessageBus Messages { get; }
        ILog Log { get; }
    }

    /// <summary>
    /// Mod 逻辑入口契约。entryDll 中的实现类在加载时被扫描并实例化。
    /// </summary>
    public interface IMod
    {
        /// <summary>加载阶段（Phase2）：注册内容/组件/系统/消息处理器，禁止读取其他 Mod 的注册。</summary>
        void OnLoad(IModContext context);

        /// <summary>全部 Mod 加载完成后（Phase4）调用。</summary>
        void OnEnable() { }

        /// <summary>卸载/禁用时调用。</summary>
        void OnDisable() { }
    }

}
