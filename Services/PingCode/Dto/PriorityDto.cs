using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

public class PriorityDto
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }
}