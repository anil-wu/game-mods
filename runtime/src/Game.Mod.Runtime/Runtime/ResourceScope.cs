using System;
using System.Collections.Generic;
using System.IO;
using Game.Mod.Contract;

namespace Game.Mod.Runtime
{
    /// <summary>
    /// 文件资源后端：Mod 包目录即资源命名空间（原型默认后端，§8.2 Mod 可换管线）。
    /// 文本类（.json/.txt/.csv/.xml/.yaml）返回 string，其余返回 byte[]。
    /// </summary>
    public sealed class FileResourceBackend : IResourceBackend
    {
        public static readonly FileResourceBackend Instance = new();

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".txt", ".csv", ".xml", ".yaml", ".yml", ".md",
        };

        public object? Load(string contentRoot, string localPath)
        {
            var normalized = localPath.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(contentRoot, normalized));

            // 路径逃逸防护：资源不得越过 Mod 包根目录（§8.4 隔离的一部分）
            var root = Path.GetFullPath(contentRoot);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ResourceAccessException($"资源路径越出 Mod 包根目录: '{localPath}'");

            if (!File.Exists(full)) return null;
            return TextExtensions.Contains(Path.GetExtension(full))
                ? File.ReadAllText(full)
                : File.ReadAllBytes(full);
        }

        public void Release(object resource) { /* 文件后端无底层句柄 */ }
    }

    /// <summary>
    /// ModObject 的资源域实现（§8.1/§8.8）：引用计数 + 子域 + 跨 Mod 受限解析。
    /// 跨 Mod 默认禁止直接访问资源（§8.4）：Load 仅限本命名空间；Resolve 需经 ModManager 校验依赖。
    /// </summary>
    public sealed class ModResourceScope : IModResourceScope
    {
        private sealed class Entry
        {
            public object Resource = null!;
            public int RefCount;
        }

        private readonly ModId _owner;
        private readonly string? _contentRoot;
        private readonly IResourceBackend _backend;
        private readonly Func<AssetId, IModResourceScope?> _resolveExternal;
        private readonly Dictionary<AssetId, Entry> _loaded = new();
        private readonly List<ModAssetScope> _children = new();
        private bool _disposed;

        public ModResourceScope(
            ModId owner,
            string? contentRoot,
            IResourceBackend backend,
            Func<AssetId, IModResourceScope?> resolveExternal)
        {
            _owner = owner;
            _contentRoot = contentRoot;
            _backend = backend;
            _resolveExternal = resolveExternal;
        }

        public object Load(AssetId id)
        {
            ThrowIfDisposed();
            if (id.Mod != _owner)
                throw new ResourceAccessException(
                    $"Mod '{_owner}' 不能直接访问其他 Mod 的资源 '{id}'（§8.4：跨 Mod 用 Resolve 或能力调用）");
            return LoadLocal(id, track: this);
        }

        public object Resolve(AssetId id)
        {
            ThrowIfDisposed();
            if (id.Mod == _owner) return Load(id);
            var scope = _resolveExternal(id)
                ?? throw new NoModException(id.Mod);
            return scope.Load(id);
        }

        public void Release(AssetId id) => ReleaseLocal(id);

        public IAssetScope CreateScope()
        {
            ThrowIfDisposed();
            var child = new ModAssetScope(this);
            _children.Add(child);
            return child;
        }

        public void ReleaseAll()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var child in _children.ToArray())
                child.Dispose();
            foreach (var (id, entry) in _loaded)
                _backend.Release(entry.Resource);
            _loaded.Clear();
            // 整域释放（§8.8）：AssetBundle 后端卸载整个 Bundle
            if (_backend is IScopedResourceBackend scoped && _contentRoot is not null)
                scoped.ReleaseScope(_contentRoot);
        }

        // ---- 内部 ----

        internal object LoadLocal(AssetId id, object track)
        {
            if (_loaded.TryGetValue(id, out var entry))
            {
                entry.RefCount++;
                return entry.Resource;
            }

            var resource = _contentRoot is null
                ? null
                : _backend.Load(_contentRoot, id.Path);
            if (resource is null)
                throw new ResourceAccessException($"资源不存在: '{id}'");

            _loaded[id] = new Entry { Resource = resource, RefCount = 1 };
            return resource;
        }

        internal void ReleaseLocal(AssetId id)
        {
            if (!_loaded.TryGetValue(id, out var entry)) return;
            if (--entry.RefCount <= 0)
            {
                _backend.Release(entry.Resource);
                _loaded.Remove(id);
            }
        }

        internal bool HasLoaded(AssetId id) => _loaded.ContainsKey(id);
        internal int LoadedCount => _loaded.Count;

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ModStateException($"Mod '{_owner}' 的资源域已释放");
        }

        void IDisposable.Dispose() => ReleaseAll();

        /// <summary>子域：共享父域缓存，跟踪自己的引用以便整域释放（§8.8）。</summary>
        private sealed class ModAssetScope : IAssetScope
        {
            private readonly ModResourceScope _parent;
            private readonly HashSet<AssetId> _mine = new();
            private bool _disposed;

            public ModAssetScope(ModResourceScope parent) => _parent = parent;

            public object Load(AssetId id)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ModAssetScope));
                var r = _parent.Load(id);
                _mine.Add(id);
                return r;
            }

            public void Release(AssetId id)
            {
                if (_disposed) return;
                if (_mine.Remove(id))
                    _parent.ReleaseLocal(id);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var id in _mine)
                    _parent.ReleaseLocal(id);
                _mine.Clear();
                _parent._children.Remove(this);
            }
        }
    }
}
