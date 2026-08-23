using System.IO;
using Game.ECS;
using Game.Messaging;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>
    /// 通用运行时引导（基础层）：装配框架服务，加载 Mods（核心 Mod + 第三方 Mod）。
    /// 运行时不含任何游戏特定逻辑——游戏循环由各 Mod 自带的视图驱动。
    /// </summary>
    public static class ModRuntime
    {
        public static World World { get; private set; } = null!;
        public static MessageBus Messages { get; private set; } = null!;
        public static SystemGroup Systems { get; private set; } = null!;
        public static ModLoaderService Loader { get; private set; } = null!;
        public static bool Initialized { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            World = new World();
            Messages = new MessageBus();
            Systems = new SystemGroup(Messages);
            Loader = new ModLoaderService();

            var modsDir = Path.Combine(Application.streamingAssetsPath, "mods");
            Loader.Load(World, Systems, Messages, modsDir);
            Initialized = true;
        }
    }
}
