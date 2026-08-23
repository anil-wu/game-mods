using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;
using Game.ModLoader;

namespace Game.Runtime
{
    /// <summary>IModContext 的运行时实现：向 Mod 注入框架服务。</summary>
    public sealed class ModContextImpl : IModContext
    {
        public ModId ModId { get; }
        public ModVersion Version { get; }
        public World World { get; }
        public SystemGroup Systems { get; }
        public MessageBus Messages { get; }
        public ILog Log { get; }

        public ModContextImpl(ModManifest manifest, World world, SystemGroup systems, MessageBus messages)
        {
            ModId = manifest.ModId;
            Version = manifest.Version;
            World = world;
            Systems = systems;
            Messages = messages;
            Log = new UnityLog();
        }
    }
}
