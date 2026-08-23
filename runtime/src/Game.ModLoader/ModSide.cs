using Game.Mod.Contract;

namespace Game.ModLoader
{
    /// <summary>运行时端侧。</summary>
    public enum ModSide
    {
        Client,
        Server,
    }

    public static class ModSideFilter
    {
        /// <summary>
        /// 按端侧过滤文件。v1 规则：asset 仅在客户端加载；code/data 双端加载。
        /// code 的 server/client 拆分通过文件名约定（*.server.dll）在程序集加载层处理。
        /// </summary>
        public static bool ShouldLoad(FileType type, ModSide side) => type switch
        {
            FileType.Asset => side == ModSide.Client,
            _ => true,
        };

        /// <summary>判断程序集文件名是否属于指定端侧。</summary>
        public static bool AssemblyBelongsTo(string fileName, ModSide side)
        {
            var lower = fileName.ToLowerInvariant();
            if (lower.EndsWith(".server.dll")) return side == ModSide.Server;
            if (lower.EndsWith(".client.dll")) return side == ModSide.Client;
            return true; // 无后缀 = shared，双端加载
        }
    }

}
