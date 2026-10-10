using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BlockLibraryManager
{
    /// <summary>可被 XML/JSON 序列化的字符串列表（内部用 List&lt;string&gt;）。</summary>
    public sealed class StringList : List<string>
    {
        public StringList() : base() { }
        public StringList(IEnumerable<string> collection) : base(collection) { }
    }

    /// <summary>
    /// 一个刚好够用的 JSON 读写器。
    /// 之所以自己写而不用 System.Text.Json / Newtonsoft：
    /// 前者 net35(CAD2010~2011) 没有，后者需要额外随插件部署 DLL。
    /// 这里只处理本项目配置用到的：string / int / bool / 字符串数组。
    /// 解析失败一律返回默认值，不让配置问题把插件搞崩。
    /// </summary>
    public sealed class MiniJson
    {
        private readonly Dictionary<string, object> _values;

        private MiniJson(Dictionary<string, object> values)
        {
            _values = values ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        // ---------------- 读 ----------------

        public static MiniJson ParseObject(string json)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(json)) return new MiniJson(result);

            try
            {
                int index = 0;
                SkipWhitespace(json, ref index);

                if (index >= json.Length || json[index] != '{') return new MiniJson(result);
                index++;

                while (index < json.Length)
                {
                    SkipWhitespace(json, ref index);
                    if (index >= json.Length || json[index] == '}') break;

                    if (json[index] == ',') { index++; continue; }

                    string key = ParseString(json, ref index);
                    if (key == null) break;

                    SkipWhitespace(json, ref index);
                    if (index >= json.Length || json[index] != ':') break;
                    index++;

                    SkipWhitespace(json, ref index);
                    object value = ParseValue(json, ref index);

                    result[key] = value;
                }
            }
            catch
            {
                return new MiniJson(result);
            }

            return new MiniJson(result);
        }

        private static object ParseValue(string json, ref int index)
        {
            if (index >= json.Length) return null;

            char c = json[index];

            if (c == '"') return ParseString(json, ref index);
            if (c == '{') return ParseNested(json, ref index);
            if (c == '[') return ParseArray(json, ref index);

            int start = index;
            while (index < json.Length && ",}] \t\r\n".IndexOf(json[index]) < 0)
                index++;

            string raw = json.Substring(start, index - start).Trim();
            if (raw.Length == 0) return null;
            if (raw == "true") return true;
            if (raw == "false") return false;
            if (raw == "null") return null;
            return raw;
        }

        private static string ParseNested(string json, ref int index)
        {
            int depth = 0;
            int start = index;
            while (index < json.Length)
            {
                char c = json[index];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { index++; break; }
                }
                index++;
            }
            return json.Substring(start, index - start);
        }

        private static List<object> ParseArray(string json, ref int index)
        {
            List<object> items = new List<object>();

            index++; // '['
            while (index < json.Length)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length) break;
                if (json[index] == ']') { index++; break; }
                if (json[index] == ',') { index++; continue; }

                object v = ParseValue(json, ref index);
                items.Add(v);
            }
            return items;
        }

        private static string ParseString(string json, ref int index)
        {
            if (index >= json.Length || json[index] != '"') return null;

            index++;
            StringBuilder sb = new StringBuilder();

            while (index < json.Length)
            {
                char c = json[index++];

                if (c == '"') break;

                if (c == '\\' && index < json.Length)
                {
                    char e = json[index++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (index + 4 <= json.Length)
                            {
                                sb.Append((char)Convert.ToInt32(json.Substring(index, 4), 16));
                                index += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
                index++;
        }

        private object GetRaw(string key)
        {
            object v;
            if (_values.TryGetValue(key, out v)) return v;
            return null;
        }

        public static string GetString(MiniJson obj, string key, string defaultValue)
        {
            if (obj == null) return defaultValue;
            object v = obj.GetRaw(key);
            return v == null ? defaultValue : v.ToString();
        }

        public static int GetInt(MiniJson obj, string key, int defaultValue)
        {
            if (obj == null) return defaultValue;
            object v = obj.GetRaw(key);
            if (v == null) return defaultValue;

            string s = v.ToString();
            int result;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return result;
            return defaultValue;
        }

        public static bool GetBool(MiniJson obj, string key, bool defaultValue)
        {
            if (obj == null) return defaultValue;
            object v = obj.GetRaw(key);
            if (v == null) return defaultValue;

            if (v is bool) return (bool)v;

            string s = v.ToString();
            bool result;
            if (bool.TryParse(s, out result)) return result;
            return defaultValue;
        }

        public static void GetStringList(MiniJson obj, string key, StringList target)
        {
            if (obj == null || target == null) return;

            object v = obj.GetRaw(key);
            if (v == null) return;

            List<object> list = v as List<object>;
            if (list == null) return;

            foreach (object item in list)
            {
                if (item == null) continue;
                string s = item.ToString();
                if (s.Length == 0) continue;
                target.Add(s);
            }
        }

        // ---------------- 写 ----------------

        public static string Escape(string text)
        {
            if (text == null) return string.Empty;

            StringBuilder sb = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static string MakeString(string key, string value)
        {
            return "\"" + key + "\": \"" + Escape(value ?? string.Empty) + "\"";
        }

        public static string MakeNumber(string key, int value)
        {
            return "\"" + key + "\": " + value.ToString(CultureInfo.InvariantCulture);
        }

        public static string MakeBool(string key, bool value)
        {
            return "\"" + key + "\": " + (value ? "true" : "false");
        }

        public static void AppendStringList(StringBuilder sb, string key, StringList list)
        {
            sb.Append("\"").Append(key).Append("\": [");

            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append("\"").Append(Escape(list[i])).Append("\"");
                }
            }

            sb.Append("]");
        }
    }
}
