using System;
using System.Collections.Generic;

namespace PackageManager.Services.PingCode.Model;

/// <summary>
/// 表示工作项的详细信息，包含状态、优先级、描述、评论等完整内容。
/// </summary>
public class WorkItemDetails
{
    /// <summary>
    /// 获取或设置工作项的唯一标识。
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置工作项的标识符（如 PROJ-123）。
    /// </summary>
    public string Identifier { get; set; }

    /// <summary>
    /// 获取或设置工作项的标题。
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// 获取或设置工作项在 Web 端的访问地址。
    /// </summary>
    public string HtmlUrl { get; set; }

    /// <summary>
    /// 获取或设置工作项的类型（如 story、bug、task 等）。
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// 获取或设置工作项所属项目的唯一标识。
    /// </summary>
    public string ProjectId { get; set; }

    /// <summary>
    /// 获取或设置父工作项的唯一标识。
    /// </summary>
    public string ParentId { get; set; }

    /// <summary>
    /// 获取或设置父工作项的标识符。
    /// </summary>
    public string ParentIdentifier { get; set; }

    /// <summary>
    /// 获取或设置父工作项的标题。
    /// </summary>
    public string ParentTitle { get; set; }

    /// <summary>
    /// 获取或设置工作项指派人的唯一标识。
    /// </summary>
    public string AssigneeId { get; set; }

    /// <summary>
    /// 获取或设置工作项指派人的名称。
    /// </summary>
    public string AssigneeName { get; set; }

    /// <summary>
    /// 获取或设置工作项的状态名称。
    /// </summary>
    public string StateName { get; set; }

    /// <summary>
    /// 获取或设置工作项的状态类型（如 todo、in_progress、done 等）。
    /// </summary>
    public string StateType { get; set; }

    /// <summary>
    /// 获取或设置工作项状态的唯一标识。
    /// </summary>
    public string StateId { get; set; }

    /// <summary>
    /// 获取或设置工作项的优先级名称。
    /// </summary>
    public string PriorityName { get; set; }

    /// <summary>
    /// 获取或设置工作项的严重程度名称。
    /// </summary>
    public string SeverityName { get; set; }

    /// <summary>
    /// 获取或设置工作项的故事点数。
    /// </summary>
    public double StoryPoints { get; set; }

    /// <summary>
    /// 获取或设置故事点汇总值（含子工作项）。
    /// </summary>
    public double StoryPointsSummary { get; set; }

    /// <summary>
    /// 获取或设置工作项所属版本的名称。
    /// </summary>
    public string VersionName { get; set; }

    /// <summary>
    /// 获取或设置工作项的开始时间。
    /// </summary>
    public DateTime? StartAt { get; set; }

    /// <summary>
    /// 获取或设置工作项的结束时间。
    /// </summary>
    public DateTime? EndAt { get; set; }

    /// <summary>
    /// 获取或设置工作项的完成时间。
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// 获取或设置所属产品的名称。
    /// </summary>
    public string ProductName { get; set; }

    /// <summary>
    /// 获取或设置缺陷的复现版本号。
    /// </summary>
    public string ReproduceVersion { get; set; }

    /// <summary>
    /// 获取或设置缺陷的复现概率。
    /// </summary>
    public string ReproduceProbability { get; set; }

    /// <summary>
    /// 获取或设置缺陷的类别。
    /// </summary>
    public string DefectCategory { get; set; }

    /// <summary>
    /// 获取或设置工作项的标签名称列表。
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 获取或设置工作项示意图的 HTML 内容。
    /// </summary>
    public string SketchHtml { get; set; }

    /// <summary>
    /// 获取或设置工作项描述的 HTML 内容。
    /// </summary>
    public string DescriptionHtml { get; set; }

    /// <summary>
    /// 获取或设置工作项的预期结果。
    /// </summary>
    public string ExpectedResult { get; set; }

    /// <summary>
    /// 获取或设置工作项的评论列表。
    /// </summary>
    public List<WorkItemComment> Comments { get; set; } = new();

    /// <summary>
    /// 获取或设置工作项的扩展属性字典。
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取或设置用于公开访问图片的令牌。
    /// </summary>
    public string PublicImageToken { get; set; }

    /// <summary>
    /// 获取或设置子工作项的数量。
    /// </summary>
    public int ChildrenCount { get; set; }
}
