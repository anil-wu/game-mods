using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Mod.Contract.Wire
{
    /// <summary>
    /// 通用纯数据（object?[]）↔ 二进制编解码（Rule 14 / §14.5）。
    /// 供"简单能力"与"动态路由"（如 console 命令转发）在不手写专用 Codec 的情况下跨 Mod 传纯数据。
    ///
    /// 仅承载纯数据：null / bool / int / uint / long / ulong / float / double / string / byte[] / 嵌套 object?[] / string[]。
    /// bytes 里不可能藏对象引用——热卸载边界由机制保证（§14.5）；不支持的类型直接抛异常（fail-fast）。
    ///
    /// 高频 / 强类型能力建议手写字段 Codec（PayloadWriter/Reader 直写，§14.12 零中间对象）；
    /// DataCodec 是"一次编解码换一个对象数组"的便利路径，定位是离散、低频、动态形状的场景。
    /// 线格式：field 1 = kinds 字节表（每个参数的 <see cref="Kind"/>），field 2..N+1 = 各参数值（嵌套数组递归）。
    /// </summary>
    public static class DataCodec
    {
        private const int KindsField = 1;
        private const int FirstValueField = 2;

        private enum Kind : byte
        {
            Null = 0, Bool = 1, Int = 2, UInt = 3, Long = 4, ULong = 5,
            Float = 6, Double = 7, String = 8, Bytes = 9, Array = 10,
        }

        /// <summary>编码 object?[] 为二进制载荷。</summary>
        public static PayloadBuffer Write(object?[]? args)
        {
            var w = new PayloadWriter();
            var items = args ?? Array.Empty<object?>();
            var kinds = new byte[items.Length];
            for (var i = 0; i < items.Length; i++) kinds[i] = (byte)KindOf(items[i]);
            w.WriteBytes(KindsField, kinds);
            for (var i = 0; i < items.Length; i++) WriteValue(w, FirstValueField + i, items[i]);
            return w.ToBuffer();
        }

        /// <summary>解码二进制载荷为 object?[]。</summary>
        public static object?[] Read(in PayloadReader reader)
        {
            if (!reader.TryReadBytes(KindsField, out var kinds) || kinds.Length == 0)
                return Array.Empty<object?>();
            var result = new object?[kinds.Length];
            for (var i = 0; i < kinds.Length; i++)
                result[i] = ReadValue(reader, FirstValueField + i, (Kind)kinds[i]);
            return result;
        }

        /// <summary>便捷：编码入参并准备调用（等同 Write(args)）。</summary>
        public static PayloadBuffer WriteArgs(params object?[] args) => Write(args);

        private static Kind KindOf(object? v) => v switch
        {
            null => Kind.Null,
            bool => Kind.Bool,
            int => Kind.Int,
            uint => Kind.UInt,
            long => Kind.Long,
            ulong => Kind.ULong,
            float => Kind.Float,
            double => Kind.Double,
            string => Kind.String,
            byte[] => Kind.Bytes,
            object?[] => Kind.Array, // string[] 经数组协变落入此分支（逐元素按 String 编码）
            _ => throw new ArgumentException(
                $"DataCodec 不支持的类型 '{v.GetType().Name}'（仅允许纯数据：基元/string/byte[]/嵌套数组，Rule 14）"),
        };

        private static void WriteValue(PayloadWriter w, int fieldId, object? v)
        {
            switch (v)
            {
                case null: break; // kind=Null，不落字段
                case bool b: w.WriteBool(fieldId, b); break;
                case int i: w.WriteInt32(fieldId, i); break;
                case uint u: w.WriteUInt32(fieldId, u); break;
                case long l: w.WriteInt64(fieldId, l); break;
                case ulong ul: w.WriteUInt64(fieldId, ul); break;
                case float f: w.WriteFloat(fieldId, f); break;
                case double d: w.WriteDouble(fieldId, d); break;
                case string s: w.WriteString(fieldId, s); break;
                case byte[] bs: w.WriteBytes(fieldId, bs); break;
                case object?[] arr: w.WriteBytes(fieldId, Write(arr).ToArray()); break;
            }
        }

        private static object? ReadValue(in PayloadReader reader, int fieldId, Kind kind)
        {
            switch (kind)
            {
                case Kind.Null: return null;
                case Kind.Bool: reader.TryReadBool(fieldId, out var b); return b;
                case Kind.Int: reader.TryReadInt32(fieldId, out var i); return i;
                case Kind.UInt: reader.TryReadUInt32(fieldId, out var u); return u;
                case Kind.Long: reader.TryReadInt64(fieldId, out var l); return l;
                case Kind.ULong: reader.TryReadUInt64(fieldId, out var ul); return ul;
                case Kind.Float: reader.TryReadFloat(fieldId, out var f); return f;
                case Kind.Double: reader.TryReadDouble(fieldId, out var d); return d;
                case Kind.String: reader.TryReadString(fieldId, out var s); return s;
                case Kind.Bytes: reader.TryReadBytes(fieldId, out var bs); return bs;
                case Kind.Array:
                    if (reader.TryReadView(fieldId, out var view))
                    {
                        var nested = new PayloadReader(in view);
                        return Read(in nested);
                    }
                    return Array.Empty<object?>();
                default:
                    throw new PayloadFormatException($"DataCodec 未知 kind: {(byte)kind}");
            }
        }
    }
}
