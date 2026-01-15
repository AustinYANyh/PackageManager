using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

public class StateDto
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("color")]
    public string Color { get; set; }
}