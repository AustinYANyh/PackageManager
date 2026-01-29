using System;

namespace PackageManager.Services.PingCode.Model;

public class WorkItemComment
{
    public string Id { get; set; }

    public string AuthorName { get; set; }

    public string AuthorAvatar { get; set; }

    public string ContentHtml { get; set; }

    public DateTime? CreatedAt { get; set; }

    public string RepliedAuthorName { get; set; }

    public string RepliedContentHtml { get; set; }

    public string RepliedCommentId { get; set; }
}
