using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Wolf
{
    // Minimal JSON reader/writer (no external deps, .NET FW 4.8 / C# 5).
    // Objects -> Dictionary<string, object>, arrays -> List<object>,
    // numbers -> double or long, strings -> string, true/false -> bool, null -> null.
    public static class Json
    {
        public static string Serialize(object value)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, value, 0, false);
            return sb.ToString();
        }

        public static string SerializePretty(object value)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, value, 0, true);
            return sb.ToString();
        }

        public static object Parse(string text)
        {
            if (text == null) { throw new FormatException("json is null"); }
            int pos = 0;
            object result = ParseValue(text, ref pos);
            SkipWs(text, ref pos);
            return result;
        }

        // Convenience: parse a JSON object into a dictionary, tolerating empty/blank input.
        public static Dictionary<string, object> ParseObject(string text)
        {
            if (string.IsNullOrEmpty(text)) { return new Dictionary<string, object>(); }
            object o = Parse(text);
            Dictionary<string, object> d = o as Dictionary<string, object>;
            return d ?? new Dictionary<string, object>();
        }

        public static void WriteFile(string path, object value, bool pretty)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
            File.WriteAllText(path, pretty ? SerializePretty(value) : Serialize(value), new UTF8Encoding(false));
        }

        public static Dictionary<string, object> ReadFile(string path)
        {
            if (!File.Exists(path)) { return new Dictionary<string, object>(); }
            try { return ParseObject(File.ReadAllText(path, Encoding.UTF8)); }
            catch { return new Dictionary<string, object>(); }
        }

        // ---------- typed getters (tolerant of missing/odd values) ----------

        public static string Str(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) { return def; }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int Int(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) { return def; }
            if (v is double) { return (int)(double)v; }
            if (v is long) { return (int)(long)v; }
            int parsed;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) ? parsed : def;
        }

        public static long Long(Dictionary<string, object> d, string key, long def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) { return def; }
            if (v is long) { return (long)v; }
            if (v is double) { return (long)(double)v; }
            long parsed;
            return long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) ? parsed : def;
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) { return def; }
            if (v is bool) { return (bool)v; }
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") { return true; }
            if (s == "false" || s == "0" || s == "no" || s == "off") { return false; }
            return def;
        }

        public static List<object> Arr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null) { return new List<object>(); }
            d.TryGetValue(key, out v);
            List<object> list = v as List<object>;
            return list ?? new List<object>();
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null) { return new Dictionary<string, object>(); }
            d.TryGetValue(key, out v);
            Dictionary<string, object> o = v as Dictionary<string, object>;
            return o ?? new Dictionary<string, object>();
        }

        // ---------- writer ----------

        private static void Write(StringBuilder sb, object value, int indent, bool pretty)
        {
            if (value == null) { sb.Append("null"); return; }

            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }

            if (value is double)
            {
                double dv = (double)value;
                if (double.IsNaN(dv) || double.IsInfinity(dv)) { sb.Append("0"); return; }
                sb.Append(dv.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is float) { sb.Append(((float)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is long || value is int || value is uint || value is ulong || value is short || value is ushort || value is byte || value is sbyte)
            {
                sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }

            IDictionary<string, object> sod = value as IDictionary<string, object>;
            if (sod != null) { WriteMap(sb, sod, indent, pretty); return; }

            IDictionary nonGeneric = value as IDictionary;
            if (nonGeneric != null)
            {
                Dictionary<string, object> copy = new Dictionary<string, object>();
                foreach (DictionaryEntry e in nonGeneric) { copy[Convert.ToString(e.Key, CultureInfo.InvariantCulture)] = e.Value; }
                WriteMap(sb, copy, indent, pretty);
                return;
            }

            IEnumerable seq = value as IEnumerable;
            if (seq != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in seq)
                {
                    if (!first) { sb.Append(','); }
                    first = false;
                    if (pretty) { sb.Append('\n'); Pad(sb, indent + 1); }
                    Write(sb, item, indent + 1, pretty);
                }
                if (pretty && !first) { sb.Append('\n'); Pad(sb, indent); }
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteMap(StringBuilder sb, IDictionary<string, object> map, int indent, bool pretty)
        {
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> kv in map)
            {
                if (!first) { sb.Append(','); }
                first = false;
                if (pretty) { sb.Append('\n'); Pad(sb, indent + 1); }
                WriteString(sb, kv.Key);
                sb.Append(':');
                if (pretty) { sb.Append(' '); }
                Write(sb, kv.Value, indent + 1, pretty);
            }
            if (pretty && !first) { sb.Append('\n'); Pad(sb, indent); }
            sb.Append('}');
        }

        private static void Pad(StringBuilder sb, int level)
        {
            for (int i = 0; i < level; i++) { sb.Append("  "); }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
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
                        if (c < 32 || c == 0x7f)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else { sb.Append(c); }
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- parser ----------

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { i++; }
                else { break; }
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) { throw new FormatException("unexpected end of json"); }
            char c = s[i];
            if (c == '{') { return ParseObjectBody(s, ref i); }
            if (c == '[') { return ParseArray(s, ref i); }
            if (c == '"') { return ParseString(s, ref i); }
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
            {
                throw new FormatException("bad literal at " + i);
            }
            i += word.Length;
        }

        private static Dictionary<string, object> ParseObjectBody(string s, ref int i)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') { throw new FormatException("expected key at " + i); }
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') { throw new FormatException("expected ':' at " + i); }
                i++;
                result[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return result; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            List<object> result = new List<object>();
            i++; // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }
            while (true)
            {
                result.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return result; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // '"'
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') { return sb.ToString(); }
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) { break; }
                char esc = s[i++];
                switch (esc)
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
                        if (i + 4 > s.Length) { throw new FormatException("bad \\u escape"); }
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(esc); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) { i++; }
            bool real = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+') { real = real || c == '.' || c == 'e' || c == 'E'; i++; continue; }
                break;
            }
            string token = s.Substring(start, i - start);
            if (token.Length == 0) { throw new FormatException("bad number at " + start); }
            double d;
            if (!double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
            {
                throw new FormatException("bad number '" + token + "'");
            }
            if (!real && d >= long.MinValue && d <= long.MaxValue && Math.Abs(d) < 9.007199254740992E15)
            {
                return (long)d;
            }
            return d;
        }
    }
}
