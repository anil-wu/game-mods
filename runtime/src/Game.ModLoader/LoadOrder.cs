using System;
using System.Collections.Generic;
using System.Linq;
using Game.Mod.Contract;

namespace Game.ModLoader
{
    /// <summary>加载解析结果。</summary>
    public sealed class LoadResult
    {
        /// <summary>依赖在前的加载顺序（仅当 Errors 为空时有效）。</summary>
        public List<ModPackage> Order { get; }

        public List<string> Errors { get; }

        public bool Success => Errors.Count == 0;

        public LoadResult(List<ModPackage> order, List<string> errors)
        {
            Order = order;
            Errors = errors;
        }
    }

    /// <summary>
    /// 加载顺序解析：依赖校验 + 拓扑排序 + 环检测。
    /// </summary>
    public static class LoadOrder
    {
        public static LoadResult Resolve(IEnumerable<ModPackage> packages)
        {
            var list = packages.ToList();
            var byId = list.ToDictionary(p => p.Manifest.ModId);
            var errors = new List<string>();

            foreach (var p in list)
            {
                foreach (var (depId, range) in p.Manifest.Dependencies)
                {
                    if (!byId.TryGetValue(depId, out var dep))
                        errors.Add($"Mod '{p.Manifest.ModId}' 缺少依赖 '{depId}'");
                    else if (!range.SatisfiedBy(dep.Manifest.Version))
                        errors.Add($"Mod '{p.Manifest.ModId}' 依赖 '{depId}' 版本 {dep.Manifest.Version} 不满足 {range}");
                }
            }

            if (errors.Count > 0)
                return new LoadResult(new List<ModPackage>(), errors);

            try
            {
                var order = DependencyResolver.TopoSort(
                    list,
                    p => p.Manifest.ModId,
                    p => p.Manifest.Dependencies.Keys);
                return new LoadResult(order, errors);
            }
            catch (ModDependencyException e)
            {
                errors.Add(e.Message);
                return new LoadResult(new List<ModPackage>(), errors);
            }
        }
    }

}
