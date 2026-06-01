using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示工作项的优先级信息。
/// </summary>
public class PriorityDto
{
    /// <summary>
    /// 获取或设置优先级的唯一标识。
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置优先级的 API 资源地址。
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; }

    /// <summary>
    /// 获取或设置优先级的名称。
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }
}