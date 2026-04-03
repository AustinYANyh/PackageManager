using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示标签的数据传输对象。
/// </summary>
public class TagDto
{
    /// <summary>
    /// 获取或设置标签的唯一标识。
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置标签的 API 资源地址。
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; }

    /// <summary>
    /// 获取或设置标签的名称。
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }
}