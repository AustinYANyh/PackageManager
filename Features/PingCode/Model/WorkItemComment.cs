using System;

namespace PackageManager.Services.PingCode.Model;

/// <summary>
/// 表示工作项的评论信息。
/// </summary>
public class WorkItemComment
{
    /// <summary>
    /// 获取或设置评论的唯一标识。
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置评论作者的名称。
    /// </summary>
    public string AuthorName { get; set; }

    /// <summary>
    /// 获取或设置评论作者的头像 URL。
    /// </summary>
    public string AuthorAvatar { get; set; }

    /// <summary>
    /// 获取或设置评论的 HTML 内容。
    /// </summary>
    public string ContentHtml { get; set; }

    /// <summary>
    /// 获取或设置评论的创建时间。
    /// </summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// 获取或设置被回复评论的作者名称。
    /// </summary>
    public string RepliedAuthorName { get; set; }

    /// <summary>
    /// 获取或设置被回复评论的 HTML 内容。
    /// </summary>
    public string RepliedContentHtml { get; set; }

    /// <summary>
    /// 获取或设置被回复评论的唯一标识。
    /// </summary>
    public string RepliedCommentId { get; set; }
}
