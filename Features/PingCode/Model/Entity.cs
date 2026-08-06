namespace PackageManager.Services.PingCode.Model;

/// <summary>
/// 表示具有唯一标识和名称的通用实体。
/// </summary>
public class Entity
{
    /// <summary>
    /// 获取或设置实体的唯一标识。
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置实体的名称。
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// 获取或设置实体的开始时间（Unix 秒），用于迭代按时间排序；非迭代实体为 null。
    /// </summary>
    public long? StartAt { get; set; }
}