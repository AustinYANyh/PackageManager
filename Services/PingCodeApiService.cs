using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
        
        public class WorkItemInfo
        {
            public string Id { get; set; }
            public string StateId { get; set; }
            public string ProjectId { get; set; }
            public string Identifier { get; set; }
            public string Title { get; set; }
            public string Status { get; set; }
            public string StateCategory { get; set; }
            public string AssigneeId { get; set; }
            public string AssigneeName { get; set; }
            public string AssigneeAvatar { get; set; }
            public double StoryPoints { get; set; }
            public string Priority { get; set; }
            public string Severity { get; set; }
            public string Type { get; set; }
            public string HtmlUrl { get; set; }
            public DateTime? StartAt { get; set; }
            public DateTime? EndAt { get; set; }
            public int CommentCount { get; set; }
            public List<string> Tags { get; set; } = new List<string>();
        }
        
        public class WorkItemDetails
        {
            public string Id { get; set; }
            public string Identifier { get; set; }
            public string Title { get; set; }
            public string HtmlUrl { get; set; }
            public string Type { get; set; }
            public string AssigneeId { get; set; }
            public string AssigneeName { get; set; }
            public string StateName { get; set; }
            public string StateType { get; set; }
            public string PriorityName { get; set; }
            public string SeverityName { get; set; }
            public double StoryPoints { get; set; }
            public string VersionName { get; set; }
            public DateTime? StartAt { get; set; }
            public DateTime? EndAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public string ProductName { get; set; }
            public string ReproduceVersion { get; set; }
            public string ReproduceProbability { get; set; }
            public string DefectCategory { get; set; }
            public List<string> Tags { get; set; } = new List<string>();
            public string SketchHtml { get; set; }
            public string DescriptionHtml { get; set; }
            public string ExpectedResult { get; set; }
            public List<WorkItemComment> Comments { get; set; } = new List<WorkItemComment>();
            public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string PublicImageToken { get; set; }
        }
        
        public class WorkItemComment
        {
            public string AuthorName { get; set; }
            public string AuthorAvatar { get; set; }
            public string ContentHtml { get; set; }
            public DateTime? CreatedAt { get; set; }
        }
        public class WorkItemDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("project")] public ProjectDto Project { get; set; }
            [JsonProperty("identifier")] public string Identifier { get; set; }
            [JsonProperty("title")] public string Title { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("start_at")] public long? StartAt { get; set; }
            [JsonProperty("end_at")] public long? EndAt { get; set; }
            [JsonProperty("parent_id")] public string ParentId { get; set; }
            [JsonProperty("short_id")] public string ShortId { get; set; }
            [JsonProperty("html_url")] public string HtmlUrl { get; set; }
            [JsonProperty("parent")] public WorkItemDto Parent { get; set; }
            [JsonProperty("assignee")] public UserDto Assignee { get; set; }
            [JsonProperty("state")] public StateDto State { get; set; }
            [JsonProperty("priority")] public PriorityDto Priority { get; set; }
            [JsonProperty("version")] public VersionDto Version { get; set; }
            [JsonProperty("sprint")] public SprintDto Sprint { get; set; }
            [JsonProperty("phase")] public string Phase { get; set; }
            [JsonProperty("story_points")] public double? StoryPoints { get; set; }
            [JsonProperty("estimated_workload")] public double? EstimatedWorkload { get; set; }
            [JsonProperty("remaining_workload")] public double? RemainingWorkload { get; set; }
            [JsonProperty("description")] public string Description { get; set; }
            [JsonProperty("completed_at")] public long? CompletedAt { get; set; }
            [JsonProperty("properties")] public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            [JsonProperty("tags")] public List<TagDto> Tags { get; set; } = new List<TagDto>();
            [JsonProperty("participants")] public List<ParticipantDto> Participants { get; set; } = new List<ParticipantDto>();
            [JsonProperty("created_at")] public long? CreatedAt { get; set; }
            [JsonProperty("created_by")] public UserDto CreatedBy { get; set; }
            [JsonProperty("updated_at")] public long? UpdatedAt { get; set; }
            [JsonProperty("updated_by")] public UserDto UpdatedBy { get; set; }
            [JsonProperty("is_archived")] public int? IsArchived { get; set; }
            [JsonProperty("is_deleted")] public int? IsDeleted { get; set; }
        }
        public class ProjectDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("identifier")] public string Identifier { get; set; }
            [JsonProperty("is_archived")] public int? IsArchived { get; set; }
            [JsonProperty("is_deleted")] public int? IsDeleted { get; set; }
        }
        public class StateDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("color")] public string Color { get; set; }
        }
        public class PriorityDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
        }
        public class VersionDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("start_at")] public long? StartAt { get; set; }
            [JsonProperty("end_at")] public long? EndAt { get; set; }
            [JsonProperty("stage")] public StageDto Stage { get; set; }
        }
        public class StageDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("color")] public string Color { get; set; }
        }
        public class SprintDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("start_at")] public long? StartAt { get; set; }
            [JsonProperty("end_at")] public long? EndAt { get; set; }
            [JsonProperty("status")] public string Status { get; set; }
        }
        public class TagDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
        }
        public class ParticipantDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("user")] public UserDto User { get; set; }
        }
        public class UserDto
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("url")] public string Url { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("display_name")] public string DisplayName { get; set; }
            [JsonProperty("avatar")] public string Avatar { get; set; }
        }
        public class StatePlanInfo
        {
            public string Id { get; set; }
            public string WorkItemType { get; set; }
            public string ProjectType { get; set; }
        }
        
        private static double ReadDouble(JToken t)
        {
            if (t == null) return 0;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return t.Value<double>();
            double d;
            return double.TryParse(t.ToString(), out d) ? d : 0;
        }
        
        private static int ReadInt(object o)
        {
            if (o == null) return 0;
            if (o is int i) return i;
            if (o is long l) return (int)l;
            if (o is double d) return (int)d;
            int r;
            return int.TryParse(o.ToString(), out r) ? r : 0;
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
        
        private static string DictGet(Dictionary<string, string> dict, string key)
        {
            if (dict == null || string.IsNullOrEmpty(key)) return null;
            string v;
            return dict.TryGetValue(key, out v) ? v : null;
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
        
        private static string ReadHtmlUrl(JToken v)
        {
            return FirstNonEmpty(
                ExtractString(v?["html_url"]),
                ExtractString(v?["web_url"]),
                ExtractString(v?["url"]),
                ExtractString(v?["fields"]?["html_url"]),
                ExtractString(v?["links"]?["html_url"])
            );
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
        
        private static DateTime? ReadDateTimeFromSeconds(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            long secs;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            {
                secs = t.Value<long>();
            }
            else
            {
                if (!long.TryParse(t.ToString(), out secs)) return null;
            }
            try
            {
                // PingCode 使用秒为单位的时间戳（UTC）
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
            if (!secs.HasValue) return null;
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
        
        private static string CategorizeState(string status)
        {
            var s = (status ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) return "未开始";
            if (s.Contains("关闭") || s.Contains("closed") || s.Contains("已拒绝"))
            {
                return "已关闭";
            }
            if (s.Contains("done") || s.Contains("完成") || s.Contains("resolved") || s.Contains("已完成"))
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
            if (s.Contains("progress") || s.Contains("进行中") || s.Contains("doing") || s.Contains("开发中") || s.Contains("处理中") ||
                s.Contains("in_progress"))
            {
                return "进行中";
            }
            if (s.Contains("未开始") || s.Contains("新建") || s.Contains("待处理") || s.Contains("todo"))
            {
                return "未开始";
            }
            return "未开始";
        }
        
        private static string MapCategoryToStateType(string category)
        {
            var c = (category ?? "").Trim();
            if (string.Equals(c, "进行中", StringComparison.OrdinalIgnoreCase)) return "in_progress";
            if (string.Equals(c, "可测试", StringComparison.OrdinalIgnoreCase)) return "testable";
            if (string.Equals(c, "测试中", StringComparison.OrdinalIgnoreCase)) return "testing";
            if (string.Equals(c, "已完成", StringComparison.OrdinalIgnoreCase)) return "done";
            if (string.Equals(c, "已关闭", StringComparison.OrdinalIgnoreCase)) return "closed";
            return "todo";
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
        
        public async Task<string> GetAccessTokenAsync()
        {
            await EnsureTokenAsync();
            return _token;
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
        
        private async Task<JObject> PatchJsonAsync(string url, JObject body)
        {
            await EnsureTokenAsync();
            var req = new HttpRequestMessage(new HttpMethod("PATCH"), url);
            var payload = body ?? new JObject();
            req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var txt = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new ApiAuthException($"PATCH 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
                }
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new ApiForbiddenException($"PATCH 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
                }
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new ApiNotFoundException($"PATCH 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
                }
                throw new InvalidOperationException($"PATCH 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }
            try
            {
                return string.IsNullOrWhiteSpace(txt) ? new JObject() : JObject.Parse(txt);
            }
            catch
            {
                return new JObject();
            }
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
        
        public async Task<List<WorkItemInfo>> GetIterationWorkItemsAsync(string iterationOrSprintId)
        {
            var result = new List<WorkItemInfo>();
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
                        var dtos = values.ToObject<List<WorkItemDto>>() ?? new List<WorkItemDto>();
                        foreach (var d in dtos)
                        {
                            var status = d.State?.Name;
                            var stateId = d.State?.Id;
                            var assigneeId = d.Assignee?.Id;
                            var assigneeName = !string.IsNullOrWhiteSpace(d.Assignee?.DisplayName) ? d.Assignee.DisplayName : d.Assignee?.Name;
                            var assigneeAvatar = d.Assignee?.Avatar;
                            var prio = d.Priority?.Name;
                            var sp = d.StoryPoints ?? 0;
                            var severity = "";
                            object sv;
                            if (d.Properties != null)
                            {
                                if (d.Properties.TryGetValue("severity", out sv) && sv != null) severity = sv.ToString();
                                else if (d.Properties.TryGetValue("严重程度", out sv) && sv != null) severity = sv.ToString();
                                else if (d.Properties.TryGetValue("严重", out sv) && sv != null) severity = sv.ToString();
                            }
                            var endAt = FromUnixSeconds(d.EndAt);
                            var startAt = FromUnixSeconds(d.StartAt);
                            var commentCount = 0;
                            object cc;
                            if (d.Properties != null)
                            {
                                if (d.Properties.TryGetValue("comment_count", out cc) && cc != null) commentCount = ReadInt(cc);
                                else if (d.Properties.TryGetValue("comments_count", out cc) && cc != null) commentCount = ReadInt(cc);
                                else if (d.Properties.TryGetValue("评论数", out cc) && cc != null) commentCount = ReadInt(cc);
                            }
                            var type = d.Type;
                            var htmlUrl = d.HtmlUrl;
                            var tagNames = (d.Tags ?? new List<TagDto>()).Select(t => t?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                            var wi = new WorkItemInfo
                            {
                                Id = d.Id ?? d.ShortId,
                                StateId = stateId,
                                ProjectId = d.Project?.Id,
                                Identifier = d.Identifier ?? d.ShortId ?? d.Id,
                                Title = d.Title ?? d.Identifier ?? d.Id,
                                Status = status,
                                StateCategory = CategorizeState(status),
                                AssigneeId = assigneeId,
                                AssigneeName = assigneeName,
                                AssigneeAvatar = assigneeAvatar,
                                StoryPoints = sp,
                                Priority = prio,
                                Severity = severity,
                                Type = type,
                                HtmlUrl = htmlUrl,
                                StartAt = startAt,
                                EndAt = endAt,
                                CommentCount = commentCount,
                                Tags = tagNames
                            };
                            result.Add(wi);
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
        
        public async Task<WorkItemDetails> GetWorkItemDetailsAsync(string workItemId)
        {
            if (string.IsNullOrWhiteSpace(workItemId)) return null;
            var candidates = new[]
            {
                $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId)}?include_public_image_token=description,shiyitu",
                $"https://open.pingcode.com/v1/agile/work_items/{Uri.EscapeDataString(workItemId)}?include_public_image_token=description,shiyitu",
                $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId)}",
                $"https://open.pingcode.com/v1/agile/work_items/{Uri.EscapeDataString(workItemId)}",
            };
            foreach (var url in candidates)
            {
                try
                {
                    var json = await GetJsonAsync(url);
                    if (json == null) continue;
                    var dto = json.ToObject<WorkItemDto>();
                    if (dto == null) continue;
                    var d = new WorkItemDetails();
                    d.Id = dto.Id ?? workItemId;
                    d.Identifier = dto.Identifier;
                    d.Title = dto.Title ?? dto.Identifier ?? dto.Id;
                    d.HtmlUrl = dto.HtmlUrl;
                    d.Type = dto.Type;
                    d.AssigneeId = dto.Assignee?.Id;
                    d.AssigneeName = !string.IsNullOrWhiteSpace(dto.Assignee?.DisplayName) ? dto.Assignee.DisplayName : dto.Assignee?.Name;
                    d.StateName = dto.State?.Name;
                    d.StateType = dto.State?.Type;
                    d.PriorityName = dto.Priority?.Name;
                    d.SeverityName = null;
                    d.StoryPoints = dto.StoryPoints ?? 0;
                    if (d.StoryPoints == 0 && dto.Properties != null && dto.Properties.TryGetValue("gushidianhuizong", out var g))
                    {
                        if (g is double gd) d.StoryPoints = gd;
                        else if (g != null)
                        {
                            double gg;
                            if (double.TryParse(g.ToString(), out gg)) d.StoryPoints = gg;
                        }
                    }
                    d.VersionName = dto.Version?.Name;
                    d.ProductName = DictGet(d.Properties, "suoshuchanpin");;
                    d.StartAt = ReadDateTimeFromSeconds(dto.StartAt);
                    d.EndAt = ReadDateTimeFromSeconds(dto.EndAt);
                    d.CompletedAt = ReadDateTimeFromSeconds(dto.CompletedAt);
                    d.Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in dto.Properties ?? new Dictionary<string, object>())
                    {
                        d.Properties[kv.Key] = kv.Value?.ToString();
                    }
                    d.SeverityName = FirstNonEmpty(
                        DictGet(d.Properties, "severity"),
                        DictGet(d.Properties, "严重程度"),
                        DictGet(d.Properties, "严重")
                    );
                    d.ReproduceVersion = DictGet(d.Properties, "复现版本号");
                    d.ReproduceProbability = DictGet(d.Properties, "复现概率");
                    d.DefectCategory = DictGet(d.Properties, "缺陷类别");
                    d.ExpectedResult = DictGet(d.Properties, "预期结果");
                    d.SketchHtml = DictGet(d.Properties, "示意图") ?? DictGet(d.Properties, "shiyitu");
                    d.DescriptionHtml = dto.Description;
                    d.PublicImageToken = FirstNonEmpty(
                        json.Value<string>("public_image_token"),
                        json["fields"]?.Value<string>("public_image_token"),
                        json["work_item"]?.Value<string>("public_image_token")
                    );
                    d.Tags = (dto.Tags ?? new List<TagDto>()).Select(t => t?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                    d.Comments = await GetWorkItemCommentsAsync(d.Id) ?? new List<WorkItemComment>();
                    return d;
                }
                catch
                {
                }
            }
            return null;
        }
        
        public async Task<List<WorkItemComment>> GetWorkItemCommentsAsync(string workItemId)
        {
            var result = new List<WorkItemComment>();
            if (string.IsNullOrWhiteSpace(workItemId)) return result;
            var candidates = new[]
            {
                $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId)}/comments",
                $"https://open.pingcode.com/v1/agile/work_items/{Uri.EscapeDataString(workItemId)}/comments",
                $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId)}/activities"
            };
            foreach (var url in candidates)
            {
                try
                {
                    var json = await GetJsonAsync(url);
                    var values = GetValuesArray(json);
                    if (values == null || values.Count == 0) continue;
                    foreach (var v in values)
                    {
                        var content = FirstNonEmpty(ExtractString(v["content"]), ExtractString(v["body"]), ExtractString(v["text"]), ExtractString(v["html"]));
                        var authorName = FirstNonEmpty(ExtractString(v["author_name"]), ExtractName(v["author"]), ExtractName(v["user"]), ExtractString(v["created_by_name"]));
                        var authorAvatar = FirstNonEmpty(
                            ExtractString(v["author_avatar"]),
                            ExtractString(v["avatar"]),
                            ExtractString(v["user"]?["avatar"]),
                            ExtractString(v["author"]?["avatar"]),
                            ExtractString(v["created_by"]?["avatar"]),
                            ExtractString(v["fields"]?["author_avatar"]),
                            ExtractString(v["author"]?["image_url"]),
                            ExtractString(v["user"]?["image_url"])
                        );
                        var createdAt = ReadDateTimeFromSeconds(v["created_at"]) ?? ReadDateTimeFromSeconds(v["timestamp"]);
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            result.Add(new WorkItemComment
                            {
                                AuthorName = authorName,
                                AuthorAvatar = authorAvatar,
                                ContentHtml = content,
                                CreatedAt = createdAt
                            });
                        }
                    }
                    return result;
                }
                catch
                {
                }
            }
            return result;
        }
        
        public async Task<bool> UpdateWorkItemStateByIdAsync(string workItemId, string stateId)
        {
            if (string.IsNullOrWhiteSpace(workItemId) || string.IsNullOrWhiteSpace(stateId)) return false;
            var urls = new[]
            {
                $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId)}",
                $"https://open.pingcode.com/v1/agile/work_items/{Uri.EscapeDataString(workItemId)}"
            };
            var bodies = new[]
            {
                // new JObject { ["state"] = new JObject { ["id"] = stateId } },
                new JObject { ["state_id"] = stateId }
            };
            foreach (var url in urls)
            {
                foreach (var body in bodies)
                {
                    try
                    {
                        var resp = await PatchJsonAsync(url, body);
                        if (resp != null) return true;
                    }
                    catch
                    {
                    }
                }
            }
            return false;
        }
        
        public async Task<List<StateDto>> GetWorkItemStatesByTypeAsync(string projectId, string workItemTypeIdOrName)
        {
            var result = new List<StateDto>();
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(workItemTypeIdOrName)) return result;
            var endpoints = new[]
            {
                "https://open.pingcode.com/v1/project/work_item_states",
                "https://open.pingcode.com/v1/project/work_item/states",
                "https://open.pingcode.com/v1/project/work_items/states"
            };
            var paramKeys = new[] { "work_item_type_id", "work_item_type", "type_id", "type" };
            foreach (var ep in endpoints)
            {
                foreach (var key in paramKeys)
                {
                    var url = $"{ep}?project_id={Uri.EscapeDataString(projectId)}&{key}={Uri.EscapeDataString(workItemTypeIdOrName)}&page_size=100";
                    try
                    {
                        var json = await GetJsonAsync(url);
                        var values = GetValuesArray(json);
                        if (values == null || values.Count == 0) continue;
                        var list = values.ToObject<List<StateDto>>() ?? new List<StateDto>();
                        if (list.Count > 0) return list;
                    }
                    catch
                    {
                    }
                }
            }
            return result;
        }
        
        public async Task<List<StateDto>> GetWorkItemStateTransitionsAsync(string projectId, string workItemTypeIdOrName, string fromStateId)
        {
            var result = new List<StateDto>();
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(workItemTypeIdOrName) || string.IsNullOrWhiteSpace(fromStateId)) return result;
            var endpoints = new[]
            {
                "https://open.pingcode.com/v1/project/work_item_state_transitions",
                "https://open.pingcode.com/v1/project/work_item/states/transitions",
                "https://open.pingcode.com/v1/project/work_item_states/transitions",
                "https://open.pingcode.com/v1/project/work_items/state_transitions"
            };
            var paramKeys = new[] { "work_item_type_id", "work_item_type", "type_id", "type" };
            foreach (var ep in endpoints)
            {
                foreach (var key in paramKeys)
                {
                    var url = $"{ep}?project_id={Uri.EscapeDataString(projectId)}&{key}={Uri.EscapeDataString(workItemTypeIdOrName)}&from_state_id={Uri.EscapeDataString(fromStateId)}&page_size=100";
                    try
                    {
                        var json = await GetJsonAsync(url);
                        var values = GetValuesArray(json);
                        if (values == null || values.Count == 0) continue;
                        foreach (var v in values)
                        {
                            var toObj = v["to"] ?? v["target"] ?? v["state"] ?? v;
                            if (toObj != null)
                            {
                                try
                                {
                                    var dto = toObj.ToObject<StateDto>();
                                    if (dto != null && !string.IsNullOrWhiteSpace(dto.Id))
                                    {
                                        result.Add(dto);
                                    }
                                }
                                catch
                                {
                                }
                            }
                        }
                        if (result.Count > 0) return result;
                    }
                    catch
                    {
                    }
                }
            }
            return result;
        }
        
        public async Task<List<StatePlanInfo>> GetWorkItemStatePlansAsync(string projectId)
        {
            var result = new List<StatePlanInfo>();
            if (string.IsNullOrWhiteSpace(projectId)) return result;
            var endpoints = new[]
            {
                "https://open.pingcode.com/v1/project/work_item_state_plans",
                "https://open.pingcode.com/v1/project/work_item/state_plans",
                "https://open.pingcode.com/v1/project/work_items/state_plans"
            };
            foreach (var ep in endpoints)
            {
                var url = $"{ep}?project_id={Uri.EscapeDataString(projectId)}&page_size=100";
                try
                {
                    var json = await GetJsonAsync(url);
                    var values = GetValuesArray(json);
                    if (values == null || values.Count == 0) continue;
                    foreach (var v in values)
                    {
                        var id = v.Value<string>("id");
                        var wtype = v.Value<string>("work_item_type") ?? v["work_item"]?.Value<string>("type");
                        var ptype = v.Value<string>("project_type") ?? v["project"]?.Value<string>("type");
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            result.Add(new StatePlanInfo { Id = id, WorkItemType = wtype, ProjectType = ptype });
                        }
                    }
                    if (result.Count > 0) return result;
                }
                catch
                {
                }
            }
            return result;
        }
        
        public async Task<List<StateDto>> GetWorkItemStateFlowsAsync(string statePlanId, string fromStateId)
        {
            var result = new List<StateDto>();
            if (string.IsNullOrWhiteSpace(statePlanId)) return result;
            var endpoints = new[]
            {
                $"https://open.pingcode.com/v1/project/work_item_state_plans/{Uri.EscapeDataString(statePlanId)}/work_item_state_flows",
                $"https://open.pingcode.com/v1/project/work_item/state_plans/{Uri.EscapeDataString(statePlanId)}/work_item_state_flows",
                $"https://open.pingcode.com/v1/project/work_items/state_plans/{Uri.EscapeDataString(statePlanId)}/work_item_state_flows"
            };
            foreach (var ep in endpoints)
            {
                var url = string.IsNullOrWhiteSpace(fromStateId)
                    ? $"{ep}?page_size=100"
                    : $"{ep}?from_state_id={Uri.EscapeDataString(fromStateId)}&page_size=100";
                try
                {
                    var json = await GetJsonAsync(url);
                    var values = GetValuesArray(json);
                    if (values == null || values.Count == 0) continue;
                    foreach (var v in values)
                    {
                        var toObj = v["to_state"] ?? v["to"] ?? v["target"] ?? v["state"];
                        if (toObj != null)
                        {
                            try
                            {
                                var dto = toObj.ToObject<StateDto>();
                                if (dto != null && !string.IsNullOrWhiteSpace(dto.Id))
                                {
                                    result.Add(dto);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    if (result.Count > 0) return result;
                }
                catch
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
