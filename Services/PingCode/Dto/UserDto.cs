using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

public class UserDto
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("display_name")]
    public string DisplayName { get; set; }

    [JsonProperty("avatar")]
    public string Avatar { get; set; }
}