using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Net;

namespace PackageManager.Services
{
    public class PingCodeApiService
    {
        private readonly HttpClient _http;
        private readonly DataPersistenceService _data;
        private string _token;
        private DateTime _tokenExpiresAt;
        
        public PingCodeApiService()
        {
            _http = new HttpClient();
            _data = new DataPersistenceService();
        }
        
        public class Entity
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }
        
        public class StoryPointBreakdown
        {
            public double NotStarted { get; set; }
            public double InProgress { get; set; }
            public double Done { get; set; }
            public double Total { get; set; }
            
            public int HighestPriorityCount { get; set; }
            public double HighestPriorityPoints { get; set; }
            public int HigherPriorityCount { get; set; }
            public double HigherPriorityPoints { get; set; }
            public int OtherPriorityCount { get; set; }
            public double OtherPriorityPoints { get; set; }
        }
        
        private static double ReadDouble(JToken t)
        {
            if (t == null) return 0;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return t.Value<double>();
            double d;
            return double.TryParse(t.ToString(), out d) ? d : 0;
        }
        
        private static string ExtractString(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Object)
            {
                var name = t["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) return name;
                var value = t["value"]?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
                return t.ToString();
            }
            return t.ToString();
        }
        
        private static string ExtractId(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Object)
            {
                var id = t.Value<string>("id");
                if (!string.IsNullOrWhiteSpace(id)) return id;
                var value = t.Value<string>("value");
                if (!string.IsNullOrWhiteSpace(value)) return value;
                var name = t.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(name)) return name;
                return t.ToString();
            }
            if (t.Type == JTokenType.String) return t.Value<string>();
            return t.ToString();
        }
        
        private static string ExtractName(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Object)
            {
                var display = t.Value<string>("display_name");
                if (!string.IsNullOrWhiteSpace(display)) return display;
                var name = t.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(name)) return name;
                var value = t.Value<string>("value");
                if (!string.IsNullOrWhiteSpace(value)) return value;
                return t.ToString();
            }
            if (t.Type == JTokenType.String) return t.Value<string>();
            return t.ToString();
        }
        
        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return null;
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }
        
        private static string ReadStatus(JToken v)
        {
            var s = ExtractString(v["status"]);
            if (string.IsNullOrWhiteSpace(s)) s = ExtractString(v["state"]);
            if (string.IsNullOrWhiteSpace(s)) s = ExtractString(v["fields"]?["status"]);
            if (string.IsNullOrWhiteSpace(s)) s = ExtractString(v["fields"]?["state"]);
            return s;
        }
        
        private enum PriorityCategory
        {
            Highest,
            Higher,
            Other
        }
        
        private static string ReadPriorityText(JToken v)
        {
            var p = ExtractString(v?["priority"]);
            if (string.IsNullOrWhiteSpace(p)) p = ExtractString(v?["fields"]?["priority"]);
            if (string.IsNullOrWhiteSpace(p)) p = ExtractString(v?["severity"]);
            if (string.IsNullOrWhiteSpace(p)) p = ExtractString(v?["fields"]?["severity"]);
            return p;
        }
        
        private static PriorityCategory ClassifyPriority(string p)
        {
            var s = (p ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) return PriorityCategory.Other;
            // Highest first to avoid "高" being matched by "最高"
            if (s.Contains("最高") || s.Contains("极高") || s.Contains("p0") || s.Contains("critical") || s.Contains("blocker") || s.Contains("urgent") || s.Contains("very high") || s == "0" || s == "1")
            {
                return PriorityCategory.Highest;
            }
            if (s.Contains("较高") || s.Contains("高") || s.Contains("p1") || s.Contains("high") || s == "2")
            {
                return PriorityCategory.Higher;
            }
            return PriorityCategory.Other;
        }
        
        private static JArray GetValuesArray(JObject json)
        {
            if (json == null) return null;
            var v = json["values"];
            var arr = v as JArray;
            if (arr != null) return arr;
            if (v is JObject vo)
            {
                arr = vo["items"] as JArray ?? vo["work_items"] as JArray ?? vo["users"] as JArray ?? vo["projects"] as JArray ?? vo["iterations"] as JArray ?? vo["sprints"] as JArray ?? vo["members"] as JArray ?? vo["list"] as JArray;
                if (arr != null) return arr;
            }
            arr = json["items"] as JArray ?? json["work_items"] as JArray ?? json["users"] as JArray ?? json["projects"] as JArray ?? json["iterations"] as JArray ?? json["sprints"] as JArray ?? json["members"] as JArray ?? json["list"] as JArray ?? json["data"] as JArray ?? json["results"] as JArray;
            return arr;
        }
        
        private string GetClientId()
        {
            var settings = _data.LoadSettings();
            var env = Environment.GetEnvironmentVariable("PINGCODE_CLIENT_ID");
            if (!string.IsNullOrWhiteSpace(settings?.PingCodeClientId)) return settings.PingCodeClientId;
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return null;
        }
        
        private string GetClientSecret()
        {
            var settings = _data.LoadSettings();
            var env = Environment.GetEnvironmentVariable("PINGCODE_CLIENT_SECRET");
            if (!string.IsNullOrWhiteSpace(settings?.PingCodeClientSecret)) return settings.PingCodeClientSecret;
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return null;
        }
        
        private async Task EnsureTokenAsync()
        {
            if (!string.IsNullOrWhiteSpace(_token) && (_tokenExpiresAt > DateTime.UtcNow.AddMinutes(1)))
            {
                return;
            }
            
            var clientId = GetClientId();
            var clientSecret = GetClientSecret();
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException("未配置 PingCode ClientId 或 Secret");
            }
            
            var authGetUrl = $"https://open.pingcode.com/v1/auth/token?grant_type=client_credentials&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}";
            try
            {
                using var resp = await _http.GetAsync(authGetUrl);
                var txt = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    var jobj = JObject.Parse(txt);
                    var access = jobj.Value<string>("access_token");
                    var expires = jobj.Value<int?>("expires_in");
                    if (!string.IsNullOrWhiteSpace(access))
                    {
                        _token = access;
                        _tokenExpiresAt = DateTime.UtcNow.AddSeconds((expires ?? 3600));
                        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                        return;
                    }
                }
                else
                {
                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.BadRequest)
                    {
                        throw new ApiAuthException($"Token 请求失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
                    }
                }
            }
            catch (Exception)
            {
            }
        }
        
        private async Task<JObject> GetJsonAsync(string url)
        {
            await EnsureTokenAsync();
            using var resp = await _http.GetAsync(url);
            var txt = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new ApiAuthException($"GET 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
                }
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new ApiForbiddenException($"GET 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
                }
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new ApiNotFoundException($"GET 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
                }
                throw new InvalidOperationException($"GET 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }
            return JObject.Parse(txt);
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
        
        public async Task<List<Entity>> GetProjectsAsync()
        {
            var candidates = new[]
            {
                "https://open.pingcode.com/v1/project/projects?page_size=100",
                "https://open.pingcode.com/v1/agile/projects?page_size=100",
                "https://open.pingcode.com/v1/projects?page_size=100"
            };
            Exception last = null;
            foreach (var url in candidates)
            {
                try
                {
                    var json = await GetJsonAsync(url);
                    var entities = ParseEntities(json);
                    if (entities.Count > 0) return entities;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            if (last != null) throw last;
            return new List<Entity>();
        }
        
        public async Task<List<Entity>> GetOngoingIterationsByProjectAsync(string projectId)
        {
            var result = new List<Entity>();
            var baseUrl = $"https://open.pingcode.com/v1/project/projects/{Uri.EscapeDataString(projectId)}/sprints";
            var pageIndex = 0;
            var pageSize = 100;
            var seen = new HashSet<string>();
            while (true)
            {
                var url = $"{baseUrl}?status=in_progress&page_size={pageSize}&page_index={pageIndex}";
                var json = await GetJsonAsync(url);
                var values = GetValuesArray(json);
                if (values == null || values.Count == 0) break;
                foreach (var v in values)
                {
                    var id = v.Value<string>("id");
                    var nm = v.Value<string>("name");
                    if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    {
                        result.Add(new Entity { Id = id, Name = nm ?? id });
                    }
                }
                var total = json.Value<int?>("total") ?? 0;
                pageIndex++;
                if ((pageIndex * pageSize) >= total) break;
            }
            
            return result;
        }
        
        public async Task<List<Entity>> GetProjectMembersAsync(string projectId)
        {
            var result = new List<Entity>();
            var baseUrl = $"https://open.pingcode.com/v1/project/projects/{Uri.EscapeDataString(projectId)}/members";
            var pageIndex = 0;
            var pageSize = 100;
            var seen = new HashSet<string>();
            while (true)
            {
                var url = $"{baseUrl}?page_size={pageSize}&page_index={pageIndex}";
                var json = await GetJsonAsync(url);
                var values = GetValuesArray(json);
                if (values == null || values.Count == 0) break;
                foreach (var v in values)
                {
                    var user = v["user"];
                    var id = user?.Value<string>("id") ?? v.Value<string>("id");
                    var nm = user?.Value<string>("display_name") ?? v.Value<string>("display_name");
                    if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    {
                        result.Add(new Entity { Id = id, Name = nm ?? id });
                    }
                }
                var total = json.Value<int?>("total") ?? 0;
                pageIndex++;
                if ((pageIndex * pageSize) >= total) break;
            }
            return result;
        }
        
        public async Task<Dictionary<string, StoryPointBreakdown>> GetIterationStoryPointsBreakdownByAssigneeAsync(string iterationOrSprintId)
        {
            var result = new Dictionary<string, StoryPointBreakdown>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(iterationOrSprintId))
            {
                return result;
            }
            var baseUrlCandidates = new[]
            {
                "https://open.pingcode.com/v1/project/work_items",
                "https://open.pingcode.com/v1/agile/work_items"
            };
            foreach (var baseUrl in baseUrlCandidates)
            {
                try
                {
                    var pageIndex = 0;
                    var pageSize = 100;
                    while (true)
                    {
                        var url = $"{baseUrl}?sprint_id={Uri.EscapeDataString(iterationOrSprintId)}&page_size={pageSize}&page_index={pageIndex}";
                        var json = await GetJsonAsync(url);
                        var values = GetValuesArray(json);
                        if (values == null || values.Count == 0)
                        {
                            url = $"{baseUrl}?iteration_id={Uri.EscapeDataString(iterationOrSprintId)}&page_size={pageSize}&page_index={pageIndex}";
                            json = await GetJsonAsync(url);
                            values = GetValuesArray(json);
                            if (values == null || values.Count == 0) break;
                        }
                        foreach (var v in values)
                        {
                            var assignedId = FirstNonEmpty(
                                ExtractId(v["assigned_to"]),
                                ExtractId(v["assignee"]),
                                ExtractId(v["owner"]),
                                ExtractId(v["processor"]),
                                ExtractId(v["fields"]?["assigned_to"]),
                                ExtractId(v["fields"]?["assignee"]),
                                ExtractId(v["fields"]?["owner"]),
                                ExtractId(v["fields"]?["processor"])
                            );
                            var assignedName = FirstNonEmpty(
                                ExtractString(v["assigned_to_name"]),
                                ExtractString(v["assignee_name"]),
                                ExtractString(v["owner_name"]),
                                ExtractString(v["processor_name"]),
                                ExtractString(v["fields"]?["assigned_to_name"]),
                                ExtractString(v["fields"]?["assignee_name"]),
                                ExtractString(v["fields"]?["owner_name"]),
                                ExtractString(v["fields"]?["processor_name"]),
                                ExtractName(v["assigned_to"]),
                                ExtractName(v["assignee"]),
                                ExtractName(v["owner"]),
                                ExtractName(v["processor"]),
                                ExtractName(v["fields"]?["assigned_to"]),
                                ExtractName(v["fields"]?["assignee"]),
                                ExtractName(v["fields"]?["owner"]),
                                ExtractName(v["fields"]?["processor"])
                            );
                            var keyId = (assignedId ?? "").Trim().ToLowerInvariant();
                            var keyName = (assignedName ?? "").Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(keyId) && string.IsNullOrEmpty(keyName)) continue;
                            StoryPointBreakdown bd = null;
                            if (!string.IsNullOrEmpty(keyId) && result.TryGetValue(keyId, out var existById))
                            {
                                bd = existById;
                            }
                            else if (!string.IsNullOrEmpty(keyName) && result.TryGetValue(keyName, out var existByName))
                            {
                                bd = existByName;
                            }
                            else
                            {
                                bd = new StoryPointBreakdown();
                                if (!string.IsNullOrEmpty(keyId)) result[keyId] = bd;
                                if (!string.IsNullOrEmpty(keyName)) result[keyName] = bd;
                            }
                            double sp = ReadDouble(v["story_points"]);
                            if (sp == 0) sp = ReadDouble(v["story_point"]);
                            if (sp == 0) sp = ReadDouble(v["fields"]?["story_points"]);
                            var status = ReadStatus(v);
                            var s = (status ?? "").Trim().ToLowerInvariant();
                            if (s.Contains("done") || s.Contains("完成") || s.Contains("resolved") || s.Contains("closed") || s.Contains("已完成"))
                            {
                                bd.Done += sp;
                            }
                            else if (s.Contains("progress") || s.Contains("进行中") || s.Contains("doing") || s.Contains("开发中") || s.Contains("处理中") ||
                                     s.Contains("in_progress") || s.Contains("可测试") || s.Contains("测试中") || s.Contains("已修复"))
                            {
                                bd.InProgress += sp;
                            }
                            else
                            {
                                bd.NotStarted += sp;
                            }
                            bd.Total += sp;
                            
                            var prioText = ReadPriorityText(v);
                            var cat = ClassifyPriority(prioText);
                            if (cat == PriorityCategory.Highest)
                            {
                                bd.HighestPriorityCount += 1;
                                bd.HighestPriorityPoints += sp;
                            }
                            else if (cat == PriorityCategory.Higher)
                            {
                                bd.HigherPriorityCount += 1;
                                bd.HigherPriorityPoints += sp;
                            }
                            else
                            {
                                bd.OtherPriorityCount += 1;
                                bd.OtherPriorityPoints += sp;
                            }
                        }
                        var totalCount = json.Value<int?>("total") ?? 0;
                        pageIndex++;
                        if ((pageIndex * pageSize) >= totalCount) break;
                    }
                    return result;
                }
                catch (Exception ex)
                {
                }
            }
            return result;
        }
        
        public async Task<double> GetUserStoryPointsSumAsync(string iterationOrSprintId, string userId)
        {
            if (string.IsNullOrWhiteSpace(iterationOrSprintId) || string.IsNullOrWhiteSpace(userId))
            {
                return 0;
            }
            var baseUrlCandidates = new[]
            {
                "https://open.pingcode.com/v1/project/work_items",
                "https://open.pingcode.com/v1/agile/work_items"
            };
            foreach (var baseUrl in baseUrlCandidates)
            {
                try
                {
                    var total = 0.0;
                    var pageIndex = 0;
                    var pageSize = 100;
                    while (true)
                    {
                        var url = $"{baseUrl}?sprint_id={Uri.EscapeDataString(iterationOrSprintId)}&assigned_to={Uri.EscapeDataString(userId)}&page_size={pageSize}&page_index={pageIndex}";
                        var json = await GetJsonAsync(url);
                        var values = GetValuesArray(json);
                        if (values == null || values.Count == 0)
                        {
                            url = $"{baseUrl}?iteration_id={Uri.EscapeDataString(iterationOrSprintId)}&assigned_to={Uri.EscapeDataString(userId)}&page_size={pageSize}&page_index={pageIndex}";
                            json = await GetJsonAsync(url);
                            values = GetValuesArray(json);
                            if (values == null || values.Count == 0) break;
                        }
                        foreach (var v in values)
                        {
                            double sp = ReadDouble(v["story_points"]);
                            if (sp == 0) sp = ReadDouble(v["story_point"]);
                            if (sp == 0) sp = ReadDouble(v["fields"]?["story_points"]);
                            total += sp;
                        }
                        var totalCount = json.Value<int?>("total") ?? 0;
                        pageIndex++;
                        if ((pageIndex * pageSize) >= totalCount) break;
                    }
                    return total;
                }
                catch
                {
                }
            }
            return 0;
        }
    }
    
    public class ApiAuthException : Exception
    {
        public ApiAuthException(string message) : base(message) { }
    }
    public class ApiForbiddenException : Exception
    {
        public ApiForbiddenException(string message) : base(message) { }
    }
    public class ApiNotFoundException : Exception
    {
        public ApiNotFoundException(string message) : base(message) { }
    }
}
