using Game.ECS;
using Game.Mod.Runtime;

namespace Com.Test.Plugin
{
    /// <summary>合成测试插件 Mod：供 ModStoreTests 经"服务器下载 → 本地安装 → 动态启动"全流程验证。
    /// 不编译进 TestRunner（ReferenceOutputAssembly=false），以独立 DLL 形式被假服务器分发。</summary>
    public sealed class PluginMod : IMod
    {
        public struct Marker : IComponent { public int Value; }

        public sealed class NoopSystem : ISystem
        {
            public void Update(SystemContext context) { }
        }

        public static int RegisterCount;

        public void Register(IModContext context)
        {
            RegisterCount++;
            context.Ecs.RegisterComponent(typeof(Marker));
            context.Ecs.RegisterSystem(new NoopSystem(), SystemSide.Shared);
            context.Log.Info($"插件 Mod '{context.Info.Id}' v{context.Info.Version} 已动态加载");
        }

        public void Unregister(IModContext context)
        {
            context.Log.Info($"插件 Mod '{context.Info.Id}' 已注销");
        }
    }
}
