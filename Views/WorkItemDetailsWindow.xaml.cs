using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Linq;
using Microsoft.Web.WebView2.Core;
using PackageManager.Services;

namespace PackageManager.Views
{
    public partial class WorkItemDetailsWindow : Window, INotifyPropertyChanged
    {
        public PingCodeApiService.WorkItemDetails Details { get; }
        private readonly PingCodeApiService _api;
        private string _accessToken;
        private static readonly Regex ImgTagRegex = new Regex("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnchorTagRegex = new Regex("<a\\b[^>]*>([\\s\\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Dictionary<string, string> TemplateCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        
        public WorkItemDetailsWindow(PingCodeApiService.WorkItemDetails details, PingCodeApiService api)
        {
            Details = details ?? new PingCodeApiService.WorkItemDetails();
            _api = api ?? new PingCodeApiService();
            InitializeComponent();
            DataContext = this;
            Loaded += async (s, e) =>
            {
                try
                {
                    ShowLoading(true);
                    InferPublicImageToken();
                    
                    await DetailsWeb.EnsureCoreWebView2Async();
                    var core = DetailsWeb.CoreWebView2;
                    core.NavigationCompleted += (sender, args) =>
                    {
                        try { ShowLoading(false); } catch { }
                    };
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.XmlHttpRequest);
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                    core.WebResourceRequested += async (sender, args) =>
                    {
                        try
                        {
                            var uri = new Uri(args.Request.Uri);
                            var host = (uri.Host ?? "").ToLowerInvariant();
                            var path = (uri.AbsolutePath ?? "").ToLowerInvariant();
                            var isAtlasPublic = host.Contains("atlas.pingcode.com") || path.Contains("/files/public/");
                            if (isAtlasPublic && args.ResourceContext == CoreWebView2WebResourceContext.Image)
                            {
                                try
                                {
                                    args.Request.Headers.SetHeader("Referer", "https://pingcode.com/");
                                    args.Request.Headers.SetHeader("Origin", "https://pingcode.com");
                                    args.Request.Headers.SetHeader("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
                                }
                                catch
                                {
                                }
                            }
                            var isPingCodeDomain = host.Equals("pingcode.com") || host.EndsWith(".pingcode.com");
                            if (isPingCodeDomain)
                            {
                                var tk = await _api.GetAccessTokenAsync();
                                if (!string.IsNullOrWhiteSpace(tk))
                                {
                                    args.Request.Headers.SetHeader("Authorization", $"Bearer {tk}");
                                }
                            }
                        }
                        catch
                        {
                        }
                    };
                    
                    var loadingHtml = BuildLoadingHtml();
                    DetailsWeb.CoreWebView2.NavigateToString(loadingHtml);
                    
                    _accessToken = await _api.GetAccessTokenAsync();
                    var html = await Task.Run(() => BuildHtml());
                    DetailsWeb.CoreWebView2.NavigateToString(html);
                }
                catch
                {
                    try { ShowLoading(false); } catch { }
                }
            };
        }
        
        private string HtmlEscape(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        
        private static string BuildLoadingHtml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"/>");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
            sb.AppendLine("<style>html,body{height:100%}body{margin:0;background:#fff;font-family:'Segoe UI','Microsoft YaHei',Arial,sans-serif;color:#111827;height:100%}");
            sb.AppendLine(".center{display:flex;align-items:center;justify-content:center;height:100%}");
            sb.AppendLine(".card{border:1px solid #E5E7EB;border-radius:8px;padding:16px;background:#fff;box-shadow:0 1px 2px rgba(0,0,0,.04)}");
            sb.AppendLine(".spinner{width:16px;height:16px;border:2px solid #93C5FD;border-top-color:#2563EB;border-radius:50%;display:inline-block;animation:spin 0.8s linear infinite;margin-right:8px}");
            sb.AppendLine("@keyframes spin{to{transform:rotate(360deg)}}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<div class=\"center\"><div class=\"card\"><span class=\"spinner\"></span><span>正在加载详情...</span></div></div>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }
        
        private string BuildHtml()
        {
            try
            {
                var tplRes = ReadEmbeddedTemplate("workitem-details.html");
                TryEnsureLatestTemplateExtracted(tplRes, "workitem-details.html");
                if (!string.IsNullOrWhiteSpace(tplRes))
                {
                    return BuildHtmlFromTemplate(tplRes);
                }
                var path = GetTemplatePath();
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    var tpl = ReadFileCached(path);
                    return BuildHtmlFromTemplate(tpl);
                }
            }
            catch
            {
            }
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"/><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
            sb.AppendLine("<style>");
            sb.AppendLine("html,body{height:100%}");
            sb.AppendLine("body{margin:0;background:#fff;font-family:'Segoe UI','Microsoft YaHei',Arial,sans-serif;color:#111827;height:100%}");
            sb.AppendLine(".wrap{padding:0;height:100%}");
            sb.AppendLine(".drawer{width:100%;max-width:100%;margin:0;border-radius:0;box-shadow:none;height:100%}");
            sb.AppendLine(".header{padding:16px 24px;border-bottom:1px solid #f0f0f0;background:#fff;display:flex;align-items:center;gap:10px}");
            sb.AppendLine(".id{color:#6B7280;font-weight:600}");
            sb.AppendLine(".title{font-size:18px;font-weight:700}");
            sb.AppendLine(".quick{display:grid;grid-template-columns:repeat(4,1fr);gap:16px;padding:8px 24px;border-bottom:1px solid #f0f0f0;background:#fff}");
            sb.AppendLine(".quick .item{display:flex;flex-direction:column;gap:6px}");
            sb.AppendLine(".quick .label{color:#6B7280}");
            sb.AppendLine(".quick .value{font-weight:600}");
            sb.AppendLine(".base-info{display:grid;grid-template-columns:repeat(4,1fr);gap:16px;padding-top:4px}");
            sb.AppendLine(".base-info .item{display:flex;flex-direction:column;gap:6px}");
            sb.AppendLine(".base-info .label{color:#6B7280}");
            sb.AppendLine(".base-info .value{font-weight:600}");
            sb.AppendLine(".layout{display:grid;grid-template-columns:1fr;gap:16px;padding:16px 24px;background:#fff}");
            sb.AppendLine(".section-title{font-size:16px;font-weight:700;margin:12px 0 8px}");
            sb.AppendLine(".desc-card,.sketch-card,.comments-card{border:1px solid #f0f0f0;border-radius:8px;padding:12px;background:#fff}");
            sb.AppendLine(".ant-descriptions-item-label{color:#6B7280}");
            sb.AppendLine(".tag{display:inline-block;margin:0 8px 8px 0}");
            sb.AppendLine(".ant-tag{display:inline-block;border:1px solid #D1D5DB;border-radius:999px;padding:2px 8px;background:#F9FAFB;color:#374151;font-size:12px}");
            sb.AppendLine(".ant-tag-pink{border-color:#FDA4AF;background:#FFE4E6;color:#9D174D}");
            sb.AppendLine(".comment-item{border-bottom:1px solid #f0f0f0;padding:12px 0}");
            sb.AppendLine(".comment-item:last-child{border-bottom:none}");
            sb.AppendLine(".comment-row{display:flex;align-items:flex-start;gap:10px}");
            sb.AppendLine(".comment-avatar{flex:0 0 24px}");
            sb.AppendLine(".comment-main{flex:1 1 auto}");
            sb.AppendLine(".comment-meta{color:#6B7280;margin-bottom:6px;font-size:12px}");
            sb.AppendLine(".comment-time{background:#F3F4F6;border:1px solid #E5E7EB;border-radius:999px;padding:2px 8px}");
            sb.AppendLine(".comment-avatar .ant-avatar{width:24px;height:24px;line-height:24px}");
            sb.AppendLine(".comment-avatar img{width:24px;height:24px;border-radius:50%;object-fit:cover}");
            sb.AppendLine(".comment-body img{max-width:300px;height:auto}");
            sb.AppendLine(".comment-attachment img{max-width:300px;height:auto}");
            sb.AppendLine(".badge{display:inline-block;border:1px solid #D1D5DB;border-radius:999px;padding:2px 8px;background:#F3F4F6;color:#374151}");
            sb.AppendLine(".state-badge{min-width:70px;display:inline-block;text-align:center}");
            sb.AppendLine(".state-badge.state-inprogress{background:#F59E0B;color:#fff;border-color:#FDBA74}");
            sb.AppendLine(".state-badge.state-testable{background:#3B82F6;color:#fff;border-color:#93C5FD}");
            sb.AppendLine(".state-badge.state-testing{background:#A855F7;color:#fff;border-color:#C4B5FD}");
            sb.AppendLine(".state-badge.state-done{background:#10B981;color:#fff;border-color:#6EE7B7}");
            sb.AppendLine(".state-badge.state-closed{background:#9CA3AF;color:#fff;border-color:#D1D5DB}");
            sb.AppendLine(".state-badge.state-pending{background:#E5E7EB;color:#374151;border-color:#D1D5DB}");
            sb.AppendLine(".comment-avatar .ant-avatar{width:24px;height:24px;line-height:24px}");
            sb.AppendLine(".comment-avatar img{width:24px;height:24px;border-radius:50%;object-fit:cover}");
            sb.AppendLine(".ant-comment-content-detail img,.desc-card img,.sketch-card img{max-width:100%;border-radius:8px;border:1px solid #E5E7EB}");
            sb.AppendLine(".ant-comment-content-detail pre, .ant-comment-content-detail code{background:#F7F7F9;border:1px solid #E5E7EB;border-radius:6px}");
            sb.AppendLine(".ant-comment-content-detail pre{padding:10px;overflow:auto}");
            sb.AppendLine(".ant-comment-content-detail code{padding:2px 4px}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<div class=\"wrap\">");
            sb.AppendLine("<div class=\"ant-drawer ant-drawer-open ant-drawer-right\">");
            sb.AppendLine("<div class=\"ant-drawer-content-wrapper drawer\"><div class=\"ant-drawer-content\"><div class=\"ant-drawer-wrapper-body\"><div class=\"ant-drawer-body\" style=\"padding:0;height:100%;overflow:auto\">");
            sb.AppendLine("<div class=\"header\"><span class=\"id\">" + HtmlEscape(Details.Identifier) + "</span><span class=\"title\">" + HtmlEscape(Details.Title) + "</span></div>");
            var stateType = (Details.StateType ?? "").Trim().ToLowerInvariant();
            var stateName = (Details.StateName ?? "").Trim().ToLowerInvariant();
            var stateCls = "state-pending";
            if (stateType.Contains("done") || stateName.Contains("完成")) stateCls = "state-done";
            else if (stateType.Contains("clos") || stateName.Contains("关闭") || stateName.Contains("拒绝")) stateCls = "state-closed";
            else if (stateName.Contains("可测试")) stateCls = "state-testable";
            else if (stateName.Contains("测试中")) stateCls = "state-testing";
            else if (stateType.Contains("progress") || stateType.Contains("in_progress") || stateName.Contains("进行中") || stateName.Contains("开发中") || stateName.Contains("处理中")) stateCls = "state-inprogress";
            var startText = FormatDate(Details.StartAt);
            var endText = FormatDate(Details.EndAt);
            sb.AppendLine("<div class=\"quick\">");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">负责人</div><div class=\"value\">{HtmlEscape(Details.AssigneeName)}</div></div>");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">状态</div><div class=\"value\"><span class=\"badge state-badge {stateCls}\">{HtmlEscape(Details.StateName)}</span></div></div>");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">开始时间</div><div class=\"value\">{startText}</div></div>");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">结束时间</div><div class=\"value\">{endText}</div></div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"layout\">");
            sb.AppendLine("<div>");
            sb.AppendLine("<div class=\"section-title\">基本信息</div>");
            var severityZh = MapSeverityText(Details.SeverityName);
            sb.AppendLine("<div class=\"base-info\">");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">优先级</div><div class=\"value\"><span class=\"badge\">{HtmlEscape(Details.PriorityName)}</span></div></div>");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">严重程度</div><div class=\"value\">{HtmlEscape(severityZh)}</div></div>");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">故事点</div><div class=\"value\">{(Math.Abs(Details.StoryPoints) < 0.000001 ? "-" : Details.StoryPoints.ToString("0.##"))}</div></div>");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">所属产品</div><div class=\"value\">{DashText(Details.ProductName)}</div></div>");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">缺陷类别</div><div class=\"value\">{DashText(Details.DefectCategory)}</div></div>");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">复现版本号</div><div class=\"value\">{DashText(Details.ReproduceVersion)}</div></div>");
            sb.AppendLine($"<div class=\"item\"><div class=\"label\">复现概率</div><div class=\"value\">{DashText(Details.ReproduceProbability)}</div></div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"section-title\">标签</div>");
            sb.AppendLine("<div>");
            foreach (var t in Details.Tags ?? new System.Collections.Generic.List<string>())
            {
                sb.AppendLine($"<span class=\"ant-tag tag ant-tag-pink\">{HtmlEscape(t)}</span>");
            }
            if ((Details.Tags?.Count ?? 0) == 0) sb.AppendLine("<span>-</span>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"section-title\">描述</div>");
            sb.AppendLine("<div class=\"desc-card\">");
            if (!string.IsNullOrWhiteSpace(Details.DescriptionHtml)) sb.AppendLine(NormalizeImages(Details.DescriptionHtml));
            else sb.AppendLine("<div>-</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"section-title\">示意图</div>");
            sb.AppendLine("<div class=\"sketch-card\">");
            if (!string.IsNullOrWhiteSpace(Details.SketchHtml)) sb.AppendLine(NormalizeImages(Details.SketchHtml));
            else sb.AppendLine("<div>-</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"section-title\">评论</div>");
            sb.AppendLine("<div class=\"comments-card\">");
            var commentsBlock = BuildCommentsHtml(Details.Comments);
            sb.AppendLine(commentsBlock);
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div></div></div></div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }
        
        private string BuildHtmlFromTemplate(string tpl)
        {
            var stateType = (Details.StateType ?? "").Trim().ToLowerInvariant();
            var stateNameRaw = (Details.StateName ?? "").Trim().ToLowerInvariant();
            var stateCls = "state-pending";
            if (stateType.Contains("done") || stateNameRaw.Contains("完成")) stateCls = "state-done";
            else if (stateType.Contains("clos") || stateNameRaw.Contains("关闭") || stateNameRaw.Contains("拒绝")) stateCls = "state-closed";
            else if (stateNameRaw.Contains("可测试")) stateCls = "state-testable";
            else if (stateNameRaw.Contains("测试中")) stateCls = "state-testing";
            else if (stateType.Contains("progress") || stateType.Contains("in_progress") || stateNameRaw.Contains("进行中") || stateNameRaw.Contains("开发中") || stateNameRaw.Contains("处理中")) stateCls = "state-inprogress";
            var startText = FormatDate(Details.StartAt);
            var endText = FormatDate(Details.EndAt);
            var severityZh = MapSeverityText(Details.SeverityName);
            var storyPointsText = Math.Abs(Details.StoryPoints) < 0.000001 ? "-" : Details.StoryPoints.ToString("0.##");
            var tagsHtml = BuildTagsHtml(Details.Tags);
            var descriptionHtml = string.IsNullOrWhiteSpace(Details.DescriptionHtml) ? "<div>-</div>" : NormalizeImages(Details.DescriptionHtml);
            var sketchHtml = string.IsNullOrWhiteSpace(Details.SketchHtml) ? "<div>-</div>" : NormalizeImages(Details.SketchHtml);
            var commentsHtml = BuildCommentsHtml(Details.Comments);
            var propertiesHtml = BuildPropertiesHtml(Details.Properties);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["{{Identifier}}"] = HtmlEscape(Details.Identifier),
                ["{{Title}}"] = HtmlEscape(Details.Title),
                ["{{AssigneeName}}"] = DashText(Details.AssigneeName),
                ["{{StateClass}}"] = stateCls,
                ["{{StateName}}"] = HtmlEscape(Details.StateName),
                ["{{StartAt}}"] = startText,
                ["{{EndAt}}"] = endText,
                ["{{PriorityName}}"] = HtmlEscape(Details.PriorityName),
                ["{{SeverityText}}"] = HtmlEscape(severityZh),
                ["{{StoryPoints}}"] = storyPointsText,
                ["{{ProductName}}"] = DashText(Details.ProductName),
                ["{{DefectCategory}}"] = DashText(Details.DefectCategory),
                ["{{ReproduceVersion}}"] = DashText(Details.ReproduceVersion),
                ["{{ReproduceProbability}}"] = DashText(Details.ReproduceProbability),
                ["{{TagsHtml}}"] = tagsHtml,
                ["{{DescriptionHtml}}"] = descriptionHtml,
                ["{{SketchHtml}}"] = sketchHtml,
                ["{{CommentsHtml}}"] = commentsHtml,
                ["{{PropertiesHtml}}"] = propertiesHtml
            };
            return ReplaceTokens(tpl, dict);
        }
        
        private static string ReplaceTokens(string tpl, Dictionary<string, string> dict)
        {
            var result = tpl;
            foreach (var kv in dict)
            {
                result = result.Replace(kv.Key, kv.Value ?? "");
            }
            return result;
        }
        
        private static string BuildTagsHtml(List<string> tags)
        {
            var list = tags ?? new List<string>();
            if (list.Count == 0) return "<span>-</span>";
            var sb = new StringBuilder();
            foreach (var t in list)
            {
                sb.Append($"<span class=\"ant-tag tag ant-tag-pink\">{System.Net.WebUtility.HtmlEncode(t ?? "")}</span>");
            }
            return sb.ToString();
        }
        
        private static string BuildPropertiesHtml(Dictionary<string, string> props)
        {
            var dict = props ?? new Dictionary<string, string>();
            var sb = new StringBuilder();
            foreach (var kv in dict)
            {
                sb.Append($"<tr class=\"ant-descriptions-row\"><td class=\"ant-descriptions-item-label\">{System.Net.WebUtility.HtmlEncode(kv.Key ?? "")}</td><td class=\"ant-descriptions-item-content\">{System.Net.WebUtility.HtmlEncode(kv.Value ?? "")}</td></tr>");
            }
            return sb.ToString();
        }
        
        private string BuildCommentsHtml(List<PingCodeApiService.WorkItemComment> comments)
        {
            var list = comments ?? new List<PingCodeApiService.WorkItemComment>();
            if (list.Count == 0) return "<div>-</div>";
            var sb = new StringBuilder();
            foreach (var c in list)
            {
                var tm = c.CreatedAt.HasValue ? c.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm") : "-";
                var nm = (c.AuthorName ?? "").Trim();
                var initial = string.IsNullOrWhiteSpace(nm) ? "-" : nm.Substring(0, Math.Min(1, nm.Length));
                var avatarHtml = string.IsNullOrWhiteSpace(c.AuthorAvatar)
                    ? $"<span class=\"ant-avatar ant-avatar-circle\"><span class=\"ant-avatar-string\">{HtmlEscape(initial)}</span></span>"
                    : $"<span class=\"ant-avatar ant-avatar-circle ant-avatar-image\"><img src=\"{HtmlEscape(c.AuthorAvatar)}\"/></span>";
                sb.Append("<div class=\"comment-item\">");
                sb.Append("<div class=\"comment-row\">");
                sb.Append($"<div class=\"comment-avatar\">{avatarHtml}</div>");
                sb.Append("<div class=\"comment-main\">");
                sb.Append($"<div class=\"comment-meta\"><span class=\"comment-time\">{tm}</span></div>");
                var content = string.IsNullOrWhiteSpace(c.ContentHtml) ? "-" : NormalizeImages(c.ContentHtml);
                sb.Append($"<div class=\"comment-body\">{content}</div>");
                sb.Append("</div></div>");
                sb.Append("</div>");
            }
            return sb.ToString();
        }
        
        private static string GetTemplatePath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var rel = Path.Combine("Views", "Templates", "workitem-details.html");
            var candidates = new List<string>();
            candidates.Add(Path.Combine(baseDir, rel));
            try
            {
                var dir = new DirectoryInfo(baseDir);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    var p = Path.Combine(dir.FullName, rel);
                    candidates.Add(p);
                    dir = dir.Parent;
                }
            }
            catch
            {
            }
            try
            {
                var cwd = Directory.GetCurrentDirectory();
                candidates.Add(Path.Combine(cwd, rel));
            }
            catch
            {
            }
            try
            {
                var asmDir = Path.GetDirectoryName(typeof(WorkItemDetailsWindow).Assembly.Location);
                if (!string.IsNullOrWhiteSpace(asmDir))
                {
                    candidates.Add(Path.Combine(asmDir, rel));
                }
            }
            catch
            {
            }
            foreach (var p in candidates)
            {
                if (File.Exists(p)) return p;
            }
            return Path.Combine(baseDir, rel);
        }
        
        private static string ReadEmbeddedTemplate(string resourceFileName)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var name = asm.GetManifestResourceNames()
                              .FirstOrDefault(n => n.EndsWith(resourceFileName, StringComparison.OrdinalIgnoreCase) ||
                                                   n.EndsWith($"Views.Templates.{resourceFileName}", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(name))
                {
                    using (var s = asm.GetManifestResourceStream(name))
                    using (var reader = new StreamReader(s, Encoding.UTF8, true))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch
            {
            }
            return null;
        }
        
        private void TryEnsureLatestTemplateExtracted(string embeddedText, string fileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(embeddedText) || string.IsNullOrWhiteSpace(fileName)) return;
                var data = new DataPersistenceService();
                var targetDir = Path.Combine(data.GetDataFolderPath(), "Views", "Templates");
                var targetPath = Path.Combine(targetDir, fileName);
                Directory.CreateDirectory(targetDir);
                var verEmbedded = ExtractTemplateVersion(embeddedText);
                var verLocal = ExtractTemplateVersionFromFile(targetPath);
                var need = string.IsNullOrWhiteSpace(verLocal) || CompareVersion(verEmbedded, verLocal) > 0 || !File.Exists(targetPath);
                if (need)
                {
                    File.WriteAllText(targetPath, embeddedText, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }
        
        private static string ExtractTemplateVersion(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                var m = Regex.Match(text, "<meta\\s+name=\\\"workitem-details-template-version\\\"\\s+content=\\\"([^\\\"]+)\\\"\\s*/?>", RegexOptions.IgnoreCase);
                if (m.Success) return (m.Groups[1].Value ?? "").Trim();
                return null;
            }
            catch
            {
                return null;
            }
        }
        
        private static string ExtractTemplateVersionFromFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                var txt = File.ReadAllText(path, Encoding.UTF8);
                return ExtractTemplateVersion(txt);
            }
            catch
            {
                return null;
            }
        }
        
        private static int CompareVersion(string a, string b)
        {
            try
            {
                var sa = (a ?? "").Trim();
                var sb = (b ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sa) && string.IsNullOrWhiteSpace(sb)) return 0;
                if (string.IsNullOrWhiteSpace(sa)) return -1;
                if (string.IsNullOrWhiteSpace(sb)) return 1;
                var pa = sa.Split('.');
                var pb = sb.Split('.');
                var len = Math.Max(pa.Length, pb.Length);
                for (int i = 0; i < len; i++)
                {
                    var va = i < pa.Length ? pa[i] : "0";
                    var vb = i < pb.Length ? pb[i] : "0";
                    if (int.TryParse(va, out var ia) && int.TryParse(vb, out var ib))
                    {
                        if (ia != ib) return ia > ib ? 1 : -1;
                    }
                    else
                    {
                        var c = string.Compare(va, vb, StringComparison.OrdinalIgnoreCase);
                        if (c != 0) return c > 0 ? 1 : -1;
                    }
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }
        
        private string DashText(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? "-" : HtmlEscape(s);
        }
        
        private string NormalizeImages(string html)
        {
            try
            {
                var result = ImgTagRegex.Replace(html, m =>
                {
                    var tag = m.Value;
                    var originUrl = ExtractAttr(tag, "originUrl");
                    var src = ExtractAttr(tag, "src");
                    var use = string.IsNullOrWhiteSpace(src) ? originUrl : src;
                    use = System.Net.WebUtility.HtmlDecode(use ?? "");
                    var withToken = AppendPublicImageTokenIfNeeded(use, Details?.PublicImageToken);
                    var finalUse = string.IsNullOrWhiteSpace(withToken) ? use : withToken;
                    finalUse = AppendAccessTokenQueryIfNeeded(finalUse, _accessToken);
                    if (string.IsNullOrWhiteSpace(use)) return tag;
                    var encodedUse = System.Net.WebUtility.HtmlEncode(finalUse ?? "");
                    var updated = tag;
                    if (string.IsNullOrWhiteSpace(src)) updated = updated.Replace("<img", $"<img src=\"{encodedUse}\"");
                    else
                    {
                        updated = Regex.Replace(updated, "\\bsrc\\s*=\\s*\"([^\"]+)\"", $"src=\"{encodedUse}\"", RegexOptions.IgnoreCase);
                        updated = Regex.Replace(updated, "\\bsrc\\s*=\\s*'([^']+)'", $"src='{encodedUse}'", RegexOptions.IgnoreCase);
                        updated = Regex.Replace(updated, "\\bsrc\\s*=\\s*([^\\s>]+)", $"src=\"{encodedUse}\"", RegexOptions.IgnoreCase);
                    }
                    var host = "";
                    try { host = new Uri(finalUse).Host.ToLowerInvariant(); } catch { }
                    var isAtlasPublic = host.Contains("atlas.pingcode.com") || finalUse.ToLowerInvariant().Contains("/files/public/");
                    if (!isAtlasPublic && !Regex.IsMatch(updated, "\\breferrerpolicy\\s*=", RegexOptions.IgnoreCase))
                    {
                        updated = updated.Replace("<img", "<img referrerpolicy=\"no-referrer\"");
                    }
                    if (!Regex.IsMatch(updated, "\\bloading\\s*=", RegexOptions.IgnoreCase))
                    {
                        updated = updated.Replace("<img", "<img loading=\"lazy\"");
                    }
                    return updated;
                });
                result = AnchorTagRegex.Replace(result, m =>
                {
                    var tag = m.Value;
                    var href = ExtractAttr(tag, "href");
                    var text = m.Groups[1].Value;
                    var decodedHref = System.Net.WebUtility.HtmlDecode(href ?? "");
                    var withToken = AppendPublicImageTokenIfNeeded(decodedHref, Details?.PublicImageToken);
                    var finalUse = string.IsNullOrWhiteSpace(withToken) ? decodedHref : withToken;
                    finalUse = AppendAccessTokenQueryIfNeeded(finalUse, _accessToken);
                    if (string.IsNullOrWhiteSpace(finalUse)) return tag;
                    if (!LooksLikeImageUrl(finalUse)) return tag;
                    var encodedUse = System.Net.WebUtility.HtmlEncode(finalUse ?? "");
                    return $"<img src=\"{encodedUse}\" alt=\"{System.Net.WebUtility.HtmlEncode(text)}\"/>";
                });
                return result;
            }
            catch
            {
                return html;
            }
        }
        
        private static string ExtractAttr(string tag, string attr)
        {
            try
            {
                var v = Regex.Match(tag, $"\\b{attr}\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(v)) return v;
                v = Regex.Match(tag, $"\\b{attr}\\s*=\\s*'([^']+)'", RegexOptions.IgnoreCase).Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(v)) return v;
                v = Regex.Match(tag, $"\\b{attr}\\s*=\\s*([^\\s>]+)", RegexOptions.IgnoreCase).Groups[1].Value;
                return v;
            }
            catch
            {
                return null;
            }
        }
        
        private void ShowLoading(bool on)
        {
            try
            {
                LoadingOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
            }
        }
        
        private static string AppendPublicImageTokenIfNeeded(string url, string token)
        {
            try
            {
                var u = (url ?? "").Trim();
                if (string.IsNullOrWhiteSpace(u)) return u;
                if (u.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return u;
                var lower = u.ToLowerInvariant();
                var isAtlasPublic = lower.Contains("atlas.pingcode.com") || lower.Contains("/files/public/");
                if (!isAtlasPublic) return u;
                if (lower.Contains("token=")) return u;
                if (string.IsNullOrWhiteSpace(token)) return u;
                if (u.Contains("?")) return $"{u}&token={Uri.EscapeDataString(token)}";
                return $"{u}?token={Uri.EscapeDataString(token)}";
            }
            catch
            {
                return url;
            }
        }
        
        private static string AppendAccessTokenQueryIfNeeded(string url, string accessToken)
        {
            try
            {
                var u = (url ?? "").Trim();
                if (string.IsNullOrWhiteSpace(u)) return u;
                var lower = u.ToLowerInvariant();
                if (u.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return u;
                var isPingCode = lower.Contains("pingcode.com") || lower.Contains(".pingcode.com");
                if (!isPingCode) return u;
                if (lower.Contains("access_token=")) return u;
                if (string.IsNullOrWhiteSpace(accessToken)) return u;
                if (u.Contains("?")) return $"{u}&access_token={Uri.EscapeDataString(accessToken)}";
                return $"{u}?access_token={Uri.EscapeDataString(accessToken)}";
            }
            catch
            {
                return url;
            }
        }
        
        private void InferPublicImageToken()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(Details?.PublicImageToken)) return;
                var t = TryExtractTokenFromHtml(Details?.DescriptionHtml);
                if (string.IsNullOrWhiteSpace(t)) t = TryExtractTokenFromHtml(Details?.SketchHtml);
                if (string.IsNullOrWhiteSpace(t) && Details?.Comments != null)
                {
                    foreach (var c in Details.Comments)
                    {
                        t = TryExtractTokenFromHtml(c?.ContentHtml);
                        if (!string.IsNullOrWhiteSpace(t)) break;
                    }
                }
                if (!string.IsNullOrWhiteSpace(t)) Details.PublicImageToken = t;
            }
            catch
            {
            }
        }
        
        private static string TryExtractTokenFromHtml(string html)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(html)) return null;
                var m = Regex.Match(html, "(?:[?&])token=([^&\"'\\s]+)", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
                return null;
            }
            catch
            {
                return null;
            }
        }
        
        private static bool LooksLikeImageUrl(string url)
        {
            try
            {
                var u = (url ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(u)) return false;
                if (u.StartsWith("data:image/")) return true;
                if (u.EndsWith(".png") || u.EndsWith(".jpg") || u.EndsWith(".jpeg") || u.EndsWith(".gif") || u.EndsWith(".bmp") || u.EndsWith(".webp") || u.EndsWith(".svg")) return true;
                if (u.Contains("atlas.pingcode.com") || u.Contains("/files/public/")) return true;
                return false;
            }
            catch
            {
                return false;
            }
        }
        
        private static string ReadFileCached(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                lock (TemplateCache)
                {
                    if (TemplateCache.TryGetValue(path, out var t) && !string.IsNullOrWhiteSpace(t)) return t;
                    var txt = File.ReadAllText(path, Encoding.UTF8);
                    TemplateCache[path] = txt ?? "";
                    return txt ?? "";
                }
            }
            catch
            {
                return null;
            }
        }
        
        private static string MapSeverityText(string raw)
        {
            var s = (raw ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) return "-";
            if (s == "5cb7e6e2fda1ce4ca0020004") return "致命";
            if (s == "5cb7e6e2fda1ce4ca0020003") return "严重";
            if (s == "5cb7e6e2fda1ce4ca0020002") return "一般";
            if (s == "5cb7e6e2fda1ce4ca0020001") return "建议";
            if (s.Contains("critical") || s.Contains("致命")) return "致命";
            if (s.Contains("严重") || s.Contains("major")) return "严重";
            if (s.Contains("一般") || s.Contains("normal")) return "一般";
            if (s.Contains("建议") || s.Contains("minor") || s.Contains("suggest")) return "建议";
            return "-";
        }
        
        private static string FormatDate(DateTime? dt)
        {
            if (!dt.HasValue) return "-";
            var v = dt.Value;
            if (v == default) return "-";
            return v.ToString("yyyy-MM-dd");
        }
        
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
