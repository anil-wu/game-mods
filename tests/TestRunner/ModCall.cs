using Game.Mod.Contract;
using Game.Mod.Contract.Wire;
using Game.Mod.Runtime;

namespace TestRunner
{
    /// <summary>
    /// 测试辅助：二进制 ModCall（Rule 14）。编码入参（object?[] 纯数据）→ Call → 解码返回。
    /// 返回的 object?[] 与能力契约行集一致（字段布局见各 Mod 的 CONTRACT.md，Rule 19）。
    /// </summary>
    internal static class ModCall
    {
        public static object?[] Invoke(IModManager mods, ModId target, CapabilityId cap, params object?[] args)
        {
            var buf = mods.Call(target, cap, DataCodec.Write(args));
            return DataCodec.Read(new PayloadReader(buf));
        }

        /// <summary>取单个 bool 返回（能力的 [0]=bool 契约）。</summary>
        public static bool InvokeBool(IModManager mods, ModId target, CapabilityId cap, params object?[] args)
        {
            var r = Invoke(mods, target, cap, args);
            return r.Length > 0 && r[0] is bool b && b;
        }
    }
}
