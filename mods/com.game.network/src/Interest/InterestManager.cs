using System.Collections.Generic;
using Game.Mod.Contract;
using Game.Mod.Runtime;

namespace Com.Game.Network
{
    /// <summary>
    /// 兴趣管理（§11.8）：按 (owner, pid) 的 relevance 钩子注册表 + Broadcast 逐连接过滤。
    /// Network.Mod 只提供钩子与执行点，不拥有任何游戏概念（§11.1 硬边界）——
    /// 实体级兴趣（NetworkId/区域/队伍）由各游戏 Mod 在自己的 IInterestManager 里实现
    /// （内部查 ECS / 自身状态）。
    /// 默认无 manager = 全房间广播（现状语义，零行为变化）。
    /// </summary>
    internal sealed class InterestManager
    {
        private sealed class Registration
        {
            public ModId Owner;
            public IInterestManager Manager = null!;
        }

        private readonly Dictionary<ProtocolId, Registration> _interests = new();

        /// <summary>注册 / 清除（null = 清除回默认全发）。owner 校验由调用方（NetworkRuntimeImpl）执行。</summary>
        public void Register(ModId owner, ProtocolId pid, IInterestManager? manager)
        {
            if (manager is null)
            {
                _interests.Remove(pid);
                return;
            }
            _interests[pid] = new Registration { Owner = owner, Manager = manager };
        }

        /// <summary>Broadcast 逐连接过滤：未注册 interest → 放行；已注册 → 委托 manager 判定。</summary>
        public bool IsRelevant(int connectionId, ProtocolId pid, in object message)
        {
            if (!_interests.TryGetValue(pid, out var reg)) return true;
            return reg.Manager.IsRelevant(connectionId, pid, in message);
        }

        /// <summary>按 owner 清理（Rule 20）。</summary>
        public void UnregisterAll(ModId owner)
        {
            var stale = new List<ProtocolId>();
            foreach (var (pid, reg) in _interests)
                if (reg.Owner == owner) stale.Add(pid);
            foreach (var pid in stale) _interests.Remove(pid);
        }
    }
}
