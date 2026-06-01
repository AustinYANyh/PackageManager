using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示用户的数据传输对象。
/// </summary>
public class UserDto
{
    /// <summary>
    /// 获取或设置用户的唯一标识。
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置用户的 API 资源地址。
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; }

    /// <summary>
    /// 获取或设置用户的用户名。
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }

    /// <summary>
    /// 获取或设置用户的显示名称。
    /// </summary>
    [JsonProperty("display_name")]
    public string DisplayName { get; set; }

    /// <summary>
    /// 获取或设置用户头像的 URL 地址。
    /// </summary>
    [JsonProperty("avatar")]
    public string Avatar { get; set; }
}