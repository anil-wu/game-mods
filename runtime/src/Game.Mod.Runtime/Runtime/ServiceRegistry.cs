using System.Collections.Generic;
using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>
    /// 服务注册表：本端服务定位（§12.11 IServiceContext）。
    /// 归属可追踪（§12.10）：每个注册关联 ModObject，卸载时强制 UnregisterAll(owner)。
    /// 框架 Mod（UI.Mod / Network.Mod）经周知 ServiceId 发布基础设施实现（§10.2）。
    /// </summary>
    public sealed class ServiceRegistry
    {
        private readonly Dictionary<ServiceId, (ModId Owner, object Service)> _services = new();

        public void Register(ModId owner, ServiceId id, object service)
        {
            if (service is null) throw new System.ArgumentNullException(nameof(service));
            _services[id] = (owner, service);
        }

        public void Unregister(ServiceId id) => _services.Remove(id);

        public object Get(ServiceId id)
        {
            if (_services.TryGetValue(id, out var entry)) return entry.Service;
            throw new NoServiceException(id);
        }

        public bool TryGet(ServiceId id, out object service)
        {
            if (_services.TryGetValue(id, out var entry))
            {
                service = entry.Service;
                return true;
            }
            service = null!;
            return false;
        }

        /// <summary>某 Mod 注册的服务数（卸载泄漏报告用）。</summary>
        public int CountOf(ModId owner)
        {
            var n = 0;
            foreach (var entry in _services.Values)
                if (entry.Owner == owner) n++;
            return n;
        }

        /// <summary>卸载某 Mod 注册的全部服务（Core 强制，不依赖 Mod 自觉）。</summary>
        public void UnregisterAll(ModId owner)
        {
            var stale = new List<ServiceId>();
            foreach (var (id, entry) in _services)
                if (entry.Owner == owner) stale.Add(id);
            foreach (var id in stale) _services.Remove(id);
        }
    }
}
