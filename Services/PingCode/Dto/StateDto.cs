using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示工作项状态的数据传输对象。
/// </summary>
public class StateDto
{
    /// <summary>
    /// 获取或设置状态的唯一标识。
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置状态的 API 资源地址。
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; }

    /// <summary>
    /// 获取或设置状态的名称。
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }

    /// <summary>
    /// 获取或设置状态的类型（如 todo、in_progress、done 等）。
    /// </summary>
    [JsonProperty("type")]
    public string Type { get; set; }

    /// <summary>
    /// 获取或设置状态的显示颜色。
    /// </summary>
    [JsonProperty("color")]
    public string Color { get; set; }
}