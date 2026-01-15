using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

public class ProjectDto
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("identifier")]
    public string Identifier { get; set; }

    [JsonProperty("is_archived")]
    public int? IsArchived { get; set; }

    [JsonProperty("is_deleted")]
    public int? IsDeleted { get; set; }
}