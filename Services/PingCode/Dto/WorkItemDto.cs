using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

public class WorkItemDto
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonProperty("project")]
    public ProjectDto Project { get; set; }

    [JsonProperty("identifier")]
    public string Identifier { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("start_at")]
    public long? StartAt { get; set; }

    [JsonProperty("end_at")]
    public long? EndAt { get; set; }

    [JsonProperty("parent_id")]
    public string ParentId { get; set; }

    [JsonProperty("short_id")]
    public string ShortId { get; set; }

    [JsonProperty("html_url")]
    public string HtmlUrl { get; set; }

    [JsonProperty("parent")]
    public WorkItemDto Parent { get; set; }

    [JsonProperty("assignee")]
    public UserDto Assignee { get; set; }

    [JsonProperty("state")]
    public StateDto State { get; set; }

    [JsonProperty("priority")]
    public PriorityDto Priority { get; set; }

    [JsonProperty("version")]
    public VersionDto Version { get; set; }

    [JsonProperty("sprint")]
    public SprintDto Sprint { get; set; }

    [JsonProperty("phase")]
    public string Phase { get; set; }

    [JsonProperty("story_points")]
    public double? StoryPoints { get; set; }

    [JsonProperty("estimated_workload")]
    public double? EstimatedWorkload { get; set; }

    [JsonProperty("remaining_workload")]
    public double? RemainingWorkload { get; set; }

    [JsonProperty("description")]
    public string Description { get; set; }

    [JsonProperty("completed_at")]
    public long? CompletedAt { get; set; }

    [JsonProperty("properties")]
    public Dictionary<string, object> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonProperty("tags")]
    public List<TagDto> Tags { get; set; } = new();

    [JsonProperty("participants")]
    public List<ParticipantDto> Participants { get; set; } = new();

    [JsonProperty("created_at")]
    public long? CreatedAt { get; set; }

    [JsonProperty("created_by")]
    public UserDto CreatedBy { get; set; }

    [JsonProperty("updated_at")]
    public long? UpdatedAt { get; set; }

    [JsonProperty("updated_by")]
    public UserDto UpdatedBy { get; set; }

    [JsonProperty("is_archived")]
    public int? IsArchived { get; set; }

    [JsonProperty("is_deleted")]
    public int? IsDeleted { get; set; }
}