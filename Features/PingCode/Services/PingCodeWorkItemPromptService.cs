using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PackageManager.Features.PingCode.Models;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Features.PingCode.Services;

public class PingCodeWorkItemPromptService
{
    private static readonly Regex AnchorRegex = new Regex("<a\\b[^>]*?href\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new Regex("https?://[^\\s<>'\"，。；、）)\\]]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new Regex("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhiteSpaceRegex = new Regex("\\s+", RegexOptions.Compiled);

    public PingCodeAiPromptRequest BuildRequest(WorkItemDetails details)
    {
        if (details == null)
        {
            throw new ArgumentNullException(nameof(details));
        }

        var links = ExtractLinks(details);
        var isFix = IsDefect(details.Type) || IsDefect(details.DefectCategory);
        var actionKind = isFix ? "修复" : "实现";
        var prompt = isFix
            ? BuildFixPrompt(details, links)
            : BuildImplementPrompt(details, links);

        return new PingCodeAiPromptRequest
        {
            WorkItemId = details.Id,
            Identifier = details.Identifier,
            Title = details.Title,
            WorkItemType = details.Type,
            ActionKind = actionKind,
            InitialPrompt = prompt,
            Links = links,
        };
    }

    public bool IsFixWorkItem(WorkItemDetails details)
    {
        return details != null && (IsDefect(details.Type) || IsDefect(details.DefectCategory));
    }

    private static string BuildImplementPrompt(WorkItemDetails details, List<PingCodePromptLink> links)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你正在当前仓库实现一个 PingCode 用户故事/需求/任务。");
        sb.AppendLine();
        AppendBasicInfo(sb, details);
        AppendSection(sb, "业务目标与描述", ToPlainText(details.DescriptionHtml));
        AppendSection(sb, "示意图/补充说明", ToPlainText(details.SketchHtml));
        AppendProperties(sb, details.Properties);
        AppendComments(sb, details.Comments);
        AppendLinks(sb, links);
        AppendPlanModeAndEvidenceRules(sb);
        sb.AppendLine("## 执行要求");
        sb.AppendLine("- 先阅读当前仓库，定位相关模块和既有实现模式。");
        sb.AppendLine("- 优先阅读工作项中列出的方案、设计、接口等参考链接；如果链接需要登录或无法访问，请明确告知并基于已有信息继续。 ");
        sb.AppendLine("- 复用现有架构、组件和交互模式，不做无关重构。");
        sb.AppendLine("- 按工作项描述、验收标准和补充说明逐项实现。");
        sb.AppendLine("- 注意不要覆盖用户已有未提交改动；修改前先检查工作区状态。");
        sb.AppendLine("- 实现后运行必要构建、测试或 UI 验证；如果无法运行，请说明原因。");
        sb.AppendLine("- 最后用中文汇报修改文件、实现内容、验证结果，以及验收标准覆盖情况。");
        return sb.ToString();
    }

