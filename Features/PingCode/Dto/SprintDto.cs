using Newtonsoft.Json;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示迭代/冲刺的数据传输对象。
/// </summary>
public class SprintDto
{
    /// <summary>
    /// 获取或设置迭代的唯一标识。
    /// </summary>
    [JsonProperty("id")]
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置迭代的 API 资源地址。
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; }

    /// <summary>
    /// 获取或设置迭代的名称。
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; }

    /// <summary>
    /// 获取或设置迭代开始时间（Unix 时间戳，秒）。
    /// </summary>
    [JsonProperty("start_at")]
    public long? StartAt { get; set; }

    /// <summary>
    /// 获取或设置迭代结束时间（Unix 时间戳，秒）。
    /// </summary>
    [JsonProperty("end_at")]
    public long? EndAt { get; set; }

    /// <summary>
    /// 获取或设置迭代的状态（如 in_progress、completed 等）。
    /// </summary>
    [JsonProperty("status")]
    public string Status { get; set; }
}