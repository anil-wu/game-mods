using System;

namespace Game.Mod.Contract
{
    /// <summary>
    /// 版本范围（semver 语义），v1 支持单操作符：
    ///   "*" / ""     任意版本
    ///   "1.2.3"      精确
    ///   ">=1.2.3" / ">1.2.3" / "&lt;=1.2.3" / "&lt;1.2.3"
    ///   "^1.2.3"     兼容（major>0 时 &lt; next-major）
    ///   "~1.2.3"     补丁（&lt; next-minor）
    /// </summary>
    public sealed class VersionRange
    {
        private enum Op { Any, Exact, Gte, Gt, Lte, Lt, Caret, Tilde }

        public static readonly VersionRange Any = new(Op.Any, default);

        private readonly Op _op;
        private readonly ModVersion _ver;

        private VersionRange(Op op, ModVersion ver)
        {
            _op = op;
            _ver = ver;
        }

        public static VersionRange Parse(string? spec)
        {
            if (string.IsNullOrWhiteSpace(spec) || spec is "*" or "x" or "X")
                return Any;

            spec = spec.Trim();
            if (spec.StartsWith("^", StringComparison.Ordinal)) return new VersionRange(Op.Caret, ModVersion.Parse(spec.Substring(1)));
            if (spec.StartsWith("~", StringComparison.Ordinal)) return new VersionRange(Op.Tilde, ModVersion.Parse(spec.Substring(1)));
            if (spec.StartsWith(">=", StringComparison.Ordinal)) return new VersionRange(Op.Gte, ModVersion.Parse(spec.Substring(2)));
            if (spec.StartsWith("<=", StringComparison.Ordinal)) return new VersionRange(Op.Lte, ModVersion.Parse(spec.Substring(2)));
            if (spec.StartsWith(">", StringComparison.Ordinal)) return new VersionRange(Op.Gt, ModVersion.Parse(spec.Substring(1)));
            if (spec.StartsWith("<", StringComparison.Ordinal)) return new VersionRange(Op.Lt, ModVersion.Parse(spec.Substring(1)));
            return new VersionRange(Op.Exact, ModVersion.Parse(spec));
        }

        public bool SatisfiedBy(ModVersion v) => _op switch
        {
            Op.Any => true,
            Op.Exact => v == _ver,
            Op.Gte => v >= _ver,
            Op.Gt => v > _ver,
            Op.Lte => v <= _ver,
            Op.Lt => v < _ver,
            Op.Caret => v >= _ver && v < UpperCaret(),
            Op.Tilde => v >= _ver && v < UpperTilde(),
            _ => false,
        };

        private ModVersion UpperCaret() => _ver.Major > 0
            ? new ModVersion(_ver.Major + 1, 0, 0)
            : _ver.Minor > 0
                ? new ModVersion(0, _ver.Minor + 1, 0)
                : new ModVersion(0, 0, _ver.Patch + 1);

        private ModVersion UpperTilde() => new ModVersion(_ver.Major, _ver.Minor + 1, 0);

        public override string ToString() => _op switch
        {
            Op.Any => "*",
            Op.Exact => _ver.ToString(),
            Op.Gte => $">={_ver}",
            Op.Gt => $">{_ver}",
            Op.Lte => $"<={_ver}",
            Op.Lt => $"<{_ver}",
            Op.Caret => $"^{_ver}",
            Op.Tilde => $"~{_ver}",
            _ => "*",
        };
    }

}
