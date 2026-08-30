using System.Collections.Generic;
using System.Reflection;
using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>
    /// 无头运行时宿主：装配 Core 能力（ECS / Message / Services / ModManager）。
    /// 游戏逻辑零感知——游戏循环由各 Mod 驱动；Unity 引导层按同样方式装配并注入平台实现。
    /// </summary>
    public sealed class ModRuntimeHost
    {
        public World World { get; }
        public SystemGroup Systems { get; }
        public MessageBus Messages { get; }
        public ServiceRegistry Services { get; }
        public ModManager Manager { get; }
        public RuntimeRole Role => Manager.Role;

        public ModRuntimeHost(
            RuntimeRole role,
            ILog? log = null,
            IAssemblyLoader? assemblyLoader = null,
            IResourceBackend? resourceBackend = null,
            MessageQuotas? quotas = null)
        {
            World = new World();
            Messages = new MessageBus(quotas);
            Systems = new SystemGroup(Messages);
            Services = new ServiceRegistry();
            Manager = new ModManager(role, World, Systems, Messages, Services, assemblyLoader, resourceBackend, log);

            Messages.OnError += e => (log ?? NullLog.Instance).Error(e.Message);
        }

        /// <summary>发现目录并整批加载（返回错误列表，空 = 全部成功）。</summary>
        public List<string> LoadDirectory(string modsDir) => Manager.LoadDirectory(modsDir);

        /// <summary>关闭运行时（退出游戏 / 停止 Play）：按依赖镜像卸载全部 Mod。</summary>
        public List<string> Shutdown() => Manager.UnloadAll();

        /// <summary>从预加载程序集加载 Mod（测试 / 嵌入式路径）。</summary>
        public ModObject Load(ModManifest manifest, Assembly assembly, string? contentRoot = null)
            => Manager.Load(manifest, new[] { assembly }, contentRoot);

        /// <summary>
        /// 主循环固定相位（§14.7）：帧开始重置消息配额 → 系统执行。
        /// Host 模式所有系统各执行一次。
        /// </summary>
        public void Tick(float deltaTime)
        {
            Messages.BeginFrame();
            Systems.UpdateAll(World, deltaTime);
        }

        /// <summary>按端侧 tick：Server 端执行 Server+Shared，Client 端执行 Client+Shared。</summary>
        public void Tick(float deltaTime, SystemSide side)
        {
            Messages.BeginFrame();
            Systems.Update(World, deltaTime, side);
        }
    }
}
