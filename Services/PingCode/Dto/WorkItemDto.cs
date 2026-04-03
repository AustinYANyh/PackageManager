using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示工作项的数据传输对象，映射 PingCode API 返回的工作项 JSON 结构。
/// </summary>
public class WorkItemDto
{
    /// <summary>
    /// 获取或设置工作项的唯一标识。
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置工作项的 API 资源地址。
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; }

    /// <summary>
    /// 获取或设置工作项所属的项目信息。
    /// </summary>
    [JsonProperty("project")]
    public ProjectDto Project { get; set; }

    /// <summary>
    /// 获取或设置工作项的标识符（如 PROJ-123）。
    /// </summary>
    [JsonProperty("identifier")]
    public string Identifier { get; set; }

    /// <summary>
    /// 获取或设置工作项的标题。
    /// </summary>
    [JsonProperty("title")]
    public string Title { get; set; }

    /// <summary>
    /// 获取或设置工作项的类型（如 story、bug、task 等）。
    /// </summary>
    [JsonProperty("type")]
    public string Type { get; set; }

    /// <summary>
    /// 获取或设置工作项开始时间（Unix 时间戳，秒）。
    /// </summary>
    [JsonProperty("start_at")]
    public long? StartAt { get; set; }

    /// <summary>
    /// 获取或设置工作项结束时间（Unix 时间戳，秒）。
    /// </summary>
    [JsonProperty("end_at")]
    public long? EndAt { get; set; }

    /// <summary>
    /// 获取或设置父工作项的唯一标识。
    /// </summary>
    [JsonProperty("parent_id")]
    public string ParentId { get; set; }

    /// <summary>
    /// 获取或设置工作项的短标识。
    /// </summary>
    [JsonProperty("short_id")]
    public string ShortId { get; set; }

    /// <summary>
    /// 获取或设置工作项在 Web 端的访问地址。
    /// </summary>
    [JsonProperty("html_url")]
    public string HtmlUrl { get; set; }

    /// <summary>
    /// 获取或设置父工作项的完整信息。
    /// </summary>
    [JsonProperty("parent")]
    public WorkItemDto Parent { get; set; }

    /// <summary>
    /// 获取或设置工作项的指派人信息。
    /// </summary>
    [JsonProperty("assignee")]
    public UserDto Assignee { get; set; }

    /// <summary>
    /// 获取或设置工作项的状态信息。
    /// </summary>
    [JsonProperty("state")]
    public StateDto State { get; set; }

    /// <summary>
    /// 获取或设置工作项的优先级信息。
    /// </summary>
    [JsonProperty("priority")]
    public PriorityDto Priority { get; set; }

    /// <summary>
    /// 获取或设置工作项所属的版本信息。
    /// </summary>
    [JsonProperty("version")]
    public VersionDto Version { get; set; }

    /// <summary>
    /// 获取或设置工作项所属的迭代/冲刺信息。
    /// </summary>
    [JsonProperty("sprint")]
    public SprintDto Sprint { get; set; }

    /// <summary>
    /// 获取或设置工作项的阶段。
    /// </summary>
    [JsonProperty("phase")]
    public string Phase { get; set; }

    /// <summary>
    /// 获取或设置工作项的故事点数。
    /// </summary>
    [JsonProperty("story_points")]
    public double? StoryPoints { get; set; }

    /// <summary>
    /// 获取或设置工作项的预估工作量。
    /// </summary>
    [JsonProperty("estimated_workload")]
    public double? EstimatedWorkload { get; set; }

    /// <summary>
    /// 获取或设置工作项的剩余工作量。
    /// </summary>
    [JsonProperty("remaining_workload")]
    public double? RemainingWorkload { get; set; }

    /// <summary>
    /// 获取或设置工作项的描述内容（HTML 格式）。
    /// </summary>
    [JsonProperty("description")]
    public string Description { get; set; }

    /// <summary>
    /// 获取或设置工作项的完成时间（Unix 时间戳，秒）。
    /// </summary>
    [JsonProperty("completed_at")]
    public long? CompletedAt { get; set; }

    /// <summary>
    /// 获取或设置工作项的扩展属性字典。
    /// </summary>
    [JsonProperty("properties")]
    public Dictionary<string, object> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取或设置工作项的标签列表。
    /// </summary>
    [JsonProperty("tags")]
    public List<TagDto> Tags { get; set; } = new();

    /// <summary>
    /// 获取或设置工作项的参与者列表。
    /// </summary>
    [JsonProperty("participants")]
    public List<ParticipantDto> Participants { get; set; } = new();

    /// <summary>
    /// 获取或设置工作项的创建时间（Unix 时间戳，秒）。
    /// </summary>
    [JsonProperty("created_at")]
    public long? CreatedAt { get; set; }

    /// <summary>
    /// 获取或设置工作项的创建者信息。
    /// </summary>
    [JsonProperty("created_by")]
    public UserDto CreatedBy { get; set; }

    /// <summary>
    /// 获取或设置工作项的更新时间（Unix 时间戳，秒）。
    /// </summary>
    [JsonProperty("updated_at")]
    public long? UpdatedAt { get; set; }

    /// <summary>
    /// 获取或设置工作项的最后更新者信息。
    /// </summary>
    [JsonProperty("updated_by")]
    public UserDto UpdatedBy { get; set; }

    /// <summary>
    /// 获取或设置工作项是否已归档（1 表示已归档，0 表示未归档）。
    /// </summary>
    [JsonProperty("is_archived")]
    public int? IsArchived { get; set; }

    /// <summary>
    /// 获取或设置工作项是否已删除（1 表示已删除，0 表示未删除）。
    /// </summary>
    [JsonProperty("is_deleted")]
    public int? IsDeleted { get; set; }
}