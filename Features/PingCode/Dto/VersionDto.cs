using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示版本的数据传输对象。
/// </summary>
public class VersionDto
{
    /// <summary>
    /// 获取或设置版本的唯一标识。
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置版本的 API 资源地址。
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; }

    /// <summary>
    /// 获取或设置版本的名称。
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }

    /// <summary>
    /// 获取或设置版本开始时间（Unix 时间戳，秒）。
    /// </summary>
    [JsonProperty("start_at")]
    public long? StartAt { get; set; }

    /// <summary>
    /// 获取或设置版本结束时间（Unix 时间戳，秒）。
    /// </summary>
    [JsonProperty("end_at")]
    public long? EndAt { get; set; }

    /// <summary>
    /// 获取或设置版本所处的阶段信息。
    /// </summary>
    [JsonProperty("stage")]
    public StageDto Stage { get; set; }
}