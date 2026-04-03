using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示 PingCode 项目的数据传输对象。
/// </summary>
public class ProjectDto
{
    /// <summary>
    /// 获取或设置项目的唯一标识。
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置项目的 API 资源地址。
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; }

    /// <summary>
    /// 获取或设置项目的名称。
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }

    /// <summary>
    /// 获取或设置项目的类型。
    /// </summary>
    [JsonProperty("type")]
    public string Type { get; set; }

    /// <summary>
    /// 获取或设置项目的标识符（如缩写编号前缀）。
    /// </summary>
    [JsonProperty("identifier")]
    public string Identifier { get; set; }

    /// <summary>
    /// 获取或设置项目是否已归档（1 表示已归档，0 表示未归档）。
    /// </summary>
    [JsonProperty("is_archived")]
    public int? IsArchived { get; set; }

    /// <summary>
    /// 获取或设置项目是否已删除（1 表示已删除，0 表示未删除）。
    /// </summary>
    [JsonProperty("is_deleted")]
    public int? IsDeleted { get; set; }
}