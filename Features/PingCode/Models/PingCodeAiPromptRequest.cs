using System.Collections.Generic;

namespace PackageManager.Features.PingCode.Models;

public class PingCodeAiPromptRequest
{
    public string WorkItemId { get; set; }

    public string Identifier { get; set; }

    public string Title { get; set; }

    public string WorkItemType { get; set; }

    public string ActionKind { get; set; }

    public string InitialPrompt { get; set; }

    public List<PingCodePromptLink> Links { get; set; } = new();
}

public class PingCodePromptLink
{
    public string Url { get; set; }

    public string Context { get; set; }

    public string Category { get; set; }

    public string DisplayText => string.IsNullOrWhiteSpace(Context)
        ? $"{Category}\n{Url}"
        : $"{Category}\n{Url}\n{Context}";
}

public class DownloadedImage
{
    public string OriginalUrl { get; set; }

    public string LocalPath { get; set; }

    public string FileName { get; set; }

    public string SourceContext { get; set; }

    public bool Success { get; set; }

    public string Error { get; set; }

    public string DisplayText => Success
        ? $"{FileName}（{SourceContext}）"
        : $"{FileName}（{SourceContext}）- 失败：{Error}";
}

public class DownloadedIntranetResource
{
    public string OriginalUrl { get; set; }

    public string LocalPath { get; set; }

    public string FileName { get; set; }

    public string SourceContext { get; set; }

    public string Title { get; set; }

    public string ResourceKind { get; set; }

    public bool Success { get; set; }

    public string Error { get; set; }

    public bool IsImage => string.Equals(ResourceKind, "Image", System.StringComparison.OrdinalIgnoreCase);

    public string DisplayText => Success
        ? $"{FileName}（{ResourceKind}，{SourceContext}）"
        : $"{FileName}（{ResourceKind}，{SourceContext}）- 失败：{Error}";
}
