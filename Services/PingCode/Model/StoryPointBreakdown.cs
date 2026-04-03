namespace PackageManager.Services.PingCode.Model;

/// <summary>
/// 表示按状态和优先级拆分的故事点统计信息。
/// </summary>
public class StoryPointBreakdown
{
    /// <summary>
    /// 获取或设置未开始状态的故事点总和。
    /// </summary>
    public double NotStarted { get; set; }

    /// <summary>
    /// 获取或设置进行中状态的故事点总和。
    /// </summary>
    public double InProgress { get; set; }

    /// <summary>
    /// 获取或设置已完成状态的故事点总和。
    /// </summary>
    public double Done { get; set; }

    /// <summary>
    /// 获取或设置已关闭状态的故事点总和。
    /// </summary>
    public double Closed { get; set; }

    /// <summary>
    /// 获取或设置所有状态的故事点总计。
    /// </summary>
    public double Total { get; set; }

    /// <summary>
    /// 获取或设置最高优先级的工作项数量。
    /// </summary>
    public int HighestPriorityCount { get; set; }

    /// <summary>
    /// 获取或设置最高优先级的故事点总和。
    /// </summary>
    public double HighestPriorityPoints { get; set; }

    /// <summary>
    /// 获取或设置较高优先级的工作项数量。
    /// </summary>
    public int HigherPriorityCount { get; set; }

    /// <summary>
    /// 获取或设置较高优先级的故事点总和。
    /// </summary>
    public double HigherPriorityPoints { get; set; }

    /// <summary>
    /// 获取或设置其他优先级的工作项数量。
    /// </summary>
    public int OtherPriorityCount { get; set; }

    /// <summary>
    /// 获取或设置其他优先级的故事点总和。
    /// </summary>
    public double OtherPriorityPoints { get; set; }
}