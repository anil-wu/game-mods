using System.IO;
using Game.Mod.Runtime;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>
    /// 通用运行时引导（V2 基础层）：装配 Core 能力 → 加载 Mods（框架 Mod 在前）→ 接线网络层。
    /// 运行时不含任何游戏特定逻辑——游戏循环由各 Mod 驱动（§18：Core 管运行时，Mod 管自己的业务）。
    /// </summary>
    public static class ModRuntime
    {
        public static ModRuntimeHost Host { get; private set; } = null!;
        public static bool Initialized { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Host 角色：同时运行 Client + Server 两套逻辑（§6/§11.3）
            Host = new ModRuntimeHost(RuntimeRole.Host, new UnityLog());
            Host.Manager.ModLoaded += InstantiateViews;

            var modsDir = Path.Combine(Application.streamingAssetsPath, "mods");
            var errors = Host.LoadDirectory(modsDir);
            foreach (var e in errors)
                Debug.LogError($"[ModRuntime] {e}");

            // 网络接线：Network.Mod（Framework Mod）已注册 INetworkRuntime →
            // 接入 Mirror 传输提供者（Provider 层可替换，§11.2）并启动 Host
            if (Host.Services.TryGet(WellKnownServices.NetworkRuntime, out var svc))
            {
                var runtime = (INetworkRuntime)svc;
                var go = new GameObject("MirrorTransport");
                var transport = go.AddComponent<MirrorTransport>();
                runtime.AttachTransport(transport);
                runtime.Start(new NetworkConfig { Role = NetworkRole.Host, Path = NetworkPath.Auto });
            }

            // 主循环驱动：帧开始重置消息配额 → 系统执行（§14.7 固定相位）
            var driver = new GameObject("ModRuntimeDriver");
            driver.AddComponent<ModRuntimeDriver>();

            Initialized = errors.Count == 0;
        }

        /// <summary>实例化 Mod 自带的视图（MonoBehaviour）——通用机制，运行时无需感知具体 Mod。</summary>
        private static void InstantiateViews(ModObject mod)
        {
            foreach (var asm in mod.Assemblies)
            {
                foreach (var t in asm.GetTypes())
                {
                    if (typeof(MonoBehaviour).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(MonoBehaviour))
                    {
                        var go = new GameObject(t.Name);
                        go.AddComponent(t);
                        Debug.Log($"[ModRuntime] 实例化视图 {t.Name}（{mod.Info.Id}）");
                    }
                }
            }
        }
    }

    /// <summary>主循环驱动器：ModRuntimeHost.Tick（消息配额相位 + ECS 系统）。</summary>
    public sealed class ModRuntimeDriver : MonoBehaviour
    {
        private void Update() => ModRuntime.Host.Tick(Time.deltaTime);
    }
}
