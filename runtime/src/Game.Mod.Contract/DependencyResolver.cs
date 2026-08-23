using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Mod.Contract
{
    /// <summary>依赖解析异常。</summary>
    public sealed class ModDependencyException : Exception
    {
        public IReadOnlyList<ModId> Cycle { get; }

        public ModDependencyException(string message) : base(message) { Cycle = Array.Empty<ModId>(); }

        public ModDependencyException(IReadOnlyList<ModId> cycle)
            : base($"检测到循环依赖: {string.Join(" → ", cycle)}")
        {
            Cycle = cycle;
        }
    }

    /// <summary>
    /// 依赖图解析：拓扑排序（依赖在前）+ 环检测。
    /// </summary>
    public static class DependencyResolver
    {
        /// <summary>
        /// 对给定集合做拓扑排序，返回依赖在前的加载顺序。
        /// </summary>
        /// <param name="getId">取元素 ModId。</param>
        /// <param name="getDependencies">取元素声明的依赖 ModId 集合。</param>
        public static List<T> TopoSort<T>(
            IEnumerable<T> items,
            Func<T, ModId> getId,
            Func<T, IEnumerable<ModId>> getDependencies) where T : notnull
        {
            var list = items.ToList();
            var byId = list.ToDictionary(getId);
            var deps = list.ToDictionary(getId, x => getDependencies(x).Distinct().ToList());

            var state = new Dictionary<ModId, int>(); // 0=white 1=gray 2=black
            var stack = new List<ModId>();
            var order = new List<T>();

            bool Visit(ModId id)
            {
                state[id] = 1;
                stack.Add(id);

                foreach (var d in deps[id])
                {
                    if (!byId.ContainsKey(d)) continue; // 缺失依赖由 LoadOrder 单独校验

                    if (!state.TryGetValue(d, out var s))
                    {
                        state[d] = 0;
                        s = 0;
                    }

                    if (s == 0)
                    {
                        if (!Visit(d)) return false;
                    }
                    else if (s == 1)
                    {
                        // 找到环：从 stack 中 d 的位置到末尾
                        var start = stack.IndexOf(d);
                        var cycle = stack.Skip(start).ToList();
                        cycle.Add(d);
                        throw new ModDependencyException(cycle);
                    }
                }

                stack.RemoveAt(stack.Count - 1);
                state[id] = 2;
                order.Add(byId[id]);
                return true;
            }

            foreach (var item in list)
            {
                var id = getId(item);
                if (!state.TryGetValue(id, out var s)) state[id] = 0;
                if (state[id] == 0) Visit(id);
            }

            return order;
        }
    }

}
