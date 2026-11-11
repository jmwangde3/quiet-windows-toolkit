using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QuietMode.Core.Json
{
    /// <summary>
    /// 零依赖 JSON 模型。写入顺序 = 添加顺序，保证配置文件 diff 稳定。
    /// 只实现 RFC 8259 严格子集，不吞注释、不吞尾逗号。
    /// </summary>
    public abstract class JsonNode
    {
        public static JsonNode Parse(string text)
        {
            if (text == null) throw new JsonParseException("输入为 null");
            JsonParser p = new JsonParser(text);
            JsonNode n = p.ParseValue();
            p.SkipWhitespace();
            if (!p.AtEnd) throw new JsonParseException("位置 " + p.Position + "：JSON 结束后仍有多余内容");
            return n;
        }

        public static bool TryParse(string text, out JsonNode node, out string error)
        {
            try
            {
                node = Parse(text);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                node = null;
                error = ex.Message;
                return false;
            }
        }

        public string ToJson(bool pretty)
        {
            StringBuilder sb = new StringBuilder(512);
            Write(sb, pretty, 0);
            return sb.ToString();
        }

        public override string ToString() { return ToJson(false); }

        internal abstract void Write(StringBuilder sb, bool pretty, int depth);

        public JsonObject AsObject()
        {
            JsonObject o = this as JsonObject;
            if (o == null) throw new JsonParseException("期望 JSON 对象，实际是 " + Kind);
            return o;
        }

        public JsonArray AsArray()
        {
            JsonArray a = this as JsonArray;
            if (a == null) throw new JsonParseException("期望 JSON 数组，实际是 " + Kind);
            return a;
        }

        public JsonObject TryObject() { return this as JsonObject; }
        public JsonArray TryArray() { return this as JsonArray; }

        public bool IsNull { get { return this is JsonNull; } }
        public string Kind { get { return GetType().Name; } }

        public string AsString()
        {
            JsonString s = this as JsonString;
            if (s != null) return s.Value;
            if (this is JsonNumber) return ((JsonNumber)this).Raw;
            if (this is JsonBool) return ((JsonBool)this).Value ? "true" : "false";
            if (this is JsonNull) return null;
            throw new JsonParseException("期望标量，实际是 " + Kind);
        }

        public bool AsBool()
        {
            JsonBool b = this as JsonBool;
            if (b != null) return b.Value;
            JsonString s = this as JsonString;
            if (s != null)
            {
                if (string.Equals(s.Value, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s.Value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            throw new JsonParseException("期望布尔值，实际是 " + Kind);
        }

        public long AsLong()
        {
            JsonNumber n = this as JsonNumber;
            if (n != null) return n.AsLong();
            JsonString s = this as JsonString;
            long v;
            if (s != null && long.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            throw new JsonParseException("期望整数，实际是 " + Kind);
        }

        public int AsInt() { return checked((int)AsLong()); }

        public double AsDouble()
        {
            JsonNumber n = this as JsonNumber;
            if (n != null) return n.AsDouble();
            throw new JsonParseException("期望数字，实际是 " + Kind);
        }

        internal static string Quote(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
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
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        internal static void Indent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++) sb.Append("  ");
        }
    }

    public sealed class JsonObject : JsonNode
    {
        private readonly List<KeyValuePair<string, JsonNode>> _items = new List<KeyValuePair<string, JsonNode>>();

        public int Count { get { return _items.Count; } }
        public IList<KeyValuePair<string, JsonNode>> Items { get { return _items; } }

        public bool ContainsKey(string name) { return IndexOf(name) >= 0; }

        public JsonNode Get(string name)
        {
            int i = IndexOf(name);
            return i < 0 ? null : _items[i].Value;
        }

        public JsonObject Set(string name, JsonNode value)
        {
            int i = IndexOf(name);
            if (i >= 0) _items[i] = new KeyValuePair<string, JsonNode>(name, value ?? JsonNull.Instance);
            else _items.Add(new KeyValuePair<string, JsonNode>(name, value ?? JsonNull.Instance));
            return this;
        }

        public JsonObject Set(string name, string value)
        {
            return Set(name, value == null ? (JsonNode)JsonNull.Instance : new JsonString(value));
        }

        public JsonObject Set(string name, bool value) { return Set(name, new JsonBool(value)); }
        public JsonObject Set(string name, int value) { return Set(name, new JsonNumber(value.ToString(CultureInfo.InvariantCulture))); }
        public JsonObject Set(string name, long value) { return Set(name, new JsonNumber(value.ToString(CultureInfo.InvariantCulture))); }

        public JsonObject SetObject(string name)
        {
            JsonObject o = new JsonObject();
            Set(name, o);
            return o;
        }

        public JsonObject SetArray(string name, out JsonArray array)
        {
            array = new JsonArray();
            Set(name, array);
            return this;
        }

        public bool Remove(string name)
        {
            int i = IndexOf(name);
            if (i < 0) return false;
            _items.RemoveAt(i);
            return true;
        }

        public string GetString(string name, string fallback)
        {
            JsonNode n = Get(name);
            if (n == null || n.IsNull) return fallback;
            return n.AsString();
        }

        public bool GetBool(string name, bool fallback)
        {
            JsonNode n = Get(name);
            if (n == null || n.IsNull) return fallback;
            return n.AsBool();
        }

        public int GetInt(string name, int fallback)
        {
            JsonNode n = Get(name);
            if (n == null || n.IsNull) return fallback;
            return n.AsInt();
        }

        public long GetLong(string name, long fallback)
        {
            JsonNode n = Get(name);
            if (n == null || n.IsNull) return fallback;
            return n.AsLong();
        }

        public JsonArray GetArray(string name)
        {
            JsonNode n = Get(name);
            return n == null || n.IsNull ? null : n.TryArray();
        }

        public JsonObject GetObject(string name)
        {
            JsonNode n = Get(name);
            return n == null || n.IsNull ? null : n.TryObject();
        }

        private int IndexOf(string name)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].Key, name, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        internal override void Write(StringBuilder sb, bool pretty, int depth)
        {
            if (_items.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            for (int i = 0; i < _items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                if (pretty) { sb.Append('\n'); Indent(sb, depth + 1); }
                sb.Append(Quote(_items[i].Key));
                sb.Append(pretty ? ": " : ":");
                _items[i].Value.Write(sb, pretty, depth + 1);
            }
            if (pretty) { sb.Append('\n'); Indent(sb, depth); }
            sb.Append('}');
        }
    }

    public sealed class JsonArray : JsonNode
    {
        private readonly List<JsonNode> _items = new List<JsonNode>();

        public int Count { get { return _items.Count; } }
        public JsonNode this[int index] { get { return _items[index]; } }

        public JsonArray Add(JsonNode value)
        {
            _items.Add(value ?? JsonNull.Instance);
            return this;
        }

        public JsonArray Add(string value)
        {
            _items.Add(value == null ? (JsonNode)JsonNull.Instance : new JsonString(value));
            return this;
        }

        public JsonArray Add(bool value) { return Add(new JsonBool(value)); }
        public JsonArray Add(int value) { return Add(new JsonNumber(value.ToString(CultureInfo.InvariantCulture))); }
        public JsonArray Add(long value) { return Add(new JsonNumber(value.ToString(CultureInfo.InvariantCulture))); }
        public JsonArray AddObject(out JsonObject item) { item = new JsonObject(); _items.Add(item); return this; }

        internal override void Write(StringBuilder sb, bool pretty, int depth)
        {
            if (_items.Count == 0) { sb.Append("[]"); return; }
            sb.Append('[');
            for (int i = 0; i < _items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                if (pretty) { sb.Append('\n'); Indent(sb, depth + 1); }
                _items[i].Write(sb, pretty, depth + 1);
            }
            if (pretty) { sb.Append('\n'); Indent(sb, depth); }
            sb.Append(']');
        }
    }

    public sealed class JsonString : JsonNode
    {
        public readonly string Value;
        public JsonString(string value) { Value = value; }
        internal override void Write(StringBuilder sb, bool pretty, int depth) { sb.Append(Quote(Value)); }
    }

    public sealed class JsonNumber : JsonNode
    {
        public readonly string Raw;
        public JsonNumber(string raw) { Raw = raw; }
        public new long AsLong()
        {
            long v;
            if (long.TryParse(Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return (long)AsDouble();
        }
        public new double AsDouble()
        {
            double d;
            if (double.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new JsonParseException("无法解析数字：" + Raw);
        }
        internal override void Write(StringBuilder sb, bool pretty, int depth) { sb.Append(Raw); }
    }

    public sealed class JsonBool : JsonNode
    {
        public readonly bool Value;
        public JsonBool(bool value) { Value = value; }
        internal override void Write(StringBuilder sb, bool pretty, int depth) { sb.Append(Value ? "true" : "false"); }
    }

    public sealed class JsonNull : JsonNode
    {
        public static readonly JsonNull Instance = new JsonNull();
        internal override void Write(StringBuilder sb, bool pretty, int depth) { sb.Append("null"); }
    }

    public sealed class JsonParseException : Exception
    {
        public JsonParseException(string message) : base(message) { }
    }

    internal sealed class JsonParser
    {
        private readonly string _s;
        private int _i;

        public JsonParser(string s) { _s = s; _i = 0; }

        public int Position { get { return _i; } }
        public bool AtEnd { get { return _i >= _s.Length; } }

        public void SkipWhitespace()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _i++;
                else break;
            }
        }

        public JsonNode ParseValue()
        {
            SkipWhitespace();
            if (_i >= _s.Length) throw Error("意外的输入结束");
            char c = _s[_i];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return new JsonString(ParseString());
                case 't': Expect("true"); return new JsonBool(true);
                case 'f': Expect("false"); return new JsonBool(false);
                case 'n': Expect("null"); return JsonNull.Instance;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                    throw Error("非法字符 '" + c + "'");
            }
        }

        private JsonObject ParseObject()
        {
            JsonObject o = new JsonObject();
            _i++; // {
            SkipWhitespace();
            if (_i < _s.Length && _s[_i] == '}') { _i++; return o; }
            while (true)
            {
                SkipWhitespace();
                if (_i >= _s.Length || _s[_i] != '"') throw Error("对象成员名必须是字符串");
                string name = ParseString();
                SkipWhitespace();
                if (_i >= _s.Length || _s[_i] != ':') throw Error("成员名后缺少 ':'");
                _i++;
                o.Set(name, ParseValue());
                SkipWhitespace();
                if (_i >= _s.Length) throw Error("对象未闭合");
                if (_s[_i] == ',') { _i++; continue; }
                if (_s[_i] == '}') { _i++; return o; }
                throw Error("对象中期望 ',' 或 '}'");
            }
        }

        private JsonArray ParseArray()
        {
            JsonArray a = new JsonArray();
            _i++; // [
            SkipWhitespace();
            if (_i < _s.Length && _s[_i] == ']') { _i++; return a; }
            while (true)
            {
                a.Add(ParseValue());
                SkipWhitespace();
                if (_i >= _s.Length) throw Error("数组未闭合");
                if (_s[_i] == ',') { _i++; continue; }
                if (_s[_i] == ']') { _i++; return a; }
                throw Error("数组中期望 ',' 或 ']'");
            }
        }

        private string ParseString()
        {
            _i++; // "
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length) throw Error("字符串未闭合");
                char c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (_i >= _s.Length) throw Error("转义未完成");
                    char e = _s[_i++];
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
                            if (_i + 4 > _s.Length) throw Error("\\u 转义长度不足");
                            string hex = _s.Substring(_i, 4);
                            int code;
                            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                throw Error("非法 \\u 转义：" + hex);
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default: throw Error("非法转义 \\" + e);
                    }
                }
                else if (c < 0x20) throw Error("字符串中出现未转义的控制字符");
                else sb.Append(c);
            }
        }

        private JsonNumber ParseNumber()
        {
            int start = _i;
            if (_i < _s.Length && _s[_i] == '-') _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            if (_i == start) throw Error("非法数字");
            return new JsonNumber(_s.Substring(start, _i - start));
        }

        private void Expect(string literal)
        {
            if (_i + literal.Length > _s.Length) throw Error("期望 '" + literal + "'");
            if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0) throw Error("期望 '" + literal + "'");
            _i += literal.Length;
        }

        private JsonParseException Error(string message)
        {
            int line = 1, col = 1;
            for (int k = 0; k < _i && k < _s.Length; k++)
            {
                if (_s[k] == '\n') { line++; col = 1; }
                else col++;
            }
            return new JsonParseException(string.Format(CultureInfo.InvariantCulture,
                "JSON 解析失败（第 {0} 行第 {1} 列）：{2}", line, col, message));
        }
    }
}