    private static string BuildFixPrompt(WorkItemDetails details, List<PingCodePromptLink> links)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你正在当前仓库修复一个 PingCode 缺陷/Bug。");
        sb.AppendLine();
        AppendBasicInfo(sb, details);
        AppendSection(sb, "问题描述", ToPlainText(details.DescriptionHtml));
        AppendSection(sb, "期望结果", details.ExpectedResult);
        AppendSection(sb, "示意图/附件说明", ToPlainText(details.SketchHtml));
        AppendProperties(sb, details.Properties);
        AppendComments(sb, details.Comments);
        AppendLinks(sb, links);
        AppendPlanModeAndEvidenceRules(sb);
        sb.AppendLine("## 执行要求");
        sb.AppendLine("- 先根据问题描述、复现步骤、实际结果、期望结果和相关链接定位根因。");
        sb.AppendLine("- 优先查看工作项中提供的日志、截图、复现页面或详细说明链接；如果链接需要登录或无法访问，请明确告知并基于已有信息继续。");
        sb.AppendLine("- 最小化修改，不做无关需求扩展或大范围重构。");
        sb.AppendLine("- 注意不要覆盖用户已有未提交改动；修改前先检查工作区状态。");
        sb.AppendLine("- 修复后运行针对性验证；如果无法运行，请说明原因。");
        sb.AppendLine("- 最后用中文汇报根因、修复点、影响范围、修改文件和验证结果。");
        return sb.ToString();
    }

    private static void AppendPlanModeAndEvidenceRules(StringBuilder sb)
    {
        sb.AppendLine("## AI 执行协议");
        sb.AppendLine("- 默认先进入方案阶段：先阅读代码、梳理证据、输出实现/修复方案，等待用户确认后再改代码。未经用户确认，不要直接编辑文件。");
        sb.AppendLine("- 不要猜测原因、接口、行为或修复方式；所有判断必须来自代码、日志、工作项描述、复现结果或用户提供的信息。");
        sb.AppendLine("- 除非有十足把握能从代码中确认根因并闭环，否则不认为仅靠代码阅读就能定位问题；应加入调试日志等待用户复现后精准确认。");
        sb.AppendLine("- 优先查找并复用当前仓库中继承 DesktopDebugLoggerBase 的调试日志类。");
        sb.AppendLine("- 如果没有现成可用的 DesktopDebugLoggerBase 派生日志类，就新增一个继承 DesktopDebugLoggerBase 的调试日志类。");
        sb.AppendLine("- 缺证据时先加最小必要调试日志，日志应输出到桌面，等待用户复现并提供日志后，再基于日志精准分析和修复。");
        sb.AppendLine("- 加日志时只记录定位所需的关键上下文，不要记录敏感信息，不要引入无关重构。");
        sb.AppendLine("- 工作项中的图片已下载到仓库 .pm-ai/images/ 目录，请直接读取这些本地图片文件来理解截图、示意图等视觉信息。");
        sb.AppendLine();
    }

    private static void AppendBasicInfo(StringBuilder sb, WorkItemDetails details)
    {
        sb.AppendLine("## 工作项信息");
        AppendLine(sb, "编号", details.Identifier);
        AppendLine(sb, "标题", details.Title);
        AppendLine(sb, "类型", details.Type);
        AppendLine(sb, "状态", details.StateName);
        AppendLine(sb, "优先级", details.PriorityName);
        AppendLine(sb, "严重程度", details.SeverityName);
        AppendLine(sb, "故事点", details.StoryPoints > 0 ? details.StoryPoints.ToString("0.##") : null);
        AppendLine(sb, "指派人", details.AssigneeName);
        AppendLine(sb, "产品", details.ProductName);
        AppendLine(sb, "版本", details.VersionName);
        AppendLine(sb, "复现版本", details.ReproduceVersion);
        AppendLine(sb, "复现概率", details.ReproduceProbability);
        AppendLine(sb, "缺陷类别", details.DefectCategory);
        AppendLine(sb, "Web 地址", details.HtmlUrl);
        if (details.Tags?.Count > 0)
        {
            AppendLine(sb, "标签", string.Join("、", details.Tags.Where(x => !string.IsNullOrWhiteSpace(x))));
        }

        if (!string.IsNullOrWhiteSpace(details.ParentIdentifier) || !string.IsNullOrWhiteSpace(details.ParentTitle))
        {
            AppendLine(sb, "父工作项", $"{details.ParentIdentifier} {details.ParentTitle}".Trim());
        }

        sb.AppendLine();
    }

    private static void AppendProperties(StringBuilder sb, Dictionary<string, string> properties)
    {
        var values = properties?
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToList();
        if (values == null || values.Count == 0)
        {
            return;
        }

        sb.AppendLine("## 扩展字段");
        foreach (var kv in values)
        {
            AppendLine(sb, kv.Key, ToPlainText(kv.Value));
        }

        sb.AppendLine();
    }

    private static void AppendComments(StringBuilder sb, List<WorkItemComment> comments)
    {
        var items = comments?
            .Where(c => !string.IsNullOrWhiteSpace(c?.ContentHtml) || !string.IsNullOrWhiteSpace(c?.RepliedContentHtml))
            .OrderBy(c => c.CreatedAt ?? DateTime.MinValue)
            .ToList();
        if (items == null || items.Count == 0)
        {
            return;
        }

        sb.AppendLine("## 评论补充");
        foreach (var comment in items)
        {
            var author = string.IsNullOrWhiteSpace(comment.AuthorName) ? "未知用户" : comment.AuthorName;
            var time = comment.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "未知时间";
            var text = ToPlainText(comment.ContentHtml);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine($"- {author}（{time}）：{text}");
            }
        }

        sb.AppendLine();
    }

    private static void AppendLinks(StringBuilder sb, List<PingCodePromptLink> links)
    {
        if (links == null || links.Count == 0)
        {
            return;
        }

        var priority = links.Where(x => x.Category == "必须优先阅读的参考资料").ToList();
        var assists = links.Where(x => x.Category != "必须优先阅读的参考资料").ToList();
        if (priority.Count > 0)
        {
            sb.AppendLine("## 必须优先阅读的参考资料");
            foreach (var link in priority)
            {
                sb.AppendLine($"- {link.Url}（{link.Context}）");
            }

            sb.AppendLine();
        }

        if (assists.Count > 0)
        {
            sb.AppendLine("## 辅助排查/参考链接");
            foreach (var link in assists)
            {
                sb.AppendLine($"- {link.Url}（{link.Context}）");
            }

            sb.AppendLine();
        }
    }

    private static void AppendSection(StringBuilder sb, string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        sb.AppendLine($"## {title}");
        sb.AppendLine(content.Trim());
        sb.AppendLine();
    }

    private static void AppendLine(StringBuilder sb, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.AppendLine($"- {label}：{value.Trim()}");
        }
    }

    private static List<PingCodePromptLink> ExtractLinks(WorkItemDetails details)
    {
        var result = new List<PingCodePromptLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLinks(result, seen, details.HtmlUrl, "工作项 Web 地址");
        AddLinks(result, seen, details.DescriptionHtml, "工作项描述");
        AddLinks(result, seen, details.SketchHtml, "示意图/附件说明");
        if (details.Properties != null)
        {
            foreach (var kv in details.Properties)
            {
                AddLinks(result, seen, kv.Value, string.IsNullOrWhiteSpace(kv.Key) ? "扩展字段" : kv.Key);
            }
        }

        if (details.Comments != null)
        {
            foreach (var comment in details.Comments)
            {
                var author = string.IsNullOrWhiteSpace(comment.AuthorName) ? "评论" : comment.AuthorName;
                AddLinks(result, seen, comment.ContentHtml, author);
                AddLinks(result, seen, comment.RepliedContentHtml, $"回复 {comment.RepliedAuthorName}".Trim());
            }
        }

        return result;
    }

    private static void AddLinks(List<PingCodePromptLink> result, HashSet<string> seen, string source, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        foreach (Match match in AnchorRegex.Matches(source))
        {
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value ?? "");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            AddLink(result, seen, url, BuildContext(sourceName, ToPlainText(match.Groups["text"].Value), source, match.Index));
        }

        foreach (Match match in UrlRegex.Matches(source))
        {
            AddLink(result, seen, WebUtility.HtmlDecode(match.Value), BuildContext(sourceName, null, source, match.Index));
        }
    }

    private static void AddLink(List<PingCodePromptLink> result, HashSet<string> seen, string url, string context)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
        {
            return;
        }

        result.Add(new PingCodePromptLink
        {
            Url = url,
            Context = context,
            Category = IsPriorityReference(url, context) ? "必须优先阅读的参考资料" : "辅助排查资料",
        });
    }

    private static string BuildContext(string sourceName, string anchorText, string source, int index)
    {
        var plain = ToPlainText(source);
        var snippet = string.Empty;
        if (!string.IsNullOrWhiteSpace(plain))
        {
            snippet = plain.Length <= 120 ? plain : plain.Substring(0, 120);
        }

        if (!string.IsNullOrWhiteSpace(anchorText))
        {
            snippet = string.IsNullOrWhiteSpace(snippet) ? anchorText : $"{anchorText}；{snippet}";
        }

        return string.IsNullOrWhiteSpace(snippet) ? sourceName : $"{sourceName}：{snippet}";
    }

    private static bool IsPriorityReference(string url, string context)
    {
        var text = $"{url} {context}".ToLowerInvariant();
        return text.Contains("方案") || text.Contains("设计") || text.Contains("接口") || text.Contains("文档") || text.Contains("doc") || text.Contains("wiki") || text.Contains("需求");
    }

    private static bool IsDefect(string value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        return text.Contains("bug") || text.Contains("缺陷") || text.Contains("故障") || text.Contains("defect") || text.Contains("issue");
    }

    private static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = html.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
        text = Regex.Replace(text, "</p>|</div>|</li>|</tr>|</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<li[^>]*>", "- ", RegexOptions.IgnoreCase);
        text = TagRegex.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = WhiteSpaceRegex.Replace(text, " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
