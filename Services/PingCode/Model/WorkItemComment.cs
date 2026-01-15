using System;

namespace PackageManager.Services.PingCode.Model;

public class WorkItemComment
{
    public string AuthorName { get; set; }

    public string AuthorAvatar { get; set; }

    public string ContentHtml { get; set; }

    public DateTime? CreatedAt { get; set; }
}