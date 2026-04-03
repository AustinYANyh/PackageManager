using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示版本阶段的数据传输对象。
/// </summary>
public class StageDto
{
    /// <summary>
    /// 获取或设置阶段的唯一标识。
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置阶段的 API 资源地址。
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; }

    /// <summary>
    /// 获取或设置阶段的名称。
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }

    /// <summary>
    /// 获取或设置阶段的类型。
    /// </summary>
    [JsonProperty("type")]
    public string Type { get; set; }

    /// <summary>
    /// 获取或设置阶段的显示颜色。
    /// </summary>
    [JsonProperty("color")]
    public string Color { get; set; }
}