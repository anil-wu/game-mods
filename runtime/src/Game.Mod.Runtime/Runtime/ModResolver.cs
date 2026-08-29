using System.Collections.Generic;
using System.Linq;
using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>依赖解析结果。</summary>
    public sealed class ResolveResult
    {
        /// <summary>依赖在前的加载顺序（仅当 Errors 为空时有效）。</summary>
        public List<ModManifest> Order { get; } = new();

        public List<string> Errors { get; } = new();

        /// <summary>被跳过的可选依赖（ModId → 原因），供降级策略参考（§12.4 Optional）。</summary>
        public Dictionary<ModId, string> SkippedOptional { get; } = new();

        public bool Success => Errors.Count == 0;
    }

    /// <summary>
    /// ModResolver（§2/§12.3）：依赖 / 版本统一处理——DAG、SemVer、环检测、Client/Server Scope 过滤（Rule 8）。
    /// 依赖必须形成 DAG（§12.8）；版本冲突判定 No compatible version，不能同时加载两个版本。
    /// </summary>
    public static class ModResolver
    {
        /// <summary>解析加载计划。hasClient/hasServer 决定 Client/Server Scope 依赖是否生效（§12.6）。</summary>
        public static ResolveResult Resolve(IEnumerable<ModManifest> manifests, bool hasClient, bool hasServer)
        {
            var result = new ResolveResult();
            var list = manifests.ToList();
            var byId = list.ToDictionary(m => m.ModId);

            foreach (var m in list)
            {
                foreach (var dep in ApplicableDependencies(m, hasClient, hasServer))
                {
                    if (!byId.TryGetValue(dep.Id, out var target))
                    {
                        if (dep.Optional)
                            result.SkippedOptional[dep.Id] = $"'{m.ModId}' 的可选依赖 '{dep.Id}' 不存在，已跳过";
                        else
                            result.Errors.Add($"Mod '{m.ModId}' 缺少依赖 '{dep.Id}'");
                    }
                    else if (!dep.Version.SatisfiedBy(target.Version))
                    {
                        if (dep.Optional)
                            result.SkippedOptional[dep.Id] =
                                $"'{m.ModId}' 的可选依赖 '{dep.Id}' 版本 {target.Version} 不满足 {dep.Version}，已跳过";
                        else
                            result.Errors.Add(
                                $"Mod '{m.ModId}' 依赖 '{dep.Id}' 版本 {target.Version} 不满足 {dep.Version}（No compatible version）");
                    }
                }
            }

            if (result.Errors.Count > 0) return result;

            try
            {
                var skipped = result.SkippedOptional.Keys.ToHashSet();
                result.Order.AddRange(DependencyResolver.TopoSort(
                    list,
                    m => m.ModId,
                    m => ApplicableDependencies(m, hasClient, hasServer)
                        .Select(d => d.Id)
                        .Where(id => !skipped.Contains(id))));
            }
            catch (ModDependencyException e)
            {
                result.Errors.Add(e.Message);
            }
            return result;
        }

        /// <summary>按运行环境过滤依赖（§12.6：Client Scope 依赖只在 HasClient 环境生效）。</summary>
        public static IEnumerable<ModDependency> ApplicableDependencies(ModManifest m, bool hasClient, bool hasServer)
        {
            foreach (var d in m.Dependencies)
            {
                var applies = d.Scope switch
                {
                    ModScope.Shared => true,
                    ModScope.Client => hasClient,
                    ModScope.Server => hasServer,
                    _ => true,
                };
                if (applies) yield return d;
            }
        }
    }
}
