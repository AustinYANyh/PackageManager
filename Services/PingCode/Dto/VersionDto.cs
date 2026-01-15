using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

public class VersionDto
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("start_at")]
    public long? StartAt { get; set; }

    [JsonProperty("end_at")]
    public long? EndAt { get; set; }

    [JsonProperty("stage")]
    public StageDto Stage { get; set; }
}