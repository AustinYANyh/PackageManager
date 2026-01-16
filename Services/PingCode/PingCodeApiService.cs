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

public class PingCodeApiService
{
    private readonly HttpClient http;

    private readonly DataPersistenceService data;

    private string token;

    private DateTime tokenExpiresAt;

    public PingCodeApiService()
    {
        http = new HttpClient();
        data = new DataPersistenceService();
    }

    public async Task<string> GetAccessTokenAsync()
    {
        await EnsureTokenAsync();
        return token;
    }

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
                    var content = FirstNonEmpty(ExtractString(v["content"]),
                                                ExtractString(v["body"]),
                                                ExtractString(v["text"]),
                                                ExtractString(v["html"]));
                    var attachmentsHtml = await BuildAttachmentsHtmlAsync(v);
                    if (!string.IsNullOrWhiteSpace(attachmentsHtml))
                    {
                        content = string.IsNullOrWhiteSpace(content) ? attachmentsHtml : content + attachmentsHtml;
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
                            AuthorName = authorName,
                            AuthorAvatar = authorAvatar,
                            ContentHtml = content,
                            CreatedAt = createdAt,
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

    public async Task<bool> AddGenericWorkItemCommentAsync(string workItemId, string contentHtml, List<string> attachmentUrls = null)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
        {
            return false;
        }
        var url = "https://open.pingcode.com/v1/comments";
        var body = new JObject
        {
            ["principal_type"] = "work_item",
            ["principal_id"] = workItemId,
            ["content"] = contentHtml ?? ""
        };
        if ((attachmentUrls != null) && (attachmentUrls.Count > 0))
        {
            var arr = new JArray();
            foreach (var u in attachmentUrls.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var it = new JObject
                {
                    ["url"] = u,
                    ["type"] = "image"
                };
                arr.Add(it);
            }
            if (arr.Count > 0)
            {
                body["attachments"] = arr;
            }
        }
        try
        {
            var resp = await PostJsonAsync(url, body);
            return resp != null;
        }
        catch
        {
            return false;
        }
    }

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

        // Highest first to avoid "高" being matched by "最高"
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

