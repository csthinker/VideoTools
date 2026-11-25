using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace VideoTools.Automation
{
    /// <summary>
    /// 轻量 JSON 工具，基于 .NET Framework 自带的 JavaScriptSerializer，避免引入额外依赖。
    /// </summary>
    public static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 512
        };

        public static string Serialize(object value)
        {
            return Serializer.Serialize(value);
        }

        public static object Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return Serializer.DeserializeObject(text);
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            var obj = Parse(text);
            var dict = obj as Dictionary<string, object>;
            if (dict == null)
            {
                throw new ToolException("INVALID_JSON", "JSON 内容不是一个对象。");
            }
            return dict;
        }

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> AsArray(object value)
        {
            var list = value as List<object>;
            if (list != null) return list;
            var array = value as object[];
            if (array != null) return new List<object>(array);
            var arrayList = value as ArrayList;
            if (arrayList != null) return new List<object>(arrayList.ToArray());
            return null;
        }

        public static string Str(IDictionary<string, object> data, string key, string fallback = null)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return fallback;
            if (value is string) return (string)value;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static int? Int(IDictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return null;
            if (value is int) return (int)value;
            if (value is long) return (int)(long)value;
            if (value is decimal) return (int)(decimal)value;
            if (value is double) return (int)(double)value;
            double parsed;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return (int)Math.Round(parsed);
            }
            return null;
        }

        public static double? Dbl(IDictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return null;
            if (value is double) return (double)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is decimal) return (double)(decimal)value;
            double parsed;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return null;
        }

        public static bool? Bool(IDictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return null;
            if (value is bool) return (bool)value;
            if (value is int) return (int)value != 0;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text)) return null;
            text = text.Trim().ToLowerInvariant();
            if (text == "true" || text == "1" || text == "yes" || text == "on" || text == "enable" || text == "enbale") return true;
            if (text == "false" || text == "0" || text == "no" || text == "off" || text == "disable") return false;
            return null;
        }

        public static List<string> StrList(IDictionary<string, object> data, string key)
        {
            var result = new List<string>();
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return result;
            var array = value as IEnumerable;
            if (array != null && !(value is string))
            {
                foreach (var item in array)
                {
                    if (item != null) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                }
                return result;
            }
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var part in text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) result.Add(trimmed);
            }
            return result;
        }

        public static Dictionary<string, object> Merge(IDictionary<string, object> baseData, IDictionary<string, object> overrides)
        {
            var merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (baseData != null)
            {
                foreach (var pair in baseData) merged[pair.Key] = pair.Value;
            }
            if (overrides != null)
            {
                foreach (var pair in overrides)
                {
                    if (pair.Value == null) continue;
                    merged[pair.Key] = pair.Value;
                }
            }
            return merged;
        }
    }
}
