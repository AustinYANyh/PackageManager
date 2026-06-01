using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示工作项的参与者信息。
/// </summary>
public class ParticipantDto
{
    /// <summary>
    /// 获取或设置参与者的唯一标识。
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置参与者的 API 资源地址。
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; }

    /// <summary>
    /// 获取或设置参与者的类型（如 assignee、watcher 等）。
    /// </summary>
    [JsonProperty("type")]
    public string Type { get; set; }

    /// <summary>
    /// 获取或设置参与者关联的用户信息。
    /// </summary>
    [JsonProperty("user")]
    public UserDto User { get; set; }
}