    private static bool LooksLikeImageUrl(string url)
    {
        try
        {
            var u = (url ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(u))
            {
                return false;
            }

            if (u.StartsWith("data:image/"))
            {
                return true;
            }

            if (u.EndsWith(".png") || u.EndsWith(".jpg") || u.EndsWith(".jpeg") || u.EndsWith(".gif") || u.EndsWith(".bmp") ||
                u.EndsWith(".webp") || u.EndsWith(".svg"))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private string GetClientId()
    {
        var settings = data.LoadSettings();
        var env = Environment.GetEnvironmentVariable("PINGCODE_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(settings?.PingCodeClientId))
        {
            return settings.PingCodeClientId;
        }

        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return null;
    }

    private string GetClientSecret()
    {
        var settings = data.LoadSettings();
        var env = Environment.GetEnvironmentVariable("PINGCODE_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(settings?.PingCodeClientSecret))
        {
            return settings.PingCodeClientSecret;
        }

        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return null;
    }

    private async Task EnsureTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(token) && (tokenExpiresAt > DateTime.UtcNow.AddMinutes(1)))
        {
            return;
        }

        var clientId = GetClientId();
        var clientSecret = GetClientSecret();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("未配置 PingCode ClientId 或 Secret");
        }

        var authGetUrl =
            $"https://open.pingcode.com/v1/auth/token?grant_type=client_credentials&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}";
        try
        {
            using var resp = await http.GetAsync(authGetUrl);
            var txt = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                var jobj = JObject.Parse(txt);
                var access = jobj.Value<string>("access_token");
                var expires = jobj.Value<int?>("expires_in");
                if (!string.IsNullOrWhiteSpace(access))
                {
                    token = access;
                    tokenExpiresAt = DateTime.UtcNow.AddSeconds(expires ?? 3600);
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            }
            else
            {
                if ((resp.StatusCode == HttpStatusCode.Unauthorized) || (resp.StatusCode == HttpStatusCode.BadRequest))
                {
                    throw new ApiAuthException($"Token 请求失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
                }
            }
        }
        catch (System.Exception)
        {
        }
    }

    private async Task<JObject> GetJsonAsync(string url)
    {
        await EnsureTokenAsync();
        using var resp = await http.GetAsync(url);
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
        using var resp = await http.SendAsync(req);
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

    private async Task<JObject> PostJsonAsync(string url, JObject body)
    {
        await EnsureTokenAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        var payload = body ?? new JObject();
        req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        var txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ApiAuthException($"POST 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new ApiForbiddenException($"POST 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ApiNotFoundException($"POST 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }
            throw new InvalidOperationException($"POST 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
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

    private async Task<string> BuildAttachmentsHtmlAsync(JToken v)
    {
        try
        {
            var arr = v?["attachments"] as JArray;
            if ((arr == null) || (arr.Count == 0))
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var a in arr)
            {
                var url = ExtractString(a?["url"]);
                var title = FirstNonEmpty(ExtractString(a?["title"]), ExtractString(a?["name"]), ExtractString(a?["filename"]));
                var type = FirstNonEmpty(ExtractString(a?["type"]), ExtractString(a?["content_type"]), ExtractString(a?["file_type"]));
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var tt = string.IsNullOrWhiteSpace(title) ? url : title;
                var typeLower = (type ?? "").Trim().ToLowerInvariant();
                var nameLower = (tt ?? "").Trim().ToLowerInvariant();
                var extImg = nameLower.EndsWith(".png") || nameLower.EndsWith(".jpg") || nameLower.EndsWith(".jpeg") || nameLower.EndsWith(".gif") ||
                             nameLower.EndsWith(".bmp") || nameLower.EndsWith(".webp") || nameLower.EndsWith(".svg") || nameLower.EndsWith(".tif") ||
                             nameLower.EndsWith(".tiff") || nameLower.EndsWith(".avif");

                var isOpenAttachment = false;
                string finalUrl = null;
                bool isImg = false;

                try
                {
                    var uri = new Uri(url);
                    var host = (uri.Host ?? "").ToLowerInvariant();
                    var path = (uri.AbsolutePath ?? "").ToLowerInvariant();
                    isOpenAttachment = host.EndsWith(".pingcode.com") && path.Contains("/v1/attachments");
                }
                catch
                {
                }

                if (isOpenAttachment)
                {
                    try
                    {
                        var meta = await GetJsonAsync(AppendAccessTokenIfNeeded(url));
                        var fileType = FirstNonEmpty(meta.Value<string>("file_type"), type);
                        var dl = meta.Value<string>("download_url");
                        var ftLower = (fileType ?? "").Trim().ToLowerInvariant();
                        isImg = (ftLower == "image") || ftLower.StartsWith("image/");
                        if (isImg && !string.IsNullOrWhiteSpace(dl))
                        {
                            finalUrl = dl;
                        }
                    }
                    catch
                    {
                    }
                }

                if (string.IsNullOrWhiteSpace(finalUrl))
                {
                    var u = AppendAccessTokenIfNeeded(url);
                    isImg = (!string.IsNullOrWhiteSpace(typeLower) && typeLower.StartsWith("image/")) || extImg || LooksLikeImageUrl(u);
                    finalUrl = u;
                }

                if (isImg)
                {
                    sb.Append($"<div class=\"comment-attachment\"><img loading=\"lazy\" src=\"{WebUtility.HtmlEncode(finalUrl)}\" alt=\"{WebUtility.HtmlEncode(tt)}\"/></div>");
                }
                else
                {
                    sb.Append($"<div class=\"comment-attachment\"><a href=\"{WebUtility.HtmlEncode(finalUrl)}\" target=\"_blank\" rel=\"noopener\">{WebUtility.HtmlEncode(tt)}</a></div>");
                }
            }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private string AppendAccessTokenIfNeeded(string url)
    {
        try
        {
            var u = (url ?? "").Trim();
            if (string.IsNullOrWhiteSpace(u))
            {
                return u;
            }

            var lower = u.ToLowerInvariant();
            var need = lower.Contains("pingcode.com") || lower.Contains(".pingcode.com");
            if (!need)
            {
                return u;
            }

            if (lower.Contains("access_token="))
            {
                return u;
            }

            var tk = token;
            if (string.IsNullOrWhiteSpace(tk))
            {
                return u;
            }

            if (u.Contains("?"))
            {
                return $"{u}&access_token={Uri.EscapeDataString(tk)}";
            }

            return $"{u}?access_token={Uri.EscapeDataString(tk)}";
        }
        catch
        {
            return url;
        }
    }

    private enum PriorityCategory
    {
        Highest,

        Higher,

        Other,
    }
}
