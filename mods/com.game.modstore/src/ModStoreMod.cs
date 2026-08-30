using System.Linq;
using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace Com.Game.ModStore
{
    /// <summary>
    /// Mod 商店（com.game.modstore）：呈现 mod_server 上的 Mod 列表，
    /// 按依赖闭包下载安装到本地 mods 目录，并启动 Mod（§18：Mod 商店 / UGC 闭环）。
    ///
    /// 对外导出能力（§12.11，其他 Mod / 控制台可经 ModCall 使用）：
    /// - modstore:list               → string[]（"modId@version" 列表）
    /// - modstore:install_and_start  → bool（args = "modId@version"）
    /// </summary>
    public sealed class ModStoreMod : IMod
    {
        public static readonly ModId ModIdValue = new("com.game.modstore");

        /// <summary>能力：列出服务器 Mod（§12.11 生成包装的手写等价物）。</summary>
        public static readonly CapabilityId ListCap = new(ModIdValue, "list");

        /// <summary>能力：安装并启动指定 Mod（args = "modId@version" 字符串）。</summary>
        public static readonly CapabilityId InstallAndStartCap = new(ModIdValue, "install_and_start");

        /// <summary>商店服务（视图与测试的访问点；Mod 内部状态）。</summary>
        public static ModStoreService Service { get; private set; } = null!;

        public void Register(IModContext context)
        {
            var serverUrl = System.Environment.GetEnvironmentVariable("MOD_SERVER_URL")
                ?? "http://localhost:5000";
            var localModsDir = System.Environment.GetEnvironmentVariable("MOD_STORE_DIR")
                ?? System.IO.Path.Combine(System.Environment.CurrentDirectory, "mods_downloaded");

            Service = new ModStoreService(context, serverUrl, localModsDir);

            // 导出能力（归属本 ModObject，卸载时自动撤销，§13.5；二进制 ABI，Rule 14）
            context.Mods.Export(ListCap, reader =>
            {
                var mods = Service.ListRemoteMods().GetAwaiter().GetResult()
                    .Select(m => m.ToString()).ToArray();
                return DataCodec.Write(mods);
            });
            context.Mods.Export(InstallAndStartCap, reader =>
            {
                var args = DataCodec.Read(reader);
                var ok = false;
                if (args.Length > 0 && TryParseInstance(args[0] as string, out var id, out var version))
                {
                    try
                    {
                        Service.InstallAndStart(id, version).GetAwaiter().GetResult();
                        ok = true;
                    }
                    catch (System.Exception e)
                    {
                        context.Log.Error($"[ModStore] 安装并启动失败: {e.Message}");
                    }
                }
                return DataCodec.Write(new object?[] { ok });
            });

            context.Log.Info($"Mod 商店 '{context.Info.Id}' v{context.Info.Version} 已注册（服务器: {serverUrl}，本地目录: {localModsDir}）");
        }

        public void Unregister(IModContext context)
        {
            Service?.Dispose();
            Service = null!;
            context.Log.Info($"Mod 商店 '{context.Info.Id}' 已注销");
        }

        private static bool TryParseInstance(string? text, out ModId id, out ModVersion version)
        {
            id = default;
            version = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var at = text.IndexOf('@');
            if (at <= 0 || at == text.Length - 1) return false;
            return ModId.TryParse(text.Substring(0, at), out id)
                && ModVersion.TryParse(text.Substring(at + 1), out version);
        }
    }
}
