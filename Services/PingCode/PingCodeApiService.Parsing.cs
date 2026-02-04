using PackageManager.Services.PingCode.Model;

namespace PackageManager.Services.PingCode;

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

public partial class PingCodeApiService
{
    private static double ReadDouble(JToken t)
    {
        if (t == null)
        {
            return 0;
        }

        if ((t.Type == JTokenType.Float) || (t.Type == JTokenType.Integer))
        {
            return t.Value<double>();
        }

        double d;
        return double.TryParse(t.ToString(), out d) ? d : 0;
    }

    private static int ReadInt(object o)
    {
        if (o == null)
        {
            return 0;
        }

        if (o is int i)
        {
            return i;
        }

        if (o is long l)
        {
            return (int)l;
        }

        if (o is double d)
        {
            return (int)d;
        }

        int r;
        return int.TryParse(o.ToString(), out r) ? r : 0;
    }

    private static string ExtractString(JToken t)
    {
        if (t == null)
        {
            return null;
        }

        if (t.Type == JTokenType.Object)
        {
            var name = t["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            var value = t["value"]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return t.ToString();
        }

        return t.ToString();
    }

    private static string DictGet(Dictionary<string, string> dict, string key)
    {
        if ((dict == null) || string.IsNullOrEmpty(key))
        {
            return null;
        }

        string v;
        return dict.TryGetValue(key, out v) ? v : null;
    }

    private static string ExtractId(JToken t)
    {
        if (t == null)
        {
            return null;
        }

        if (t.Type == JTokenType.Object)
        {
            var id = t.Value<string>("id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }

            var value = t.Value<string>("value");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var name = t.Value<string>("name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            return t.ToString();
        }

        if (t.Type == JTokenType.String)
        {
            return t.Value<string>();
        }

        return t.ToString();
    }

    private static string ExtractName(JToken t)
    {
        if (t == null)
        {
            return null;
        }

        if (t.Type == JTokenType.Object)
        {
            var display = t.Value<string>("display_name");
            if (!string.IsNullOrWhiteSpace(display))
            {
                return display;
            }

            var name = t.Value<string>("name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            var value = t.Value<string>("value");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return t.ToString();
        }

        if (t.Type == JTokenType.String)
        {
            return t.Value<string>();
        }

        return t.ToString();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
        {
            return null;
        }

        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }

        return null;
    }

    private static string ReadStatus(JToken v)
    {
        var s = ExtractString(v["status"]);
        if (string.IsNullOrWhiteSpace(s))
        {
            s = ExtractString(v["state"]);
        }

        if (string.IsNullOrWhiteSpace(s))
        {
            s = ExtractString(v["fields"]?["status"]);
        }

        if (string.IsNullOrWhiteSpace(s))
        {
            s = ExtractString(v["fields"]?["state"]);
        }

        return s;
    }

    private static string ReadHtmlUrl(JToken v)
    {
        return FirstNonEmpty(ExtractString(v?["html_url"]),
                             ExtractString(v?["web_url"]),
                             ExtractString(v?["url"]),
                             ExtractString(v?["fields"]?["html_url"]),
                             ExtractString(v?["links"]?["html_url"]));
    }

    private static string ReadPriorityText(JToken v)
    {
        var p = ExtractString(v?["priority"]);
        if (string.IsNullOrWhiteSpace(p))
        {
            p = ExtractString(v?["fields"]?["priority"]);
        }

        if (string.IsNullOrWhiteSpace(p))
        {
            p = ExtractString(v?["severity"]);
        }

        if (string.IsNullOrWhiteSpace(p))
        {
            p = ExtractString(v?["fields"]?["severity"]);
        }

        return p;
    }

    private static DateTime? ReadDateTimeFromSeconds(JToken t)
    {
        if ((t == null) || (t.Type == JTokenType.Null))
        {
            return null;
        }

        long secs;
        if ((t.Type == JTokenType.Integer) || (t.Type == JTokenType.Float))
        {
            secs = t.Value<long>();
        }
        else
        {
            if (!long.TryParse(t.ToString(), out secs))
            {
                return null;
            }
        }

        try
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
            return dt;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? FromUnixSeconds(long? secs)
    {
        if (!secs.HasValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(secs.Value).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

    private static PriorityCategory ClassifyPriority(string p)
    {
        var s = (p ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s))
        {
            return PriorityCategory.Other;
        }

        if (s.Contains("最高") || s.Contains("极高") || s.Contains("p0") || s.Contains("critical") || s.Contains("blocker") || s.Contains("urgent") ||
            s.Contains("very high") || (s == "0") || (s == "1"))
        {
            return PriorityCategory.Highest;
        }

        if (s.Contains("较高") || s.Contains("高") || s.Contains("p1") || s.Contains("high") || (s == "2"))
        {
            return PriorityCategory.Higher;
        }

        return PriorityCategory.Other;
    }

    private static string CategorizeState(string status)
    {
        var s = (status ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s))
        {
            return "未开始";
        }

        if (s.Contains("关闭") || s.Contains("closed") || s.Contains("已拒绝"))
        {
            return "已关闭";
        }

        if (s.Contains("done") || s.Contains("完成") || s.Contains("resolved") || s.Contains("已完成") || s.Contains("已发布"))
        {
            return "已完成";
        }

        if (s.Contains("可测试") || s.Contains("已修复"))
        {
            return "可测试";
        }

        if (s.Contains("测试中") || s.Contains("测试"))
        {
            return "测试中";
        }

        if (s.Contains("重新打开") || s.Contains("progress") || s.Contains("进行中") || s.Contains("doing") || s.Contains("开发中") || s.Contains("处理中") ||
            s.Contains("挂起")
            || s.Contains("待完善") || s.Contains("in_progress"))
        {
            return "进行中";
        }

        if (s.Contains("新提交") || s.Contains("打开") || s.Contains("未开始") || s.Contains("新建") || s.Contains("待处理") || s.Contains("todo"))
        {
            return "未开始";
        }

        return "未开始";
    }

    private static string MapCategoryToStateType(string category)
    {
        var c = (category ?? "").Trim();
        if (string.Equals(c, "进行中", StringComparison.OrdinalIgnoreCase))
        {
            return "in_progress";
        }

        if (string.Equals(c, "可测试", StringComparison.OrdinalIgnoreCase))
        {
            return "testable";
        }

        if (string.Equals(c, "测试中", StringComparison.OrdinalIgnoreCase))
        {
            return "testing";
        }

        if (string.Equals(c, "已完成", StringComparison.OrdinalIgnoreCase))
        {
            return "done";
        }

        if (string.Equals(c, "已关闭", StringComparison.OrdinalIgnoreCase))
        {
            return "closed";
        }

        return "todo";
    }

    private static JArray GetValuesArray(JObject json)
    {
        if (json == null)
        {
            return null;
        }

        var v = json["values"];
        var arr = v as JArray;
        if (arr != null)
        {
            return arr;
        }

        if (v is JObject vo)
        {
            arr = vo["items"] as JArray ?? vo["work_items"] as JArray ?? vo["users"] as JArray ?? vo["projects"] as JArray ??
                  vo["iterations"] as JArray ?? vo["sprints"] as JArray ?? vo["members"] as JArray ?? vo["list"] as JArray;
            if (arr != null)
            {
                return arr;
            }
        }

        arr = json["items"] as JArray ?? json["work_items"] as JArray ?? json["users"] as JArray ?? json["projects"] as JArray ??
              json["iterations"] as JArray ?? json["sprints"] as JArray ??
              json["members"] as JArray ?? json["list"] as JArray ?? json["data"] as JArray ?? json["results"] as JArray;
        return arr;
    }

    private static List<Entity> ParseEntities(JObject jobj)
    {
        var result = new List<Entity>();
        var values = GetValuesArray(jobj);
        if (values == null)
        {
            return result;
        }

        foreach (var v in values)
        {
            var id = v.Value<string>("id") ?? v["user"]?.Value<string>("id") ?? v["iteration"]?.Value<string>("id");
            var name = v.Value<string>("name") ?? v["user"]?.Value<string>("name") ?? v["iteration"]?.Value<string>("name");
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Add(new Entity { Id = id, Name = name ?? id });
            }
        }

        return result;
    }
}
