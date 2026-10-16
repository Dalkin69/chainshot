// SPDX-License-Identifier: MIT
// Minimal JSON reader/writer, enough for ChainShot files (objects, arrays, strings,
// numbers, booleans, null). Kept dependency-free so ChainShot.exe is a single file
// on .NET Framework 4.8. Numbers are kept as their source text: journal values are
// strings and must round-trip exactly.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ChainShot
{
    /// <summary>JSON object that keeps the order of its keys.</summary>
    public sealed class JsonObject : List<KeyValuePair<string, object>>
    {
        public object this[string key]
        {
            get
            {
                foreach (var kv in this) if (kv.Key == key) return kv.Value;
                return null;
            }
            set
            {
                for (int i = 0; i < Count; i++)
                {
                    if (base[i].Key == key) { base[i] = new KeyValuePair<string, object>(key, value); return; }
                }
                Add(new KeyValuePair<string, object>(key, value));
            }
        }

        public bool Has(string key)
        {
            foreach (var kv in this) if (kv.Key == key) return true;
            return false;
        }

        /// <summary>String value of a key, "" when missing or null.</summary>
        public string Str(string key)
        {
            object v = this[key];
            if (v == null) return "";
            if (v is bool) return (bool)v ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }

    public static class MiniJson
    {
        public static object Parse(string text)
        {
            int pos = 0;
            if (text.Length > 0 && text[0] == '﻿') pos = 1; // tolerate a BOM
            object value = ParseValue(text, ref pos);
            SkipWs(text, ref pos);
            if (pos != text.Length) throw new FormatException("Unexpected data after JSON value at " + pos);
            return value;
        }

        public static JsonObject ParseObject(string text)
        {
            var o = Parse(text) as JsonObject;
            if (o == null) throw new FormatException("JSON object expected");
            return o;
        }

        static void SkipWs(string s, ref int p)
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) p++;
        }

        static object ParseValue(string s, ref int p)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[p];
            if (c == '{') return ParseObj(s, ref p);
            if (c == '[') return ParseArr(s, ref p);
            if (c == '"') return ParseStr(s, ref p);
            if (Match(s, ref p, "true")) return true;
            if (Match(s, ref p, "false")) return false;
            if (Match(s, ref p, "null")) return null;
            int start = p;
            while (p < s.Length && "+-0123456789.eE".IndexOf(s[p]) >= 0) p++;
            if (p == start) throw new FormatException("Unexpected character '" + c + "' at " + p);
            return s.Substring(start, p - start);
        }

        static bool Match(string s, ref int p, string word)
        {
            if (string.CompareOrdinal(s, p, word, 0, word.Length) == 0) { p += word.Length; return true; }
            return false;
        }

        static JsonObject ParseObj(string s, ref int p)
        {
            var o = new JsonObject();
            p++; // {
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return o; }
            while (true)
            {
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != '"') throw new FormatException("Object key expected at " + p);
                string key = ParseStr(s, ref p);
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != ':') throw new FormatException("':' expected at " + p);
                p++;
                o[key] = ParseValue(s, ref p);
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == '}') { p++; return o; }
                throw new FormatException("',' or '}' expected at " + p);
            }
        }

        static List<object> ParseArr(string s, ref int p)
        {
            var a = new List<object>();
            p++; // [
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref p));
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == ']') { p++; return a; }
                throw new FormatException("',' or ']' expected at " + p);
            }
        }

        static string ParseStr(string s, ref int p)
        {
            var sb = new StringBuilder();
            p++; // opening quote
            while (p < s.Length)
            {
                char c = s[p++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= s.Length) break;
                char e = s[p++];
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
                        if (p + 4 > s.Length) throw new FormatException("Bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        p += 4;
                        break;
                    default: throw new FormatException("Bad escape \\" + e);
                }
            }
            throw new FormatException("Unterminated string");
        }

        // ------------------------------------------------------------------ writer
        public static string Serialize(object value, bool indent = false)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v, bool indent, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { WriteStr(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            var o = v as JsonObject;
            if (o != null)
            {
                if (o.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                for (int i = 0; i < o.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (indent) { sb.Append('\n').Append(' ', 2 * (depth + 1)); }
                    WriteStr(sb, o[i].Key);
                    sb.Append(indent ? ": " : ":");
                    Write(sb, o[i].Value, indent, depth + 1);
                }
                if (indent) sb.Append('\n').Append(' ', 2 * depth);
                sb.Append('}');
                return;
            }
            var list = v as System.Collections.IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item, indent, depth + 1);
                }
                sb.Append(']');
                return;
            }
            sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void WriteStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
