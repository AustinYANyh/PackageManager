using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
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

    /// <summary>自定义字段枚举字典缓存：项目 ID → 字段 key → 选项 ID → 显示文本。会话级缓存，新枚举值未命中时自动刷新。</summary>
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> customFieldOptionCache =
        new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>已拉取过字典的项目集合，避免对同项目重复请求属性端点。</summary>
    private readonly HashSet<string> customFieldOptionLoadedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 静态构造：提高对 PingCode 主机的并发连接上限（.NET Framework 默认 2），
    /// 避免悬停预取请求占满通道导致主请求（详情/看板）排队变慢。
    /// </summary>
    static PingCodeApiService()
    {
        System.Net.ServicePointManager.DefaultConnectionLimit = 16;
    }

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
    /// <returns>有效的访问令牌字符串。</returns>
    public async Task<string> GetAccessTokenAsync()
    {
        await EnsureTokenAsync();
        return token;
    }

    /// <summary>
    /// 获取项目列表（兼容多种端点，优先返回首个非空结果）。
    /// </summary>
    /// <returns>项目实体列表。</returns>
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
    /// <param name="projectId">项目的唯一标识。</param>
    /// <returns>未完成的迭代实体列表。</returns>
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
                    result.Add(new Entity { Id = id, Name = nm ?? id, StartAt = v.Value<long?>("start_at"), EndAt = v.Value<long?>("end_at") });
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
    /// <param name="projectId">项目的唯一标识。</param>
    /// <returns>进行中的迭代实体列表。</returns>
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
                    result.Add(new Entity { Id = id, Name = nm ?? id, StartAt = v.Value<long?>("start_at"), EndAt = v.Value<long?>("end_at") });
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
    /// 创建工作项（POST /v1/project/work_items）。type_id 必须使用系统类型标识（缺陷=bug、故事=story、任务=task），
    /// 不能传中文名称；sprint_id 指定迭代；不传 assignee_id 表示处理人留空。
    /// </summary>
    /// <param name="body">工作项字段，至少包含 project_id/title/type_id。</param>
    /// <returns>创建响应（含 id/identifier/html_url 等），失败抛异常。</returns>
    public async Task<JObject> CreateWorkItemAsync(JObject body)
    {
        return await PostJsonAsync("https://open.pingcode.com/v1/project/work_items", body ?? new JObject());
    }

    /// <summary>
    /// 更新工作项（PATCH /v1/project/work_items/{id}）。自定义字段写入 properties（实测 fields 不生效、properties 生效），
    /// 例如写示意图：patch 的 properties.shiyitu 设为 HTML 片段。
    /// </summary>
    /// <param name="workItemId">工作项标识。</param>
    /// <param name="patch">待更新字段，自定义字段放在 properties 下。</param>
    /// <returns>更新成功返回 true；失败抛异常。</returns>
    public async Task<bool> UpdateWorkItemAsync(string workItemId, JObject patch)
    {
        await PatchJsonAsync(
            $"https://open.pingcode.com/v1/project/work_items/{Uri.EscapeDataString(workItemId ?? string.Empty)}",
            patch ?? new JObject());
        return true;
    }

    /// <summary>
    /// 获取项目成员（去重并返回 Id/Name）。
    /// </summary>
    /// <param name="projectId">项目的唯一标识。</param>
    /// <returns>项目成员实体列表。</returns>
    /// <summary>项目成员映射的实例级缓存（projectId → 成员列表）：工作项详情窗每次打开都要拉，
    /// 同项目反复打开时零网络往返。</summary>
    private readonly Dictionary<string, List<Entity>> _projectMembersCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取项目成员列表（带实例级缓存：同项目 10 分钟内复用，成员变化低频）。
    /// </summary>
    /// <param name="projectId">项目唯一标识。</param>
    /// <returns>成员实体列表。</returns>
    public Task<List<Entity>> GetProjectMembersAsync(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Task.FromResult(new List<Entity>());
        }

        lock (_projectMembersCache)
        {
            if (_projectMembersCache.TryGetValue(projectId, out var cached) && cached != null)
            {
                return Task.FromResult(cached);
            }
        }

        return GetProjectMembersRemoteAndCacheAsync(projectId);
    }

    private async Task<List<Entity>> GetProjectMembersRemoteAndCacheAsync(string projectId)
    {
        var result = await GetProjectMembersRemoteAsync(projectId);
        lock (_projectMembersCache)
        {
            _projectMembersCache[projectId] = result;
        }

        return result;
    }

    private async Task<List<Entity>> GetProjectMembersRemoteAsync(string projectId)
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
                    result.Add(new Entity { Id = id, Name = nm ?? id, StartAt = v.Value<long?>("start_at") });
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
    /// <param name="iterationOrSprintId">迭代或冲刺的唯一标识。</param>
    /// <returns>以处理人标识为键、故事点拆分为值的字典。</returns>
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
                    // 仅使用 sprint_id 过滤：开放 API 不识别 iteration_id 参数（会被静默忽略并返回全库工作项），
                    // sprint_id 查询为空即表示该迭代没有工作项，直接结束分页。
                    var url = $"{baseUrl}?sprint_id={Uri.EscapeDataString(iterationOrSprintId)}&page_size={pageSize}&page_index={pageIndex}";
                    var json = await GetJsonAsync(url);
                    var values = GetValuesArray(json);
                    if ((values == null) || (values.Count == 0))
                    {
                        break;
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
                LoggingService.LogError(ex, "获取工作项列表失败");
            }
        }

        return result;
    }

    /// <summary>已拉取过成员映射的项目集合（实例级缓存，跨周期刷新保留，避免每 5 秒重复请求）。</summary>
    private readonly HashSet<string> _iterationLoadedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>成员 ID→名称映射（实例级缓存，跨周期刷新累积复用）。</summary>
    private readonly Dictionary<string, string> _iterationIdNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取迭代内工作项列表（补充参与者与成员映射、状态/优先级/故事点等信息）。
    /// </summary>
    /// <param name="iterationOrSprintId">迭代或冲刺的唯一标识。</param>
    /// <returns>工作项摘要信息列表。</returns>
    public async Task<List<WorkItemInfo>> GetIterationWorkItemsAsync(string iterationOrSprintId)
    {
        var result = new List<WorkItemInfo>();
        var idNameMap = _iterationIdNameMap;
        var loadedProjects = _iterationLoadedProjects;
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
                const int pageSize = 100;

                string PageUrl(int index) =>
                    $"{baseUrl}?sprint_id={Uri.EscapeDataString(iterationOrSprintId)}&page_size={pageSize}&page_index={index}";

                // 首页探测端点并取 total（仅 sprint_id 过滤：开放 API 不识别 iteration_id，
                // 会被静默忽略返回全库工作项；sprint_id 查询为空即该迭代无工作项）
                var firstJson = await GetJsonAsync(PageUrl(0));
                var firstValues = GetValuesArray(firstJson);
                if ((firstValues == null) || (firstValues.Count == 0))
                {
                    break;
                }

                var total = firstJson.Value<int?>("total") ?? firstValues.Count;
                var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));

                // 剩余页并行拉取（限流 4，失败页按空处理），消除串行分页的叠加耗时
                var pageValues = new JArray[pageCount];
                pageValues[0] = firstValues;
                using (var pageGate = new SemaphoreSlim(4))
                {
                    // 数组只含剩余页任务（WhenAll 不接受 null 元素；单页时为空数组同样安全）
                    var fetchTasks = new Task[pageCount - 1];
                    for (var index = 1; index < pageCount; index++)
                    {
                        var pageIndex = index;
                        fetchTasks[pageIndex - 1] = Task.Run(async () =>
                        {
                            await pageGate.WaitAsync();
                            try
                            {
                                var json = await GetJsonAsync(PageUrl(pageIndex));
                                pageValues[pageIndex] = GetValuesArray(json);
                            }
                            catch
                            {
                                // 单页失败按空页处理，不影响其余页
                            }
                            finally
                            {
                                pageGate.Release();
                            }
                        });
                    }

                    await Task.WhenAll(fetchTasks);
                }

                for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
                {
                    var values = pageValues[pageIndex];
                    if ((values == null) || (values.Count == 0))
                    {
                        continue;
                    }

                    var dtos = await Task.Run(() => values.ToObject<List<WorkItemDto>>() ?? new List<WorkItemDto>());

                    // 阶段1（轻量 IO）：本页涉及的新项目先加载成员映射
                    foreach (var projId in dtos.Select(d => d.Project?.Id)
                                                .Where(id => !string.IsNullOrWhiteSpace(id))
                                                .Distinct()
                                                .Where(id => !loadedProjects.Contains(id)))
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

                    // 阶段2（CPU）：整页映射移出 UI 线程，消除周期刷新时的界面卡顿
                    result.AddRange(await Task.Run(() => MapWorkItemDtos(dtos, idNameMap)));
                }

                return result;
            }
            catch (System.Exception ex)
            {
                LoggingService.LogError(ex, "获取迭代内工作项失败");
            }
        }

        return result;
    }

    /// <summary>
    /// 将一页工作项 DTO 映射为看板信息（含参与者/关注者/严重程度/评论数等解析）。
    /// 纯 CPU 计算并更新 <paramref name="idNameMap"/>，供后台线程执行。
    /// </summary>
    /// <param name="dtos">单页工作项 DTO。</param>
    /// <param name="idNameMap">成员 ID 到名称的映射（跨页共享）。</param>
    /// <summary>
    /// 将 PingCode 严重程度选项 ID 或中文/英文文本映射为标准显示文本。
    /// 选项 ID 为 PingCode 内置（不同站点一致且基本不变），看板与详情共用。
    /// </summary>
    /// <param name="raw">原始值（选项 ID 或文本）。</param>
    /// <returns>标准显示文本；无法识别返回原值。</returns>
    internal static string MapSeverityText(string raw)
    {
        var s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }

        switch (s)
        {
            case "5cb7e6e2fda1ce4ca0020004":
                return "致命";
            case "5cb7e6e2fda1ce4ca0020003":
                return "严重";
            case "5cb7e6e2fda1ce4ca0020002":
                return "一般";
            case "5cb7e6e2fda1ce4ca0020001":
                return "建议";
        }

        var lower = s.ToLowerInvariant();
        if (lower.Contains("critical") || s.Contains("致命"))
        {
            return "致命";
        }

        if (s.Contains("严重") || lower.Contains("major"))
        {
            return "严重";
        }

        if (s.Contains("一般") || lower.Contains("normal"))
        {
            return "一般";
        }

        if (s.Contains("建议") || lower.Contains("minor") || lower.Contains("suggest"))
        {
            return "建议";
        }

        return s;
    }

    /// <returns>该页的工作项信息列表。</returns>
    private static List<WorkItemInfo> MapWorkItemDtos(List<WorkItemDto> dtos, Dictionary<string, string> idNameMap)
    {
        var page = new List<WorkItemInfo>();
        foreach (var d in dtos)
        {
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
                {                    severity = sv.ToString();
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

            // PingCode 将严重程度做成了选项 ID（开放 API 返回原始 ID），此处统一翻译为标准文本
            severity = MapSeverityText(severity);

            var endAt = FromUnixSeconds(d.EndAt);
            var startAt = FromUnixSeconds(d.StartAt);
            var completedAt = FromUnixSeconds(d.CompletedAt);
            var updatedAt = FromUnixSeconds(d.UpdatedAt);
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
                CompletedAt = completedAt,
                UpdatedAt = updatedAt,
                CommentCount = commentCount,
                Tags = tagNames,
                ParticipantIds = partIds,
                ParticipantNames = partNames,
                WatcherIds = watcherIds,
                WatcherNames = watcherNames,
            };
            page.Add(wi);
        }

        return page;
    }

    /// <summary>
    /// 将自定义字段（如所属产品 suoshuchanpin）的选项 ID 翻译为显示文本。
    /// 字典来自开放 API 属性列表端点，按项目会话级缓存；未知选项 ID（PingCode 新增枚举）触发一次重新拉取后自愈。
    /// </summary>
    /// <param name="projectId">项目唯一标识。</param>
    /// <param name="fieldKey">自定义字段 key。</param>
    /// <param name="optionId">选项 ID 原始值。</param>
    /// <returns>选项显示文本；无法翻译时返回 null（调用方回退显示原 ID）。</returns>
    private async Task<string> TranslateCustomFieldOptionAsync(string projectId, string fieldKey, string optionId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(fieldKey) || string.IsNullOrWhiteSpace(optionId))
        {
            return null;
        }

        // 命中缓存直接翻译
        if (TryLookupCustomFieldOption(projectId, fieldKey, optionId, out var cachedText))
        {
            return cachedText;
        }

        // 未缓存（首次）或缓存里没有该选项 ID（PingCode 新增枚举）：拉取/重拉字典一次后重查
        var firstLoad = !customFieldOptionLoadedProjects.Contains(projectId);
        await LoadCustomFieldOptionsAsync(projectId, force: firstLoad);
        return TryLookupCustomFieldOption(projectId, fieldKey, optionId, out var reloadedText)
            ? reloadedText
            : null;
    }

    private bool TryLookupCustomFieldOption(string projectId, string fieldKey, string optionId, out string text)
    {
        text = null;
        return customFieldOptionCache.TryGetValue(projectId, out var fields)
               && fields.TryGetValue(fieldKey, out var options)
               && options.TryGetValue(optionId, out text);
    }

    /// <summary>
    /// 拉取指定项目全部自定义字段属性与枚举选项字典并入缓存。同一会话内只拉一次；
    /// 翻译未命中新枚举值时由调用方以 force=true 强制重拉实现自愈。
    /// 各工作项类型的属性页并行拉取（限流 4），单类型失败跳过不影响其余类型。
    /// </summary>
    /// <param name="projectId">项目唯一标识。</param>
    /// <param name="force">是否强制重拉（忽略会话内已拉取标记）。</param>
    private async Task LoadCustomFieldOptionsAsync(string projectId, bool force)
    {
        if (!force && customFieldOptionLoadedProjects.Contains(projectId))
        {
            return;
        }

        try
        {
            var typeIds = await GetProjectWorkItemTypeIdsAsync(projectId);
            Dictionary<string, Dictionary<string, string>>[] typeResults;
            using (var gate = new SemaphoreSlim(4))
            {
                // 每类型独立拉取+解析，互不拖累；失败类型返回 null（跳过）
                typeResults = await Task.WhenAll(typeIds.Select(async typeId =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var url = $"https://open.pingcode.com/v1/pjm/work_item/properties?project_id={Uri.EscapeDataString(projectId)}&work_item_type_id={Uri.EscapeDataString(typeId)}";
                        var json = await GetJsonAsync(url);
                        var values = json?["values"] as JArray;
                        if (values == null || values.Count == 0)
                        {
                            return null;
                        }

                        var parsed = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var property in values.OfType<JObject>())
                        {
                            var key = property.Value<string>("id");
                            var optionArray = property["options"] as JArray;
                            if (string.IsNullOrWhiteSpace(key) || optionArray == null || optionArray.Count == 0)
                            {
                                continue;
                            }

                            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var option in optionArray.OfType<JObject>())
                            {
                                var optionId = option.Value<string>("_id");
                                var optionText = option.Value<string>("text");
                                if (!string.IsNullOrWhiteSpace(optionId) && !string.IsNullOrWhiteSpace(optionText) && !options.ContainsKey(optionId))
                                {
                                    options[optionId] = optionText;
                                }
                            }

                            if (options.Count > 0)
                            {
                                parsed[key] = options;
                            }
                        }

                        return parsed;
                    }
                    catch (System.Exception typeEx)
                    {
                        LoggingService.LogWarning($"拉取工作项类型 {typeId} 的属性字典失败（跳过）：{typeEx.Message}");
                        return null;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }

            // 合并：同字段多类型重复返回时选项取首次出现（正常完全一致）
            var merged = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var parsed in typeResults.Where(r => r != null))
            {
                foreach (var kv in parsed)
                {
                    if (!merged.TryGetValue(kv.Key, out var options))
                    {
                        merged[kv.Key] = kv.Value;
                        continue;
                    }

                    foreach (var opt in kv.Value)
                    {
                        if (!options.ContainsKey(opt.Key))
                        {
                            options[opt.Key] = opt.Value;
                        }
                    }
                }
            }

            if (merged.Count > 0)
            {
                customFieldOptionCache[projectId] = merged;
            }

            customFieldOptionLoadedProjects.Add(projectId);
            if (merged.Count == 0 && typeIds.Count > 0)
            {
                LoggingService.LogWarning($"项目 {projectId} 属性字典加载结果为空，所属产品等自定义字段将显示原始 ID");
            }
        }
        catch (System.Exception ex)
        {
            LoggingService.LogWarning($"项目 {projectId} 属性字典加载失败，自定义字段将显示原始 ID：{ex.Message}");
        }
    }

    /// <summary>
    /// 预取指定项目的自定义字段枚举字典（如所属产品），供看板加载完成后在 UI 上下文后台调用，
    /// 使首次打开工作项详情时命中缓存无需等待网络。
    /// </summary>
    /// <param name="projectId">项目唯一标识。</param>
    /// <returns>异步任务。</returns>
    public Task PrefetchCustomFieldOptionsAsync(string projectId)
    {
        return string.IsNullOrWhiteSpace(projectId) ? Task.CompletedTask : LoadCustomFieldOptionsAsync(projectId, force: false);
    }

    /// <summary>
    /// 获取项目内全部工作项类型 ID（属性端点要求传项目内真实类型 ID，不接受全局 key）。
    /// </summary>
    /// <param name="projectId">项目唯一标识。</param>
    /// <returns>类型 ID 列表。</returns>
    private async Task<List<string>> GetProjectWorkItemTypeIdsAsync(string projectId)
    {
        var result = new List<string>();
        var pageIndex = 0;
        while (true)
        {
            var url = $"https://open.pingcode.com/v1/project/work_item_types?project_id={Uri.EscapeDataString(projectId)}&page_size=100&page_index={pageIndex}";
            var json = await GetJsonAsync(url);
            var values = json?["values"] as JArray;
            if (values != null)
            {
                foreach (var item in values.OfType<JObject>())
                {
                    var id = item.Value<string>("id");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result.Add(id);
                    }
                }
            }

            var total = json?.Value<int?>("total") ?? 0;
            pageIndex++;
            if ((pageIndex * 100) >= total || values == null)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 获取工作项的完整详细信息（兼容多个端点，包含评论与图片令牌）。
    /// </summary>
    /// <param name="workItemId">工作项的唯一标识。</param>
    /// <returns>工作项详细信息；若未找到则返回 <c>null</c>。</returns>
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

                // 评论与详情字段解析并行发起，省一次串行网络往返
                var commentsTask = GetWorkItemCommentsAsync(dto.Id ?? workItemId);
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
                // 所属产品为自定义枚举字段，开放 API 返回选项 ID；经属性字典翻译为显示文本，失败回退原 ID
                var rawProductId = d.ProductName;
                if (!string.IsNullOrWhiteSpace(rawProductId))
                {
                    var translated = await TranslateCustomFieldOptionAsync(d.ProjectId, "suoshuchanpin", rawProductId);
                    if (!string.IsNullOrWhiteSpace(translated))
                    {
                        d.ProductName = translated;
                    }
                }
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
                d.Comments = await commentsTask ?? new List<WorkItemComment>();
                return d;
            }
            catch
            {
            }
        }

        return null;
    }

    /// <summary>
    /// 获取工作项的评论列表（兼容多个端点，含附件和回复信息）。
    /// </summary>
    /// <param name="workItemId">工作项的唯一标识。</param>
    /// <returns>评论列表。</returns>
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

    /// <summary>
    /// 获取指定工作项的子工作项列表。
    /// </summary>
    /// <param name="parentWorkItemId">父工作项的唯一标识。</param>
    /// <returns>子工作项摘要信息列表。</returns>
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
                    // 开放 API 默认按内部 _id 排序，同一秒批量创建的子任务顺序会与网页端（position 顺序）交叉；
                    // 未手动拖拽时编号尾号升序 == position 顺序，按尾号数字重排以对齐网页端展示。
                    result = result.OrderBy(ExtractIdentifierSequenceNumber).ThenBy(c => c.Identifier ?? "", StringComparer.OrdinalIgnoreCase).ToList();
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
    /// 提取工作项编号（如 JD_GROUP-7044）的尾号数字，用于子工作项按创建顺序排序。
    /// </summary>
    /// <param name="item">工作项摘要信息。</param>
    /// <returns>尾号数字；无法解析时返回 long.MaxValue 使其排到末尾。</returns>
    private static long ExtractIdentifierSequenceNumber(WorkItemInfo item)
    {
        var identifier = item?.Identifier ?? "";
        var dashIndex = identifier.LastIndexOf('-');
        var tail = dashIndex >= 0 ? identifier.Substring(dashIndex + 1) : identifier;
        return long.TryParse(tail, out var number) ? number : long.MaxValue;
    }

    /// <summary>
    /// 获取子工作项数量（优先 parent_id 查询，回退 children 端点）。
    /// </summary>
    /// <param name="parentWorkItemId">父工作项的唯一标识。</param>
    /// <returns>子工作项的数量。</returns>
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
    /// <param name="workItemId">工作项的唯一标识。</param>
    /// <param name="contentPayload">结构化评论内容的 JSON 数组。</param>
    /// <returns>创建成功后的评论 JSON 对象；失败返回 <c>null</c>。</returns>
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
    /// <param name="workItemId">工作项的唯一标识。</param>
    /// <param name="contentHtml">评论的 HTML 内容。</param>
    /// <returns>是否创建成功。</returns>
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
    /// <param name="workItemId">工作项的唯一标识。</param>
    /// <param name="contentHtml">评论的 HTML 内容。</param>
    /// <param name="attachments">可选的附件列表（当前未使用）。</param>
    /// <returns>是否创建成功。</returns>
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
    /// <param name="workItemId">工作项的唯一标识。</param>
    /// <param name="contentHtml">评论的 HTML 内容。</param>
    /// <returns>创建成功后的评论 JSON 对象；失败返回 <c>null</c>。</returns>
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
    /// <param name="workItemId">工作项的唯一标识。</param>
    /// <param name="stateId">目标状态的唯一标识。</param>
    /// <returns>是否更新成功。</returns>
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
    /// <param name="workItemId">工作项的唯一标识。</param>
    /// <param name="storyPoints">要设置的故事点数。</param>
    /// <returns>是否更新成功。</returns>
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
    /// <param name="projectId">项目的唯一标识。</param>
    /// <param name="workItemTypeIdOrName">工作项类型的标识或名称。</param>
    /// <returns>状态 DTO 列表。</returns>
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
    /// <param name="projectId">项目的唯一标识。</param>
    /// <param name="workItemTypeIdOrName">工作项类型的标识或名称。</param>
    /// <param name="fromStateId">起始状态的唯一标识。</param>
    /// <returns>可迁移到的目标状态 DTO 列表。</returns>
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
    /// <param name="projectId">项目的唯一标识。</param>
    /// <returns>状态方案信息列表。</returns>
    public async Task<List<StatePlanInfo>> GetWorkItemStatePlansAsync(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return new List<StatePlanInfo>();
        }

        lock (_statePlansCache)
        {
            if (_statePlansCache.TryGetValue(projectId, out var cached) && cached != null)
            {
                return cached;
            }
        }

        var result = await GetWorkItemStatePlansRemoteAsync(projectId);
        if (result.Count > 0)
        {
            lock (_statePlansCache)
            {
                _statePlansCache[projectId] = result;
            }
        }

        return result;
    }

    /// <summary>状态方案列表的实例级缓存（projectId → 方案列表）：方案配置低频变化。</summary>
    private readonly Dictionary<string, List<StatePlanInfo>> _statePlansCache = new(StringComparer.OrdinalIgnoreCase);

    private async Task<List<StatePlanInfo>> GetWorkItemStatePlansRemoteAsync(string projectId)
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
    /// <param name="statePlanId">状态方案的唯一标识。</param>
    /// <param name="fromStateId">起始状态的唯一标识（可为 <c>null</c> 以获取所有流转）。</param>
    /// <returns>可流转到的目标状态 DTO 列表。</returns>
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
    /// <param name="iterationOrSprintId">迭代或冲刺的唯一标识。</param>
    /// <param name="userId">用户的唯一标识。</param>
    /// <returns>该用户在指定迭代内的故事点总和。</returns>
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
                    // 仅使用 sprint_id 过滤：开放 API 不识别 iteration_id 参数（会被静默忽略并返回全库工作项），
                    // sprint_id 查询为空即表示该迭代没有工作项，直接结束分页。
                    var url =
                        $"{baseUrl}?sprint_id={Uri.EscapeDataString(iterationOrSprintId)}&assigned_to={Uri.EscapeDataString(userId)}&page_size={pageSize}&page_index={pageIndex}";
                    var json = await GetJsonAsync(url);
                    var values = GetValuesArray(json);
                    if ((values == null) || (values.Count == 0))
                    {
                        break;
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
