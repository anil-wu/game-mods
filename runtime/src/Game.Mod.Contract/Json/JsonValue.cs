using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Game.Mod.Contract.Json
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// 极简 JSON 值 + 解析器/序列化器（零外部依赖，netstandard2.1 兼容）。
    /// 仅用于 Mod 契约的 manifest 序列化，支持完整 JSON 子集：
    /// object / array / string(含转义) / number / true / false / null。
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; }

        private readonly object? _data;

        private JsonValue(JsonKind kind, object? data)
        {
            Kind = kind;
            _data = data;
        }

        public static JsonValue Null => new(JsonKind.Null, null);
        public static JsonValue Of(bool b) => new(JsonKind.Bool, b);
        public static JsonValue Of(long n) => new(JsonKind.Number, n);
        public static JsonValue Of(double n) => new(JsonKind.Number, n);
        public static JsonValue Of(string s) => new(JsonKind.String, s);
        public static JsonValue NewArray(List<JsonValue> items) => new(JsonKind.Array, items);
        public static JsonValue NewObject(Dictionary<string, JsonValue> members) => new(JsonKind.Object, members);

        public bool AsBool => Kind == JsonKind.Bool && (bool)_data!;

        public double AsNumber => Kind == JsonKind.Number
            ? (_data is long l ? (double)l : (double)_data!)
            : 0;

        public long AsLong => Kind == JsonKind.Number
            ? (_data is long l ? l : (long)(double)_data!)
            : 0;

        public string AsString => Kind == JsonKind.String ? (string)_data! : "";

        public List<JsonValue> AsArray =>
            Kind == JsonKind.Array ? (List<JsonValue>)_data! : throw new InvalidOperationException("不是数组");

        public Dictionary<string, JsonValue> AsObject =>
            Kind == JsonKind.Object ? (Dictionary<string, JsonValue>)_data! : throw new InvalidOperationException("不是对象");

        public JsonValue this[int index] => AsArray[index];
        public JsonValue this[string key] => AsObject.TryGetValue(key, out var v) ? v : Null;

        public string ToJsonString() => Serialize(this);

        public static JsonValue Parse(string text) => new Parser(text).ParseDocument();

        // ---------- 序列化 ----------

        private static string Serialize(JsonValue v)
        {
            var sb = new StringBuilder();
            Write(sb, v);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, JsonValue v)
        {
            switch (v.Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(v.AsBool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    if (v._data is long l) sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    else sb.Append(((double)v._data!).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String:
                    WriteString(sb, v.AsString);
                    break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < v.AsArray.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Write(sb, v.AsArray[i]);
                    }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    int j = 0;
                    foreach (var kv in v.AsObject)
                    {
                        if (j++ > 0) sb.Append(',');
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- 解析（递归下降） ----------

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) => _s = s;

            public JsonValue ParseDocument()
            {
                SkipWs();
                var v = ParseValue();
                SkipWs();
                if (_i != _s.Length) throw Error("多余字符");
                return v;
            }

            private JsonValue ParseValue()
            {
                SkipWs();
                if (_i >= _s.Length) throw Error("意外结束");
                return _s[_i] switch
                {
                    '{' => ParseObject(),
                    '[' => ParseArray(),
                    '"' => JsonValue.Of(ParseString()),
                    't' => ParseLiteral("true", JsonValue.Of(true)),
                    'f' => ParseLiteral("false", JsonValue.Of(false)),
                    'n' => ParseLiteral("null", JsonValue.Null),
                    _ => ParseNumber(),
                };
            }

            private void SkipWs()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private JsonValue ParseObject()
            {
                _i++; // '{'
                var dict = new Dictionary<string, JsonValue>();
                SkipWs();
                if (_i < _s.Length && _s[_i] == '}') { _i++; return JsonValue.NewObject(dict); }

                while (true)
                {
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != '"') throw Error("对象键应为字符串");
                    var key = ParseString();
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != ':') throw Error("缺少 ':'");
                    _i++;
                    dict[key] = ParseValue();
                    SkipWs();
                    if (_i >= _s.Length) throw Error("对象未闭合");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return JsonValue.NewObject(dict); }
                    throw Error("对象内应为 ',' 或 '}'");
                }
            }

            private JsonValue ParseArray()
            {
                _i++; // '['
                var list = new List<JsonValue>();
                SkipWs();
                if (_i < _s.Length && _s[_i] == ']') { _i++; return JsonValue.NewArray(list); }

                while (true)
                {
                    list.Add(ParseValue());
                    SkipWs();
                    if (_i >= _s.Length) throw Error("数组未闭合");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return JsonValue.NewArray(list); }
                    throw Error("数组内应为 ',' 或 ']'");
                }
            }

            private JsonValue ParseLiteral(string lit, JsonValue value)
            {
                if (_i + lit.Length > _s.Length || string.CompareOrdinal(_s, _i, lit, 0, lit.Length) != 0)
                    throw Error($"非法字面量 '{lit}'");
                _i += lit.Length;
                return value;
            }

            private string ParseString()
            {
                _i++; // '"'
                var sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    var c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (_i >= _s.Length) throw Error("非法转义");
                        var e = _s[_i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_i + 4 > _s.Length) throw Error("非法 \\u 转义");
                                sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16));
                                _i += 4;
                                break;
                            default: throw Error($"非法转义 '\\{e}'");
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                throw Error("字符串未闭合");
            }

            private JsonValue ParseNumber()
            {
                var start = _i;
                while (_i < _s.Length &&
                       (char.IsDigit(_s[_i]) || _s[_i] == '-' || _s[_i] == '+' || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E'))
                    _i++;

                var text = _s.Substring(start, _i - start);
                if (text.Length == 0) throw Error("非法数字");
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return JsonValue.Of(l);
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return JsonValue.Of(d);
                throw Error($"非法数字 '{text}'");
            }

            private FormatException Error(string msg) => new($"JSON 解析错误 @ {_i}: {msg}");
        }
    }

}
