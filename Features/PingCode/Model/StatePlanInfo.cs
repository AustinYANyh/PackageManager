namespace PackageManager.Services.PingCode.Model;

/// <summary>
/// 表示状态方案的信息，包含方案标识及其关联的工作项类型和项目类型。
/// </summary>
public class StatePlanInfo
{
    /// <summary>
    /// 获取或设置状态方案的唯一标识。
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置关联的工作项类型。
    /// </summary>
    public string WorkItemType { get; set; }

    /// <summary>
    /// 获取或设置关联的项目类型。
    /// </summary>
    public string ProjectType { get; set; }
}