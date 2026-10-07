using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace IAFoot.Learning
{
    /// <summary>
    /// Mini lecteur / écrivain JSON sans dépendance. Les descriptions de modèles envoyées par Python ont une
    /// structure libre (elle dépend du type de modèle), ce que JsonUtility ne sait pas représenter.
    /// Un objet JSON devient un Dictionary&lt;string, object&gt;, un tableau une List&lt;object&gt;,
    /// un nombre un double, et les autres valeurs string / bool / null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var parser = new Parser(text);
            object value = parser.ReadValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
                throw parser.Error("caractères inattendus après la fin du document");
            return value;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case float f:
                    sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case sbyte or byte or short or ushort or int or uint or long:
                    sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, object> pair in dict)
                    {
                        if (!first)
                            sb.Append(',');
                        first = false;
                        WriteString(sb, pair.Key);
                        sb.Append(':');
                        Write(sb, pair.Value);
                    }
                    sb.Append('}');
                    break;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (!first)
                            sb.Append(',');
                        first = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    break;
                }
                default:
                    WriteString(sb, value.ToString());
                    break;
            }
        }

        static void WriteString(StringBuilder sb, string s)
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
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Parser
        {
            readonly string text;
            int pos;

            public Parser(string text) => this.text = text ?? string.Empty;

            public bool AtEnd => pos >= text.Length;

            public FormatException Error(string message) => new FormatException($"JSON invalide (position {pos}) : {message}");

            public void SkipWhitespace()
            {
                while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                    pos++;
            }

            public object ReadValue()
            {
                SkipWhitespace();
                if (AtEnd)
                    throw Error("fin inattendue");

                char c = text[pos];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ReadNumber();
                }
            }

            Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>();
                pos++; // {
                SkipWhitespace();
                if (Peek() == '}')
                {
                    pos++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                        throw Error("nom de champ attendu");
                    string key = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':')
                        throw Error("':' attendu");
                    pos++;
                    result[key] = ReadValue();
                    SkipWhitespace();
                    char c = Peek();
                    pos++;
                    if (c == '}')
                        return result;
                    if (c != ',')
                        throw Error("',' ou '}' attendu");
                }
            }

            List<object> ReadArray()
            {
                var result = new List<object>();
                pos++; // [
                SkipWhitespace();
                if (Peek() == ']')
                {
                    pos++;
                    return result;
                }

                while (true)
                {
                    result.Add(ReadValue());
                    SkipWhitespace();
                    char c = Peek();
                    pos++;
                    if (c == ']')
                        return result;
                    if (c != ',')
                        throw Error("',' ou ']' attendu");
                }
            }

            string ReadString()
            {
                var sb = new StringBuilder();
                pos++; // "
                while (true)
                {
                    if (AtEnd)
                        throw Error("chaîne non terminée");
                    char c = text[pos++];
                    if (c == '"')
                        return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (AtEnd)
                        throw Error("échappement non terminé");
                    char escaped = text[pos++];
                    switch (escaped)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (pos + 4 > text.Length)
                                throw Error("échappement \\u incomplet");
                            sb.Append((char)int.Parse(text.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            pos += 4;
                            break;
                        default: sb.Append(escaped); break; // \" \\ \/
                    }
                }
            }

            object ReadNumber()
            {
                int start = pos;
                while (pos < text.Length && "+-0123456789.eE".IndexOf(text[pos]) >= 0)
                    pos++;
                if (pos == start)
                    throw Error($"valeur inattendue '{text[pos]}'");
                return double.Parse(text.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            char Peek() => AtEnd ? '\0' : text[pos];

            void Expect(string word)
            {
                if (string.CompareOrdinal(text, pos, word, 0, word.Length) != 0)
                    throw Error($"'{word}' attendu");
                pos += word.Length;
            }
        }
    }

    /// <summary>Accès typés aux valeurs produites par <see cref="Json.Parse"/>.</summary>
    public static class JsonExt
    {
        public static Dictionary<string, object> AsObject(this object value, string what) =>
            value as Dictionary<string, object> ?? throw new FormatException($"{what} : objet JSON attendu");

        public static List<object> AsList(this object value, string what) =>
            value as List<object> ?? throw new FormatException($"{what} : tableau JSON attendu");

        public static bool Has(this Dictionary<string, object> obj, string key) =>
            obj.TryGetValue(key, out object value) && value != null;

        public static object Require(this Dictionary<string, object> obj, string key, string what) =>
            obj.Has(key) ? obj[key] : throw new FormatException($"{what} : champ '{key}' manquant");

        public static string GetString(this Dictionary<string, object> obj, string key, string fallback = null) =>
            obj.TryGetValue(key, out object value) && value is string s ? s : fallback;

        public static double GetDouble(this Dictionary<string, object> obj, string key, double fallback = 0d)
        {
            if (!obj.TryGetValue(key, out object value))
                return fallback;
            switch (value)
            {
                case double d: return d;
                case float f: return f;
                case int i: return i;
                case long l: return l;
                default: return fallback;
            }
        }

        public static float GetFloat(this Dictionary<string, object> obj, string key, float fallback = 0f) =>
            (float)obj.GetDouble(key, fallback);

        public static int GetInt(this Dictionary<string, object> obj, string key, int fallback = 0) =>
            (int)Math.Round(obj.GetDouble(key, fallback));

        public static bool GetBool(this Dictionary<string, object> obj, string key, bool fallback = false) =>
            obj.TryGetValue(key, out object value) && value is bool b ? b : fallback;
    }
}
