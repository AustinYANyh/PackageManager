using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PackageManager.Services.PingCode.Dto;
using PackageManager.Services.PingCode.Exception;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Services.PingCode;

/// <summary>
/// PingCode 开放接口客户端，封装项目、迭代、工作项、评论等查询与操作。
/// </summary>
public partial class PingCodeApiService
{
    private readonly HttpClient http;

    private readonly DataPersistenceService data;

    private string token;

    private DateTime tokenExpiresAt;

    /// <summary>
    /// 初始化服务实例，创建 HTTP 客户端并载入持久化服务。
    /// </summary>
    public PingCodeApiService()
    {
        http = new HttpClient();
        data = new DataPersistenceService();
    }

    /// <summary>
    /// 获取当前访问令牌（自动刷新且返回最新有效令牌）。
    /// </summary>
    public async Task<string> GetAccessTokenAsync()
    {
        await EnsureTokenAsync();
        return token;
    }

    /// <summary>
    /// 获取项目列表（兼容多种端点，优先返回首个非空结果）。
    /// </summary>
    public async Task<List<Entity>> GetProjectsAsync()
    {
        var candidates = new[]
        {
            "https://open.pingcode.com/v1/project/projects?page_size=100",
            "https://open.pingcode.com/v1/agile/projects?page_size=100",
            "https://open.pingcode.com/v1/projects?page_size=100",
        };
        System.Exception last = null;
        foreach (var url in candidates)
        {
            try
            {
                var json = await GetJsonAsync(url);
                var entities = ParseEntities(json);
                if (entities.Count > 0)
                {
                    return entities;
                }
            }
            catch (System.Exception ex)
            {
                last = ex;
            }
        }

        if (last != null)
        {
            throw last;
        }

        return new List<Entity>();
    }

    /// <summary>
    /// 获取指定项目未完成的迭代（过滤 Completed/Done/Closed 等状态）。
    /// </summary>
    public async Task<List<Entity>> GetNotCompletedIterationsByProjectAsync(string projectId)
    {
        var result = new List<Entity>();
        var baseUrl = $"https://open.pingcode.com/v1/project/projects/{Uri.EscapeDataString(projectId)}/sprints";
        var pageIndex = 0;
        var pageSize = 100;
        var seen = new HashSet<string>();
        while (true)
        {
            var url = $"{baseUrl}?page_size={pageSize}&page_index={pageIndex}";
            var json = await GetJsonAsync(url);
            var values = GetValuesArray(json);
            if ((values == null) || (values.Count == 0))
            {
                break;
            }

            foreach (var v in values)
            {
                var id = v.Value<string>("id");
                var nm = v.Value<string>("name");
                var statusText = ReadStatus(v);
                var statusNormalized = (statusText ?? "").Trim().ToLowerInvariant();
                var isCompleted = statusNormalized == "completed" ||
                                  statusNormalized == "done" ||
                                  statusNormalized == "closed" ||
                                  statusNormalized == "finish" ||
                                  statusNormalized == "finished";
                if (isCompleted)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                {
                    result.Add(new Entity { Id = id, Name = nm ?? id });
                }
            }

            var total = json.Value<int?>("total") ?? 0;
            pageIndex++;
            if ((pageIndex * pageSize) >= total)
            {
                break;
            }
        }

        return result;
    }
    
    /// <summary>
    /// 获取指定项目进行中的迭代（status=in_progress）。
    /// </summary>
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
            if ((values == null) || (values.Count == 0))
            {
                break;
            }

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
            if ((pageIndex * pageSize) >= total)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 获取项目成员（去重并返回 Id/Name）。
    /// </summary>
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
            if ((values == null) || (values.Count == 0))
            {
                break;
            }

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
            if ((pageIndex * pageSize) >= total)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 获取迭代内按处理人聚合的故事点拆分（Closed/Done/InProgress/NotStarted 及优先级分布）。
    /// </summary>
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
            "https://open.pingcode.com/v1/agile/work_items",
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
                    if ((values == null) || (values.Count == 0))
                    {
                        url = $"{baseUrl}?iteration_id={Uri.EscapeDataString(iterationOrSprintId)}&page_size={pageSize}&page_index={pageIndex}";
                        json = await GetJsonAsync(url);
                        values = GetValuesArray(json);
                        if ((values == null) || (values.Count == 0))
                        {
                            break;
                        }
                    }

                    foreach (var v in values)
                    {
                        var assignedId = FirstNonEmpty(ExtractId(v["assigned_to"]),
                                                       ExtractId(v["assignee"]),
                                                       ExtractId(v["owner"]),
                                                       ExtractId(v["processor"]),
                                                       ExtractId(v["fields"]?["assigned_to"]),
                                                       ExtractId(v["fields"]?["assignee"]),
                                                       ExtractId(v["fields"]?["owner"]),
                                                       ExtractId(v["fields"]?["processor"]));
                        var assignedName = FirstNonEmpty(ExtractString(v["assigned_to_name"]),
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
                                                         ExtractName(v["fields"]?["processor"]));
                        var keyId = (assignedId ?? "").Trim().ToLowerInvariant();
                        var keyName = (assignedName ?? "").Trim().ToLowerInvariant();
                        if (string.IsNullOrEmpty(keyId) && string.IsNullOrEmpty(keyName))
                        {
                            continue;
                        }

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
                            if (!string.IsNullOrEmpty(keyId))
                            {
                                result[keyId] = bd;
                            }

                            if (!string.IsNullOrEmpty(keyName))
                            {
                                result[keyName] = bd;
                            }
                        }

