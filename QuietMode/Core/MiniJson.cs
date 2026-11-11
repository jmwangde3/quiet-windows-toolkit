using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QuietMode.Core
{
    /// <summary>
    /// 托盘侧的极简 JSON 读取器。托盘刻意不引用 QuietMode.Core.dll（部署只要两个 exe），
    /// 所以自带这一份只读实现。只做「读」，不做序列化 —— 写 JSON 是 qmctl 的事。
    /// 解析失败会抛 FormatException 并带位置，调用方转成界面上的「读取失败」。
    /// </summary>
    internal enum JKind
    {
        Null = 0,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    internal sealed class JNode
    {
        public JKind Kind = JKind.Null;
        public bool BoolValue;
        public double NumberValue;
        public string StringValue = "";
        public List<JNode> Items;
        public Dictionary<string, JNode> Members;

        public static readonly JNode Null = new JNode();

        public bool IsNull { get { return Kind == JKind.Null; } }

        public int Count
        {
            get
            {
                if (Kind == JKind.Array) return Items == null ? 0 : Items.Count;
                if (Kind == JKind.Object) return Members == null ? 0 : Members.Count;
                return 0;
            }
        }

        public JNode this[string key]
        {
            get
            {
                if (Kind != JKind.Object || Members == null) return Null;
                JNode v;
                return Members.TryGetValue(key, out v) ? v : Null;
            }
        }

        public JNode this[int index]
        {
            get
            {
                if (Kind != JKind.Array || Items == null || index < 0 || index >= Items.Count) return Null;
                return Items[index];
            }
        }

        public bool Has(string key)
        {
            return Kind == JKind.Object && Members != null && Members.ContainsKey(key);
        }

        public string AsString(string fallback = "")
        {
            if (Kind == JKind.String) return StringValue;
            if (Kind == JKind.Number) return NumberValue.ToString(CultureInfo.InvariantCulture);
            if (Kind == JKind.Bool) return BoolValue ? "true" : "false";
            return fallback;
        }

        public bool AsBool(bool fallback = false)
        {
            if (Kind == JKind.Bool) return BoolValue;
            if (Kind == JKind.Number) return Math.Abs(NumberValue) > double.Epsilon;
            if (Kind == JKind.String)
            {
                string s = StringValue.Trim();
                if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return fallback;
        }

        public int AsInt(int fallback = 0)
        {
            if (Kind == JKind.Number) return (int)Math.Round(NumberValue);
            if (Kind == JKind.String)
            {
                int n;
                if (int.TryParse(StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            }
            if (Kind == JKind.Bool) return BoolValue ? 1 : 0;
            return fallback;
        }

        public long AsLong(long fallback = 0)
        {
            if (Kind == JKind.Number) return (long)Math.Round(NumberValue);
            if (Kind == JKind.String)
            {
                long n;
                if (long.TryParse(StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            }
            return fallback;
        }

        /// <summary>可空布尔：JSON null / 缺失 → null（「未知」，不是 false）。</summary>
        public bool? AsNullableBool()
        {
            if (Kind == JKind.Null) return null;
            if (Kind == JKind.Bool) return BoolValue;
            if (Kind == JKind.Number) return Math.Abs(NumberValue) > double.Epsilon;
            return null;
        }
    }

    internal static class MiniJson
    {
        public static JNode Parse(string text)
        {
            if (text == null) throw new FormatException("JSON 为空");
            int i = 0;
            JNode node = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i < text.Length) throw Error("结尾有多余字符", text, i);
            return node;
        }

        private static JNode ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw Error("意外结束", s, i);
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return new JNode { Kind = JKind.String, StringValue = ParseString(s, ref i) };
                case 't':
                    Expect(s, ref i, "true");
                    return new JNode { Kind = JKind.Bool, BoolValue = true };
                case 'f':
                    Expect(s, ref i, "false");
                    return new JNode { Kind = JKind.Bool, BoolValue = false };
                case 'n':
                    Expect(s, ref i, "null");
                    return JNode.Null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        private static JNode ParseObject(string s, ref int i)
        {
            JNode node = new JNode { Kind = JKind.Object, Members = new Dictionary<string, JNode>(StringComparer.Ordinal) };
            i++;   // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return node; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw Error("对象键必须是字符串", s, i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw Error("缺少冒号", s, i);
                i++;
                node.Members[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw Error("对象未闭合", s, i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return node; }
                throw Error("对象中出现意外字符 '" + s[i] + "'", s, i);
            }
        }

        private static JNode ParseArray(string s, ref int i)
        {
            JNode node = new JNode { Kind = JKind.Array, Items = new List<JNode>() };
            i++;   // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return node; }
            while (true)
            {
                node.Items.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw Error("数组未闭合", s, i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return node; }
                throw Error("数组中出现意外字符 '" + s[i] + "'", s, i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            StringBuilder b = new StringBuilder();
            i++;   // "
            while (true)
            {
                if (i >= s.Length) throw Error("字符串未闭合", s, i);
                char c = s[i++];
                if (c == '"') return b.ToString();
                if (c != '\\') { b.Append(c); continue; }
                if (i >= s.Length) throw Error("转义未完成", s, i);
                char e = s[i++];
                switch (e)
                {
                    case '"': b.Append('"'); break;
                    case '\\': b.Append('\\'); break;
                    case '/': b.Append('/'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw Error("\\u 转义不完整", s, i);
                        b.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: throw Error("未知转义 \\" + e, s, i);
                }
            }
        }

        private static JNode ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            string raw = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw Error("非法数字 '" + raw + "'", s, start);
            return new JNode { Kind = JKind.Number, NumberValue = d };
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw Error("期望 " + literal, s, i);
            i += literal.Length;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        private static FormatException Error(string message, string text, int index)
        {
            int line = 1, col = 1;
            for (int k = 0; k < index && k < text.Length; k++)
            {
                if (text[k] == '\n') { line++; col = 1; }
                else col++;
            }
            return new FormatException("JSON 解析失败（第 " + line + " 行第 " + col + " 列）：" + message);
        }
    }
}
