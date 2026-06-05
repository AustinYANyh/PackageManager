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

    public PingCodeAiPromptRequest BuildDecomposeRequest(WorkItemDetails details)
    {
        if (details == null)
        {
            throw new ArgumentNullException(nameof(details));
        }

        var links = ExtractLinks(details);
        var prompt = BuildDecomposePrompt(details, links);

        return new PingCodeAiPromptRequest
        {
            WorkItemId = details.Id,
            Identifier = details.Identifier,
            Title = details.Title,
            WorkItemType = details.Type,
            ActionKind = "拆解",
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
        sb.AppendLine("- 主动访问工作项中列出的内网链接（方案/原型/文档），获取完整设计信息后再动手。");
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
        sb.AppendLine("- 先根据问题描述、复现步骤、实际结果、期望结果定位根因。");
        sb.AppendLine("- 主动访问工作项中列出的内网链接（日志、截图、复现页面等），获取详细信息后再分析。");
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
        sb.AppendLine("### 链接访问规则");
        sb.AppendLine("**重要：不要用 WebFetch 或 curl 访问网页链接**——它们只能拿到 SPA 空壳 HTML，拿不到实际内容。");
        sb.AppendLine("访问任何网页链接时，统一使用 headless Edge 渲染后获取完整 DOM：");
        sb.AppendLine("```bash");
        sb.AppendLine("powershell.exe -NoProfile -Command \"& (Get-Command msedge).Source --headless --disable-gpu --dump-dom '<URL>'\"");
        sb.AppendLine("```");
        sb.AppendLine("- **内网链接**（192.168.x.x、10.x.x.x 等内网地址）：无需认证，直接访问。务必主动访问获取方案、设计、原型等完整信息。");
        sb.AppendLine("- **PingCode 链接**（*.pingcode.com）：需要在 URL 后追加 access_token 参数认证（见 prompt 末尾的凭证信息）。");
        sb.AppendLine("- **内网 Axure 原型**：start.html 是空壳框架。正确做法：先访问同目录下 `data/document.js` 提取各子页面的 `url` 字段，然后逐个访问子页面 HTML。");
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
                sb.AppendLine($"- {FormatLinkWithAccessLabel(link)}");
            }

            sb.AppendLine();
        }

        if (assists.Count > 0)
        {
            sb.AppendLine("## 辅助排查/参考链接");
            foreach (var link in assists)
            {
                sb.AppendLine($"- {FormatLinkWithAccessLabel(link)}");
            }

            sb.AppendLine();
        }
    }

    private static string FormatLinkWithAccessLabel(PingCodePromptLink link)
    {
        var label = IsPingCodeUrl(link.Url) ? "需追加 access_token" : "可直接访问";
        return $"[{label}] {link.Url}（{link.Context}）";
    }

    private static bool IsPingCodeUrl(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return host.EndsWith("pingcode.com");
        }
        catch
        {
            return false;
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

    private static string BuildDecomposePrompt(WorkItemDetails details, List<PingCodePromptLink> links)
    {
        var sb = new StringBuilder();
        var isFix = IsDefect(details.Type) || IsDefect(details.DefectCategory);
        sb.AppendLine(isFix
            ? "你正在分析一个 PingCode 缺陷/Bug，需要将其拆解为多个可独立实现的小任务（子工作项），并通过 PingCode API 创建。"
            : "你正在分析一个 PingCode 用户故事/需求，需要将其拆解为多个可独立实现的小任务（子工作项），并通过 PingCode API 创建。");
        sb.AppendLine();
        AppendBasicInfo(sb, details);
        AppendSection(sb, isFix ? "问题描述" : "业务目标与描述", ToPlainText(details.DescriptionHtml));
        AppendSection(sb, "示意图/补充说明", ToPlainText(details.SketchHtml));
        if (isFix)
        {
            AppendSection(sb, "期望结果", details.ExpectedResult);
        }

        AppendProperties(sb, details.Properties);
        AppendComments(sb, details.Comments);
        AppendLinks(sb, links);
        AppendPlanModeAndEvidenceRules(sb);
        AppendDecompositionInstructions(sb);
        AppendPingCodeCreateApiInstructions(sb, details);
        return sb.ToString();
    }

    private static void AppendDecompositionInstructions(StringBuilder sb)
    {
        sb.AppendLine("## 拆解任务要求");
        sb.AppendLine();
        sb.AppendLine("你的任务是将上述工作项拆解为多个小任务，并在 PingCode 中创建对应的子工作项。");
        sb.AppendLine();
        sb.AppendLine("### 分析步骤");
        sb.AppendLine("1. 仔细阅读工作项的业务目标、描述、验收标准和补充说明。");
        sb.AppendLine("2. 如果有内网链接（方案/原型/文档），先主动访问获取完整设计信息。");
        sb.AppendLine("3. 阅读当前仓库代码，理解相关模块的架构和既有模式。");
        sb.AppendLine("4. 识别可独立实现的功能切片。");
        sb.AppendLine();
        sb.AppendLine("### 拆解原则");
        sb.AppendLine("- 每个子任务应该足够小（建议 1-3 个故事点），可以单独开发和测试。");
        sb.AppendLine("- 子任务之间尽量解耦，允许并行开发。");
        sb.AppendLine("- 每个子任务必须有明确的标题、描述和验收标准。");
        sb.AppendLine("- 保留父工作项的上下文信息，每个子任务的描述应该自包含，让后续 AI 实现时能理解完整背景。");
        sb.AppendLine("- **子任务标题格式**：`AI—[模块名] 具体任务描述`（必须以 `AI—` 开头）。");
        sb.AppendLine("- 子任务描述应包含：实现范围、建议故事点（你估算的工作量）、验收标准、相关文件或模块路径（如果已知）。");
        sb.AppendLine("- **注意**：创建子任务时故事点统一填 0.1（占位值），你估算的建议故事点写在描述正文中「实现范围」和「验收标准」之间。");
        sb.AppendLine();
        sb.AppendLine("### 输出要求");
        sb.AppendLine("在调用 API 创建之前，先输出拆解方案供用户确认：");
        sb.AppendLine();
        sb.AppendLine("| 序号 | 标题 | 描述摘要 | 建议故事点 | 依赖 |");
        sb.AppendLine("|------|------|----------|------------|------|");
        sb.AppendLine("| 1    | AI—[模块] ... | ... | 2 | 无 |");
        sb.AppendLine();
        sb.AppendLine("**必须等待用户确认拆解方案后，再执行 API 调用创建子工作项。**");
        sb.AppendLine();
    }

    private static void AppendPingCodeCreateApiInstructions(StringBuilder sb, WorkItemDetails details)
    {
        sb.AppendLine("## PingCode 子工作项创建指南");
        sb.AppendLine();
        sb.AppendLine("用户确认拆解方案后，按以下步骤创建子工作项。");
        sb.AppendLine();
        sb.AppendLine("### 重要：必须先查询 type_id 和 assignee_id");
        sb.AppendLine();
        sb.AppendLine("PingCode API **不接受** `type: \"task\"` 字符串，必须使用项目中「任务」类型的 `type_id`。");
        sb.AppendLine("同时，所有子任务需要指派给 **闫云皓**，需要查询其 `user_id`。");
        sb.AppendLine();
        sb.AppendLine("**步骤 1：查询「任务」类型的 type_id**");
        sb.AppendLine("```powershell");
        sb.AppendLine("$token = '<access_token>'");
        sb.AppendLine("$headers = @{ 'Authorization' = \"Bearer $token\" }");
        sb.AppendLine($"$urls = @('https://open.pingcode.com/v1/project/work_items/types?project_id={details.ProjectId}',");
        sb.AppendLine($"          'https://open.pingcode.com/v1/project/work_item_types?project_id={details.ProjectId}')");
        sb.AppendLine("foreach ($u in $urls) {");
        sb.AppendLine("    $r = Invoke-WebRequest -Uri $u -Headers $headers -SkipHttpErrorCheck");
        sb.AppendLine("    if ($r.StatusCode -eq 200) { ($r.Content | ConvertTo-Json -Depth 5); break }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine("从返回的数组中找到 `name` 包含「任务」或 `display_name` 为「task」的条目，取其 `id` 字段作为 `type_id`。");
        sb.AppendLine();
        sb.AppendLine("**步骤 2：查询闫云皓的 user_id**");
        sb.AppendLine("```powershell");
        sb.AppendLine($"$r = Invoke-WebRequest -Uri 'https://open.pingcode.com/v1/project/projects/{details.ProjectId}/members?page_size=100' -Headers $headers -SkipHttpErrorCheck");
        sb.AppendLine("($r.Content | ConvertTo-Json -Depth 5)");
        sb.AppendLine("```");
        sb.AppendLine("从返回的成员列表中找到 `display_name` 或 `name` 为「闫云皓」的条目，取其 `user.id` 或 `id` 字段。");
        sb.AppendLine();
        sb.AppendLine("### 步骤 3：创建子工作项");
        sb.AppendLine("```");
        sb.AppendLine("POST https://open.pingcode.com/v1/project/work_items");
        sb.AppendLine("Content-Type: application/json");
        sb.AppendLine("Authorization: Bearer <access_token>（见 prompt 末尾的 PingCode API 认证凭证）");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("请求体（**使用查到的 type_id 和 assignee_id**）：");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine($"  \"project_id\": \"{details.ProjectId}\",");
        sb.AppendLine("  \"title\": \"AI—[模块名] 具体任务描述\",");
        sb.AppendLine("  \"type_id\": \"<步骤1查到的type_id>\",");
        sb.AppendLine($"  \"parent_id\": \"{details.Id}\",");
        sb.AppendLine("  \"assignee_id\": \"<步骤2查到的闫云皓user_id>\",");
        sb.AppendLine("  \"description\": \"<p><b>实现范围：</b>...</p><p><b>建议故事点：</b>2</p><p><b>验收标准：</b>...</p>\",");
        sb.AppendLine("  \"story_points\": 0.1");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("PowerShell 调用示例：");
        sb.AppendLine("```powershell");
        sb.AppendLine("$headers = @{");
        sb.AppendLine("    'Content-Type' = 'application/json'");
        sb.AppendLine("    'Authorization' = \"Bearer $token\"");
        sb.AppendLine("}");
        sb.AppendLine("$body = @{");
        sb.AppendLine($"    project_id = '{details.ProjectId}'");
        sb.AppendLine("    title = 'AI—[模块名] 具体任务描述'");
        sb.AppendLine("    type_id = $typeId");
        sb.AppendLine($"    parent_id = '{details.Id}'");
        sb.AppendLine("    assignee_id = $assigneeId");
        sb.AppendLine("    description = '<p><b>实现范围：</b>...</p><p><b>建议故事点：</b>2</p><p><b>验收标准：</b>...</p>'");
        sb.AppendLine("    story_points = 0.1");
        sb.AppendLine("} | ConvertTo-Json -Compress");
        sb.AppendLine("$resp = Invoke-WebRequest -Uri 'https://open.pingcode.com/v1/project/work_items' -Method Post -Headers $headers -Body $body -SkipHttpErrorCheck");
        sb.AppendLine("$resp.Content | ConvertTo-Json -Depth 10");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### 执行流程总结");
        sb.AppendLine("1. 查询 type_id（「任务」类型）和 assignee_id（闫云皓）。");
        sb.AppendLine("2. 用户确认拆解方案后，逐个调用 POST 创建子工作项（故事点统一 0.1，标题以 `AI—` 开头）。");
        sb.AppendLine("3. 从每个响应中提取 `id` 和 `identifier`（如 PROJ-456）。");
        sb.AppendLine("4. 最后用中文汇报创建结果（成功/失败、各子任务编号）。");
        sb.AppendLine();
        sb.AppendLine("### 容错说明");
        sb.AppendLine("- 如果 POST 返回 400，检查响应体中的错误信息，可能是字段名不正确。");
        sb.AppendLine("- 如果某个子任务创建失败，继续创建其余子任务，最后汇总失败项。");
        sb.AppendLine("- 如果 API 返回 401，说明 token 已过期，提示用户重新执行。");
        sb.AppendLine("- 使用 `Invoke-WebRequest` 搭配 `-SkipHttpErrorCheck` 来获取完整响应体（包括错误时的响应），以便调试。");
        sb.AppendLine();
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
