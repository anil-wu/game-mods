using System.Collections.Generic;
using Game.ECS;
using Game.Messaging;
using Game.Mod.Contract;
using Game.ModLoader;
using UnityEngine;

namespace Game.Runtime
{
    /// <summary>
    /// 运行时引导：装配框架服务（World / SystemGroup / MessageBus），
    /// 发现并解析 Mod 加载顺序，驱动 ECS 系统 tick。
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        private readonly World _world = new World();
        private readonly SystemGroup _systems = new SystemGroup();
        private readonly MessageBus _messages = new MessageBus();

        private void Awake()
        {
            // 1. 发现 Mod（扫描游戏 StreamingAssets 下的 mods/ 目录）
            var modsDir = System.IO.Path.Combine(Application.streamingAssetsPath, "mods");
            var packages = ModDiscovery.Discover(modsDir);

            // 2. 解析加载顺序（依赖校验 + 拓扑排序 + 环检测）
            var result = LoadOrder.Resolve(packages);
            if (!result.Success)
            {
                foreach (var err in result.Errors)
                    Debug.LogError($"[ModLoader] {err}");
                return;
            }

            foreach (var pkg in result.Order)
                Debug.Log($"[ModLoader] 加载顺序: {pkg.Manifest.InstanceId}");

            // TODO: 加载各 Mod 的 entryDll（Assembly.Load），实例化 IMod 并调用 OnLoad，
            // 将 World/Systems/Messages 注入 ModContext。此步在 Mirror/资产管线接入后实现。
        }

        private void Update()
        {
            // 单进程（Host）tick：所有系统各执行一次。
            // 服务端/客户端分离时改用 _systems.Update(_world, dt, SystemSide.Server/Client)。
            _systems.UpdateAll(_world, Time.deltaTime);
        }
    }
}
