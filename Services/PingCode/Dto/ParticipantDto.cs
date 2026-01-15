using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

public class ParticipantDto
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("user")]
    public UserDto User { get; set; }
}