                        double sp = ReadDouble(v["story_points"]);
                        if (sp == 0)
                        {
                            sp = ReadDouble(v["story_point"]);
                        }

                        if (sp == 0)
                        {
                            sp = ReadDouble(v["fields"]?["story_points"]);
                        }

                        var status = ReadStatus(v);
                        var s = (status ?? "").Trim().ToLowerInvariant();
                        if (s.Contains("closed") || s.Contains("关闭") || s.Contains("已关闭") || s.Contains("已拒绝"))
                        {
                            bd.Closed += sp;
                        }
                        else if (s.Contains("done") || s.Contains("完成") || s.Contains("resolved") || s.Contains("已完成"))
                        {
                            bd.Done += sp;
                        }
                        else if (s.Contains("progress") || s.Contains("进行中") || s.Contains("doing") || s.Contains("开发中") || s.Contains("处理中") ||
                                 s.Contains("in_progress") || s.Contains("可测试") || s.Contains("测试中") || s.Contains("已修复") || s.Contains("挂起"))
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
                    if ((pageIndex * pageSize) >= totalCount)
                    {
                        break;
                    }
                }

                return result;
            }
            catch (System.Exception ex)
            {
            }
        }

        return result;
    }

    /// <summary>
    /// 获取迭代内工作项列表（补充参与者与成员映射、状态/优先级/故事点等信息）。
    /// </summary>
    public async Task<List<WorkItemInfo>> GetIterationWorkItemsAsync(string iterationOrSprintId)
    {
        var result = new List<WorkItemInfo>();
        var idNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var loadedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(iterationOrSprintId))
        {
            return result;
        }

        var baseUrlCandidates = new[]
        {
            "https://open.pingcode.com/v1/project/work_items",
            "https://open.pingcode.com/v1/agile/work_items",
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
                    if ((values == null) || (values.Count == 0))
                    {
                        url = $"{baseUrl}?iteration_id={Uri.EscapeDataString(iterationOrSprintId)}&page_size={pageSize}&page_index={pageIndex}";
                        json = await GetJsonAsync(url);
                        values = GetValuesArray(json);
                        if ((values == null) || (values.Count == 0))
                        {
                            break;
                        }
                    }

                    var dtos = values.ToObject<List<WorkItemDto>>() ?? new List<WorkItemDto>();
                    foreach (var d in dtos)
                    {
                        var projId = d.Project?.Id;
                        if (!string.IsNullOrWhiteSpace(projId) && !loadedProjects.Contains(projId))
                        {
                            try
                            {
                                var members = await GetProjectMembersAsync(projId);
                                foreach (var m in members ?? new List<Entity>())
                                {
                                    var mid = (m?.Id ?? "").Trim();
                                    var mname = (m?.Name ?? "").Trim();
                                    if (!string.IsNullOrWhiteSpace(mid) && !string.IsNullOrWhiteSpace(mname))
                                    {
                                        idNameMap[mid] = mname;
                                    }
                                }

                                loadedProjects.Add(projId);
                            }
                            catch
                            {
                            }
                        }

                        var status = d.State?.Name;
                        var stateId = d.State?.Id;
                        var assigneeId = d.Assignee?.Id;
                        var assigneeName = !string.IsNullOrWhiteSpace(d.Assignee?.DisplayName) ? d.Assignee.DisplayName : d.Assignee?.Name;
                        var assigneeAvatar = d.Assignee?.Avatar;
                        if (!string.IsNullOrWhiteSpace(assigneeId) && !string.IsNullOrWhiteSpace(assigneeName))
                        {
                            idNameMap[assigneeId] = assigneeName;
                        }

                        var prio = d.Priority?.Name;
                        var sp = d.StoryPoints ?? 0;
                        var severity = "";
                        object sv;
                        if (d.Properties != null)
                        {
                            if (d.Properties.TryGetValue("severity", out sv) && (sv != null))
                            {
                                severity = sv.ToString();
                            }
                            else if (d.Properties.TryGetValue("严重程度", out sv) && (sv != null))
                            {
                                severity = sv.ToString();
                            }
                            else if (d.Properties.TryGetValue("严重", out sv) && (sv != null))
                            {
                                severity = sv.ToString();
                            }
                        }

                        var endAt = FromUnixSeconds(d.EndAt);
                        var startAt = FromUnixSeconds(d.StartAt);
                        var commentCount = 0;
                        object cc;
                        if (d.Properties != null)
                        {
                            if (d.Properties.TryGetValue("comment_count", out cc) && (cc != null))
                            {
                                commentCount = ReadInt(cc);
                            }
                            else if (d.Properties.TryGetValue("comments_count", out cc) && (cc != null))
                            {
                                commentCount = ReadInt(cc);
                            }
                            else if (d.Properties.TryGetValue("评论数", out cc) && (cc != null))
                            {
                                commentCount = ReadInt(cc);
                            }
                        }

                        var type = d.Type;
                        var htmlUrl = d.HtmlUrl;
                        var tagNames = (d.Tags ?? new List<TagDto>()).Select(t => t?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                        var partIds = (d.Participants ?? new List<ParticipantDto>())
                                      .Select(p => FirstNonEmpty(p?.User?.Id, p?.Id))
                                      .Where(s => !string.IsNullOrWhiteSpace(s))
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .ToList();
                        var partNames = (d.Participants ?? new List<ParticipantDto>())
                                        .Select(p => FirstNonEmpty(p?.User?.DisplayName, p?.User?.Name))
                                        .Where(s => !string.IsNullOrWhiteSpace(s))
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList();
                        foreach (var p in d.Participants ?? new List<ParticipantDto>())
                        {
                            var uid = p?.User?.Id;
                            var pid = p?.Id;
                            var pnm = FirstNonEmpty(p?.User?.DisplayName, p?.User?.Name);
                            if (!string.IsNullOrWhiteSpace(pnm))
                            {
                                if (!string.IsNullOrWhiteSpace(uid))
                                {
                                    idNameMap[uid] = pnm;
                                }

                                if (!string.IsNullOrWhiteSpace(pid))
                                {
                                    idNameMap[pid] = pnm;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(d.CreatedBy?.Id))
                        {
                            var nm = FirstNonEmpty(d.CreatedBy?.DisplayName, d.CreatedBy?.Name);
                            if (!string.IsNullOrWhiteSpace(nm))
                            {
                                idNameMap[d.CreatedBy.Id] = nm;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(d.UpdatedBy?.Id))
                        {
                            var nm = FirstNonEmpty(d.UpdatedBy?.DisplayName, d.UpdatedBy?.Name);
                            if (!string.IsNullOrWhiteSpace(nm))
                            {
                                idNameMap[d.UpdatedBy.Id] = nm;
                            }
                        }

                        var watcherIds = (d.Participants ?? new List<ParticipantDto>())
                                         .Where(p => !string.IsNullOrWhiteSpace(p?.Type) && (
                                                                                                string.Equals(p.Type,
                                                                                                              "watcher",
                                                                                                              StringComparison.OrdinalIgnoreCase) ||
                                                                                                string.Equals(p.Type,
                                                                                                              "关注者",
                                                                                                              StringComparison.OrdinalIgnoreCase) ||
                                                                                                (p.Type.IndexOf("watch",
                                                                                                                StringComparison.OrdinalIgnoreCase) >=
                                                                                                 0)))
                                         .Select(p => FirstNonEmpty(p?.User?.Id, p?.Id))
                                         .Where(s => !string.IsNullOrWhiteSpace(s))
                                         .Distinct(StringComparer.OrdinalIgnoreCase)
                                         .ToList();
                        var watcherNames = watcherIds.Select(id =>
                        {
                            string nm;
                            return idNameMap.TryGetValue(id, out nm) ? nm : id;
                        }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var propPartIds = new List<string>();
                        var propPartNames = new List<string>();
                        if ((d.Properties != null) && d.Properties.TryGetValue("canyuzhe", out var pv) && (pv != null))
                        {
                            try
                            {
                                if (pv is JArray ja)
                                {
                                    foreach (var x in ja)
                                    {
                                        var id = ExtractId(x);
                                        var name = ExtractName(x);
                                        if (!string.IsNullOrWhiteSpace(id))
                                        {
                                            propPartIds.Add(id);
                                            string nm;
                                            if (idNameMap.TryGetValue(id, out nm))
                                            {
                                                propPartNames.Add(nm);
                                            }
                                        }
                                        else if (!string.IsNullOrWhiteSpace(name))
                                        {
                                            propPartNames.Add(name);
                                        }
                                    }
                                }
                                else
                                {
                                    var txt = pv.ToString();
                                    JArray parsed = null;
                                    try
                                    {
                                        parsed = JArray.Parse(txt);
                                    }
                                    catch
                                    {
                                    }

                                    if (parsed != null)
                                    {
                                        foreach (var x in parsed)
                                        {
                                            var id = ExtractId(x);
                                            var name = ExtractName(x);
                                            if (!string.IsNullOrWhiteSpace(id))
                                            {
                                                propPartIds.Add(id);
                                                string nm;
                                                if (!string.IsNullOrWhiteSpace(name))
                                                {
                                                    propPartNames.Add(name);
                                                }
                                                else if (idNameMap.TryGetValue(id, out nm))
                                                {
                                                    propPartNames.Add(nm);
                                                }
                                            }
                                            else if (!string.IsNullOrWhiteSpace(name))
                                            {
                                                propPartNames.Add(name);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        var parts = txt.Split(new[] { ',', ';', '|', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                        foreach (var s in parts.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)))
                                        {
                                            string nm;
                                            if (idNameMap.TryGetValue(s, out nm))
                                            {
                                                propPartIds.Add(s);
                                                propPartNames.Add(nm);
                                            }
                                            else
                                            {
                                                if (s.Length >= 20)
                                                {
                                                    propPartIds.Add(s);
                                                    if (idNameMap.TryGetValue(s, out nm))
                                                    {
                                                        propPartNames.Add(nm);
                                                    }
                                                }
                                                else
                                                {
                                                    propPartNames.Add(s);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }

                        foreach (var id in propPartIds)
                        {
                            string nm;
                            if (!string.IsNullOrWhiteSpace(id) && idNameMap.TryGetValue(id, out nm))
                            {
                                if (!partNames.Contains(nm, StringComparer.OrdinalIgnoreCase))
                                {
                                    partNames.Add(nm);
                                }

                                if (!partIds.Contains(id))
                                {
                                    partIds.Add(id);
                                }
                            }
                        }

                        foreach (var nm in propPartNames.Where(x => !string.IsNullOrWhiteSpace(x)))
                        {
                            if (!partNames.Contains(nm, StringComparer.OrdinalIgnoreCase))
                            {
                                partNames.Add(nm);
                            }
                        }

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
                            Tags = tagNames,
                            ParticipantIds = partIds,
                            ParticipantNames = partNames,
                            WatcherIds = watcherIds,
                            WatcherNames = watcherNames,
                        };
                        result.Add(wi);
                    }

                    var totalCount = json.Value<int?>("total") ?? 0;
                    pageIndex++;
                    if ((pageIndex * pageSize) >= totalCount)
                    {
                        break;
                    }
                }

                return result;
            }
            catch (System.Exception ex)
            {
            }
        }

        return result;
    }

    public async Task<WorkItemDetails> GetWorkItemDetailsAsync(string workItemId)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
        {
            return null;
        }

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
                if (json == null)
                {
                    continue;
                }

                var dto = json.ToObject<WorkItemDto>();
                if (dto == null)
                {
                    continue;
                }

                var d = new WorkItemDetails();
                d.Id = dto.Id ?? workItemId;
                d.Identifier = dto.Identifier;
                d.Title = dto.Title ?? dto.Identifier ?? dto.Id;
                d.HtmlUrl = dto.HtmlUrl;
                d.Type = dto.Type;
                d.ProjectId = dto.Project?.Id;
                if (dto.Parent != null)
                {
                    d.ParentId = dto.Parent.Id ?? dto.Parent.ShortId ?? dto.Parent.Identifier;
                    d.ParentIdentifier = dto.Parent.Identifier ?? dto.Parent.ShortId ?? dto.Parent.Id;
                    d.ParentTitle = dto.Parent.Title ?? d.ParentIdentifier;
                }

                d.AssigneeId = dto.Assignee?.Id;
                d.AssigneeName = !string.IsNullOrWhiteSpace(dto.Assignee?.DisplayName) ? dto.Assignee.DisplayName : dto.Assignee?.Name;
                d.StateName = dto.State?.Name;
                d.StateType = dto.State?.Type;
                d.StateId = dto.State?.Id;
                d.PriorityName = dto.Priority?.Name;
                d.SeverityName = null;
                d.StoryPoints = dto.StoryPoints ?? 0;
                if ((dto.Properties != null) && dto.Properties.TryGetValue("gushidianhuizong", out var g))
                {
                    double sum = 0;
                    if (g is double gd)
                    {
                        sum = gd;
                    }
                    else if (g != null)
                    {
                        double gg;
                        if (double.TryParse(g.ToString(), out gg))
                        {
                            sum = gg;
                        }
                    }

                    d.StoryPointsSummary = sum;
                }

                d.VersionName = dto.Version?.Name;
                d.StartAt = ReadDateTimeFromSeconds(dto.StartAt);
                d.EndAt = ReadDateTimeFromSeconds(dto.EndAt);
                d.CompletedAt = ReadDateTimeFromSeconds(dto.CompletedAt);
                d.Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in dto.Properties ?? new Dictionary<string, object>())
                {
                    d.Properties[kv.Key] = kv.Value?.ToString();
                }

                d.SeverityName = FirstNonEmpty(DictGet(d.Properties, "severity"),
                                               DictGet(d.Properties, "严重程度"),
                                               DictGet(d.Properties, "严重"));
                d.ProductName = DictGet(d.Properties, "suoshuchanpin");
                d.ReproduceVersion = DictGet(d.Properties, "复现版本号");
                d.ReproduceProbability = DictGet(d.Properties, "复现概率");
                d.DefectCategory = DictGet(d.Properties, "缺陷类别");
                d.ExpectedResult = DictGet(d.Properties, "预期结果");
                d.SketchHtml = DictGet(d.Properties, "示意图") ?? DictGet(d.Properties, "shiyitu");
                d.DescriptionHtml = dto.Description;
                d.PublicImageToken = FirstNonEmpty(json.Value<string>("public_image_token"),
                                                   json["fields"]?.Value<string>("public_image_token"),
                                                   json["work_item"]?.Value<string>("public_image_token"));
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
        if (string.IsNullOrWhiteSpace(workItemId))
        {
            return result;
        }

        var candidates = new[]
        {
            $"https://open.pingcode.com/v1/comments/?principal_type=work_item&principal_id={workItemId}",
            $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId)}/comments",
            $"https://open.pingcode.com/v1/agile/work_items/{Uri.EscapeDataString(workItemId)}/comments",
            $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId)}/activities",
        };
        foreach (var url in candidates)
        {
            try
            {
                var json = await GetJsonAsync(url);
                var values = GetValuesArray(json);
                if ((values == null) || (values.Count == 0))
                {
                    continue;
                }

                foreach (var v in values)
                {
                    var id = FirstNonEmpty(ExtractString(v["id"]), ExtractString(v["comment_id"]));
                    var content = FirstNonEmpty(ExtractString(v["content"]),
                                                ExtractString(v["body"]),
                                                ExtractString(v["text"]),
                                                ExtractString(v["html"]));
                    var attachmentsHtml = await BuildAttachmentsHtmlAsync(v);
                    if (!string.IsNullOrWhiteSpace(attachmentsHtml))
                    {
                        content = string.IsNullOrWhiteSpace(content) ? attachmentsHtml : content + attachmentsHtml;
                    }

                    var repliedObj = v?["replied_comment"];
                    string repliedContent = null;
                    string repliedAuthor = null;
                    string repliedId = null;
                    try
                    {
                        if (repliedObj != null)
                        {
                            if (repliedObj.Type == JTokenType.Object)
                            {
                                repliedContent = FirstNonEmpty(ExtractString(repliedObj["content"]),
                                                               ExtractString(repliedObj["body"]),
                                                               ExtractString(repliedObj["text"]),
                                                               ExtractString(repliedObj["html"]));
                                repliedId = FirstNonEmpty(ExtractString(repliedObj["id"]), ExtractString(repliedObj["comment_id"]));
                                repliedAuthor = FirstNonEmpty(ExtractString(repliedObj.Value<string>("author_name")),
                                                              ExtractName(repliedObj["author"]),
                                                              ExtractName(repliedObj["created_by"]),
                                                              ExtractString(repliedObj.Value<string>("display_name")),
                                                              ExtractString(repliedObj["user"]?["name"]),
                                                              ExtractString(repliedObj.Value<string>("created_by_name")));
                            }
                            else if (repliedObj.Type == JTokenType.Array)
                            {
                                var first = (repliedObj as JArray)?.First;
                                if (first != null)
                                {
                                    if (first.Type == JTokenType.Object)
                                    {
                                        repliedContent = FirstNonEmpty(ExtractString(first["content"]),
                                                                       ExtractString(first["body"]),
                                                                       ExtractString(first["text"]),
                                                                       ExtractString(first["html"]));
                                        repliedId = FirstNonEmpty(ExtractString(first["id"]), ExtractString(first["comment_id"]));
                                        repliedAuthor = FirstNonEmpty(ExtractString(first.Value<string>("author_name")),
                                                                      ExtractName(first["author"]),
                                                                      ExtractName(first["created_by"]),
                                                                      ExtractString(first.Value<string>("display_name")),
                                                                      ExtractString(first["user"]?["name"]),
                                                                      ExtractString(first.Value<string>("created_by_name")));
                                    }
                                    else
                                    {
                                        repliedContent = ExtractString(first);
                                    }
                                }
                            }
                            else
                            {
                                repliedContent = ExtractString(repliedObj);
                            }
                        }
                    }
                    catch
                    {
                    }

                    var authorName = FirstNonEmpty(ExtractName(v["created_by"]),
                                                   ExtractString(v["author_name"]),
                                                   ExtractName(v["author"]),
                                                   ExtractName(v["user"]),
                                                   ExtractString(v["created_by_name"]));
                    var authorAvatar = FirstNonEmpty(ExtractString(v["author_avatar"]),
                                                     ExtractString(v["avatar"]),
                                                     ExtractString(v["user"]?["avatar"]),
                                                     ExtractString(v["author"]?["avatar"]),
                                                     ExtractString(v["created_by"]?["avatar"]),
                                                     ExtractString(v["fields"]?["author_avatar"]),
                                                     ExtractString(v["author"]?["image_url"]),
                                                     ExtractString(v["user"]?["image_url"]));
                    var createdAt = ReadDateTimeFromSeconds(v["created_at"]) ?? ReadDateTimeFromSeconds(v["timestamp"]);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        result.Add(new WorkItemComment
                        {
                            Id = id,
                            AuthorName = authorName,
                            AuthorAvatar = authorAvatar,
                            ContentHtml = content,
                            CreatedAt = createdAt,
                            RepliedAuthorName = repliedAuthor,
                            RepliedContentHtml = repliedContent,
                            RepliedCommentId = repliedId,
                        });
                    }
                }

                try
                {
                    var map = new Dictionary<string, WorkItemComment>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in result)
                    {
                        var cid = (c?.Id ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(cid) && !map.ContainsKey(cid))
                        {
                            map[cid] = c;
                        }
                    }
                    foreach (var c in result)
                    {
                        if ((c != null) && string.IsNullOrWhiteSpace(c.RepliedAuthorName))
                        {
                            var rid = (c.RepliedCommentId ?? "").Trim();
                            if (!string.IsNullOrWhiteSpace(rid) && map.TryGetValue(rid, out var target))
                            {
                                c.RepliedAuthorName = target?.AuthorName;
                            }
                        }
                    }
                }
                catch
                {
                }

                return result;
            }
            catch
            {
            }
        }

        return result;
    }

    public async Task<List<WorkItemInfo>> GetChildWorkItemsAsync(string parentWorkItemId)
    {
        var result = new List<WorkItemInfo>();
        if (string.IsNullOrWhiteSpace(parentWorkItemId))
        {
            return result;
        }

        var baseUrlCandidates = new[]
        {
            "https://open.pingcode.com/v1/project/work_items",
            "https://open.pingcode.com/v1/agile/work_items",
        };
        foreach (var baseUrl in baseUrlCandidates)
        {
            try
            {
                var pageIndex = 0;
                var pageSize = 100;
                while (true)
                {
                    var url = $"{baseUrl}?parent_id={Uri.EscapeDataString(parentWorkItemId)}&page_size={pageSize}&page_index={pageIndex}";
                    var json = await GetJsonAsync(url);
                    var values = GetValuesArray(json);
                    if ((values == null) || (values.Count == 0))
                    {
                        url = $"{baseUrl}/{Uri.EscapeDataString(parentWorkItemId)}/children";
                        json = await GetJsonAsync(url);
                        values = GetValuesArray(json);
                        if ((values == null) || (values.Count == 0))
                        {
                            break;
                        }
                    }

                    var dtos = values.ToObject<List<WorkItemDto>>() ?? new List<WorkItemDto>();
                    foreach (var d in dtos)
                    {
                        var wi = new WorkItemInfo
                        {
                            Id = d.Id ?? d.ShortId,
                            ProjectId = d.Project?.Id,
                            Identifier = d.Identifier ?? d.ShortId ?? d.Id,
                            Title = d.Title ?? d.Identifier ?? d.Id,
                            Status = d.State?.Name,
                            AssigneeId = d.Assignee?.Id,
                            AssigneeName = !string.IsNullOrWhiteSpace(d.Assignee?.DisplayName) ? d.Assignee.DisplayName : d.Assignee?.Name,
                            HtmlUrl = d.HtmlUrl,
                            StartAt = ReadDateTimeFromSeconds(d.StartAt),
                            EndAt = ReadDateTimeFromSeconds(d.EndAt),
                        };
                        result.Add(wi);
                    }

                    var totalCount = json.Value<int?>("total") ?? 0;
                    pageIndex++;
                    if ((pageIndex * pageSize) >= totalCount)
                    {
                        break;
                    }
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }
            catch
            {
            }
        }

        return result;
    }

    /// <summary>
    /// 获取子工作项数量（优先 parent_id 查询，回退 children 端点）。
    /// </summary>
    public async Task<int> GetChildWorkItemCountAsync(string parentWorkItemId)
    {
        if (string.IsNullOrWhiteSpace(parentWorkItemId))
        {
            return 0;
        }
        var baseUrlCandidates = new[]
        {
            "https://open.pingcode.com/v1/project/work_items",
            "https://open.pingcode.com/v1/agile/work_items",
        };
        foreach (var baseUrl in baseUrlCandidates)
        {
            try
            {
                var url = $"{baseUrl}?parent_id={Uri.EscapeDataString(parentWorkItemId)}&page_size=1&page_index=0";
                var json = await GetJsonAsync(url);
                var total = json?.Value<int?>("total") ?? 0;
                if (total > 0)
                {
                    return total;
                }
                url = $"{baseUrl}/{Uri.EscapeDataString(parentWorkItemId)}/children?page_size=1&page_index=0";
                json = await GetJsonAsync(url);
                total = json?.Value<int?>("total") ?? 0;
                if (total > 0)
                {
                    return total;
                }
            }
            catch
            {
            }
        }
        return 0;
    }

    /// <summary>
    /// 创建结构化评论（content 为结构化 payload，支持 @提及）。
    /// </summary>
    public async Task<JObject> CreateWorkItemCommentWithPayloadAsync(string workItemId, JArray contentPayload)
    {
        if (string.IsNullOrWhiteSpace(workItemId) || contentPayload == null)
        {
            return null;
        }
        var url = "https://open.pingcode.com/v1/comments";
        var body = new JObject
        {
            ["principal_type"] = "work_item",
            ["principal_id"] = workItemId,
            ["content"] = contentPayload
        };
        try
        {
            var resp = await PostJsonAsync(url, body);
            return resp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 创建普通评论（content 为 HTML 字符串），兼容不同字段名。
    /// </summary>
    public async Task<bool> AddWorkItemCommentAsync(string workItemId, string contentHtml)
    {
        if (string.IsNullOrWhiteSpace(workItemId) || string.IsNullOrWhiteSpace(contentHtml))
        {
            return false;
        }
        var urls = new[]
        {
            $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId)}/comments",
            $"https://open.pingcode.com/v1/agile/work_items/{Uri.EscapeDataString(workItemId)}/comments",
        };
        var bodies = new[]
        {
            new JObject { ["content"] = contentHtml },
            new JObject { ["html"] = contentHtml },
            new JObject { ["body"] = contentHtml },
        };
        foreach (var url in urls)
        {
            foreach (var body in bodies)
            {
                try
                {
                    var resp = await PostJsonAsync(url, body);
                    if (resp != null)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 创建通用评论（仅发送正文），返回是否成功。
    /// </summary>
    public async Task<bool> AddGenericWorkItemCommentAsync(string workItemId, string contentHtml, List<JObject> attachments = null)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
        {
            return false;
        }
        var resp = await CreateGenericWorkItemCommentAsync(workItemId, contentHtml);
        return resp != null;
    }

    /// <summary>
    /// 创建通用评论并返回响应对象。
    /// </summary>
    public async Task<JObject> CreateGenericWorkItemCommentAsync(string workItemId, string contentHtml)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
        {
            return null;
        }
        var url = "https://open.pingcode.com/v1/comments";
        var body = new JObject
        {
            ["principal_type"] = "work_item",
            ["principal_id"] = workItemId,
            ["content"] = contentHtml ?? ""
        };
        try
        {
            var resp = await PostJsonAsync(url, body);
            return resp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 更新工作项状态（兼容 state_id 字段写入）。
    /// </summary>
    public async Task<bool> UpdateWorkItemStateByIdAsync(string workItemId, string stateId)
    {
        if (string.IsNullOrWhiteSpace(workItemId) || string.IsNullOrWhiteSpace(stateId))
        {
            return false;
        }

        var urls = new[]
        {
            $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId)}",
            $"https://open.pingcode.com/v1/agile/work_items/{Uri.EscapeDataString(workItemId)}",
        };
        var bodies = new[]
        {
            // new JObject { ["state"] = new JObject { ["id"] = stateId } },
            new JObject { ["state_id"] = stateId },
        };
        foreach (var url in urls)
        {
            foreach (var body in bodies)
            {
                try
                {
                    var resp = await PatchJsonAsync(url, body);
                    if (resp != null)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 更新工作项故事点（兼容 story_points/story_point 字段）。
    /// </summary>
    public async Task<bool> UpdateWorkItemStoryPointsAsync(string workItemId, double storyPoints)
    {
        if (string.IsNullOrWhiteSpace(workItemId) || (storyPoints < 0))
        {
            return false;
        }

        var urls = new[]
        {
            $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId)}",
            $"https://open.pingcode.com/v1/agile/work_items/{Uri.EscapeDataString(workItemId)}",
        };
        var bodies = new[]
        {
            new JObject { ["story_points"] = storyPoints },
            new JObject { ["story_point"] = storyPoints },
        };
        foreach (var url in urls)
        {
            foreach (var body in bodies)
            {
                try
                {
                    var resp = await PatchJsonAsync(url, body);
                    if (resp != null)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 获取某工作项类型下的状态列表（兼容多个端点与参数键）。
    /// </summary>
    public async Task<List<StateDto>> GetWorkItemStatesByTypeAsync(string projectId, string workItemTypeIdOrName)
    {
        var result = new List<StateDto>();
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(workItemTypeIdOrName))
        {
            return result;
        }

        var endpoints = new[]
        {
            "https://open.pingcode.com/v1/project/work_item_states",
            "https://open.pingcode.com/v1/project/work_item/states",
            "https://open.pingcode.com/v1/project/work_items/states",
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
                    if ((values == null) || (values.Count == 0))
                    {
                        continue;
                    }

                    var list = values.ToObject<List<StateDto>>() ?? new List<StateDto>();
                    if (list.Count > 0)
                    {
                        return list;
                    }
                }
                catch
                {
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 获取从指定状态可迁移到的目标状态列表（兼容多个端点与参数键）。
    /// </summary>
    public async Task<List<StateDto>> GetWorkItemStateTransitionsAsync(string projectId, string workItemTypeIdOrName, string fromStateId)
    {
        var result = new List<StateDto>();
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(workItemTypeIdOrName) || string.IsNullOrWhiteSpace(fromStateId))
        {
            return result;
        }

        var endpoints = new[]
        {
            "https://open.pingcode.com/v1/project/work_item_state_transitions",
            "https://open.pingcode.com/v1/project/work_item/states/transitions",
            "https://open.pingcode.com/v1/project/work_item_states/transitions",
            "https://open.pingcode.com/v1/project/work_items/state_transitions",
        };
        var paramKeys = new[] { "work_item_type_id", "work_item_type", "type_id", "type" };
        foreach (var ep in endpoints)
        {
            foreach (var key in paramKeys)
            {
                var url =
                    $"{ep}?project_id={Uri.EscapeDataString(projectId)}&{key}={Uri.EscapeDataString(workItemTypeIdOrName)}&from_state_id={Uri.EscapeDataString(fromStateId)}&page_size=100";
                try
                {
                    var json = await GetJsonAsync(url);
                    var values = GetValuesArray(json);
                    if ((values == null) || (values.Count == 0))
                    {
                        continue;
                    }

                    foreach (var v in values)
                    {
                        var toObj = v["to"] ?? v["target"] ?? v["state"] ?? v;
                        if (toObj != null)
                        {
                            try
                            {
                                var dto = toObj.ToObject<StateDto>();
                                if ((dto != null) && !string.IsNullOrWhiteSpace(dto.Id))
                                {
                                    result.Add(dto);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }

                    if (result.Count > 0)
                    {
                        return result;
                    }
                }
                catch
                {
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 获取项目下的状态方案列表（返回方案 Id 与项目/工作项类型信息）。
    /// </summary>
    public async Task<List<StatePlanInfo>> GetWorkItemStatePlansAsync(string projectId)
    {
        var result = new List<StatePlanInfo>();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return result;
        }

        var endpoints = new[]
        {
            "https://open.pingcode.com/v1/project/work_item_state_plans",
            "https://open.pingcode.com/v1/project/work_item/state_plans",
            "https://open.pingcode.com/v1/project/work_items/state_plans",
        };
        foreach (var ep in endpoints)
        {
            var url = $"{ep}?project_id={Uri.EscapeDataString(projectId)}&page_size=100";
            try
            {
                var json = await GetJsonAsync(url);
                var values = GetValuesArray(json);
                if ((values == null) || (values.Count == 0))
                {
                    continue;
                }

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

                if (result.Count > 0)
                {
                    return result;
                }
            }
            catch
            {
            }
        }

        return result;
    }

    /// <summary>
    /// 获取状态方案内的状态流转（可指定 fromStateId 过滤）。
    /// </summary>
    public async Task<List<StateDto>> GetWorkItemStateFlowsAsync(string statePlanId, string fromStateId)
    {
        var result = new List<StateDto>();
        if (string.IsNullOrWhiteSpace(statePlanId))
        {
            return result;
        }

        var endpoints = new[]
        {
            $"https://open.pingcode.com/v1/project/work_item_state_plans/{Uri.EscapeDataString(statePlanId)}/work_item_state_flows",
            $"https://open.pingcode.com/v1/project/work_item/state_plans/{Uri.EscapeDataString(statePlanId)}/work_item_state_flows",
            $"https://open.pingcode.com/v1/project/work_items/state_plans/{Uri.EscapeDataString(statePlanId)}/work_item_state_flows",
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
                if ((values == null) || (values.Count == 0))
                {
                    continue;
                }

                foreach (var v in values)
                {
                    var toObj = v["to_state"] ?? v["to"] ?? v["target"] ?? v["state"];
                    if (toObj != null)
                    {
                        try
                        {
                            var dto = toObj.ToObject<StateDto>();
                            if ((dto != null) && !string.IsNullOrWhiteSpace(dto.Id))
                            {
                                result.Add(dto);
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }
            catch
            {
            }
        }

        return result;
    }

    /// <summary>
    /// 计算用户在迭代内的故事点总和（迭代/冲刺 + 指派过滤）。
    /// </summary>
    public async Task<double> GetUserStoryPointsSumAsync(string iterationOrSprintId, string userId)
    {
        if (string.IsNullOrWhiteSpace(iterationOrSprintId) || string.IsNullOrWhiteSpace(userId))
        {
            return 0;
        }

        var baseUrlCandidates = new[]
        {
            "https://open.pingcode.com/v1/project/work_items",
            "https://open.pingcode.com/v1/agile/work_items",
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
                    var url =
                        $"{baseUrl}?sprint_id={Uri.EscapeDataString(iterationOrSprintId)}&assigned_to={Uri.EscapeDataString(userId)}&page_size={pageSize}&page_index={pageIndex}";
                    var json = await GetJsonAsync(url);
                    var values = GetValuesArray(json);
                    if ((values == null) || (values.Count == 0))
                    {
                        url =
                            $"{baseUrl}?iteration_id={Uri.EscapeDataString(iterationOrSprintId)}&assigned_to={Uri.EscapeDataString(userId)}&page_size={pageSize}&page_index={pageIndex}";
                        json = await GetJsonAsync(url);
                        values = GetValuesArray(json);
                        if ((values == null) || (values.Count == 0))
                        {
                            break;
                        }
                    }

                    foreach (var v in values)
                    {
                        double sp = ReadDouble(v["story_points"]);
                        if (sp == 0)
                        {
                            sp = ReadDouble(v["story_point"]);
                        }

                        if (sp == 0)
                        {
                            sp = ReadDouble(v["fields"]?["story_points"]);
                        }

                        total += sp;
                    }

                    var totalCount = json.Value<int?>("total") ?? 0;
                    pageIndex++;
                    if ((pageIndex * pageSize) >= totalCount)
                    {
                        break;
                    }
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
