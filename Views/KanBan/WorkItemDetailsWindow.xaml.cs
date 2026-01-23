using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PackageManager.Services;
using PackageManager.Services.PingCode;
using PackageManager.Services.PingCode.Dto;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Views.KanBan;

public partial class WorkItemDetailsWindow : Window, INotifyPropertyChanged
{
    private static readonly Regex ImgTagRegex = new("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnchorTagRegex = new("<a\\b[^>]*>([\\s\\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> TemplateCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> MembersJsonCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, List<StateDto>> StateFlowsCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly PingCodeApiService api;

    private string accessToken;

    private bool docBridgeInjectedOnDocumentCreated;

    private List<StateDto> availableStates = new();

    private readonly Dictionary<string, Newtonsoft.Json.Linq.JObject> uploadedAttachmentMap = new(StringComparer.OrdinalIgnoreCase);

    private bool childrenLoaded;

    private string cachedMembersJson;

    public WorkItemDetailsWindow(WorkItemDetails details, PingCodeApiService api)
    {
        Details = details ?? new WorkItemDetails();
        this.api = api ?? new PingCodeApiService();
        InitializeComponent();
        DataContext = this;
        Loaded += async (s, e) =>
        {
            try
            {
                ShowLoading(true);
                InferPublicImageToken();
                var core = await InitializeWebViewAsync();
                await InjectDomReadyBridgeScript(core);
                RegisterCoreEvents(core);
                await NavigateAndInitAsync();
            }
            catch
            {
                try
                {
                    ShowLoading(false);
                }
                catch
                {
                }
            }
        };
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public WorkItemDetails Details { get; }

    protected void OnPropertyChanged([CallerMemberName] string name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }

    private static string JsEscape(string s)
    {
        s = s ?? "";
        return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static string GetQueryParam(Uri uri, string name)
    {
        try
        {
            var q = uri?.Query ?? "";
            if (string.IsNullOrWhiteSpace(q))
            {
                return null;
            }

            if (q.StartsWith("?"))
            {
                q = q.Substring(1);
            }

            var parts = q.Split('&');
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                var kv = part.Split(new[] { '=' }, 2);
                var k = Uri.UnescapeDataString(kv[0] ?? "");
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Length > 1 ? Uri.UnescapeDataString(kv[1] ?? "") : "";
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

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
        if (list.Count == 0)
        {
            return "<span>-</span>";
        }

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

    private static string GetTemplatePath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var rel = Path.Combine("Views", "Templates", "workitem-details.html");
        var candidates = new List<string>();
        candidates.Add(Path.Combine(baseDir, rel));
        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; (i < 6) && (dir != null); i++)
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
            if (File.Exists(p))
            {
                return p;
            }
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

    private static string ExtractTemplateVersion(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var m = Regex.Match(text,
                                "<meta\\s+name=\\\"workitem-details-template-version\\\"\\s+content=\\\"([^\\\"]+)\\\"\\s*/?>",
                                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return (m.Groups[1].Value ?? "").Trim();
            }

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
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

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
            if (string.IsNullOrWhiteSpace(sa) && string.IsNullOrWhiteSpace(sb))
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(sa))
            {
                return -1;
            }

            if (string.IsNullOrWhiteSpace(sb))
            {
                return 1;
            }

            var pa = sa.Split('.');
            var pb = sb.Split('.');
            var len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                var va = i < pa.Length ? pa[i] : "0";
                var vb = i < pb.Length ? pb[i] : "0";
                if (int.TryParse(va, out var ia) && int.TryParse(vb, out var ib))
                {
                    if (ia != ib)
                    {
                        return ia > ib ? 1 : -1;
                    }
                }
                else
                {
                    var c = string.Compare(va, vb, StringComparison.OrdinalIgnoreCase);
                    if (c != 0)
                    {
                        return c > 0 ? 1 : -1;
                    }
                }
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string ExtractAttr(string tag, string attr)
    {
        try
        {
            var v = Regex.Match(tag, $"\\b{attr}\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }

            v = Regex.Match(tag, $"\\b{attr}\\s*=\\s*'([^']+)'", RegexOptions.IgnoreCase).Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }

            v = Regex.Match(tag, $"\\b{attr}\\s*=\\s*([^\\s>]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            return v;
        }
        catch
        {
            return null;
        }
    }

    private static string AppendPublicImageTokenIfNeeded(string url, string token)
    {
        try
        {
            var u = (url ?? "").Trim();
            if (string.IsNullOrWhiteSpace(u))
            {
                return u;
            }

            if (u.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return u;
            }

            var lower = u.ToLowerInvariant();
            var isAtlasPublic = lower.Contains("atlas.pingcode.com") || lower.Contains("/files/public/");
            if (!isAtlasPublic)
            {
                return u;
            }

            if (lower.Contains("token="))
            {
                return u;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return u;
            }

            if (u.Contains("?"))
            {
                return $"{u}&token={Uri.EscapeDataString(token)}";
            }

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
            if (string.IsNullOrWhiteSpace(u))
            {
                return u;
            }

            var lower = u.ToLowerInvariant();
            if (u.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return u;
            }

            var isPingCode = lower.Contains("pingcode.com") || lower.Contains(".pingcode.com");
            if (!isPingCode)
            {
                return u;
            }

            if (lower.Contains("access_token="))
            {
                return u;
            }

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return u;
            }

            if (u.Contains("?"))
            {
                return $"{u}&access_token={Uri.EscapeDataString(accessToken)}";
            }

            return $"{u}?access_token={Uri.EscapeDataString(accessToken)}";
        }
        catch
        {
            return url;
        }
    }

    private static string TryExtractTokenFromHtml(string html)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var m = Regex.Match(html, "(?:[?&])token=([^&\"'\\s]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups[1].Value;
            }

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
            if (string.IsNullOrEmpty(u))
            {
                return false;
            }

            if (u.StartsWith("data:image/"))
            {
                return true;
            }

            if (u.EndsWith(".png") || u.EndsWith(".jpg") || u.EndsWith(".jpeg") || u.EndsWith(".gif") || u.EndsWith(".bmp") || u.EndsWith(".webp") ||
                u.EndsWith(".svg"))
            {
                return true;
            }

            if (u.Contains("atlas.pingcode.com") || u.Contains("/files/public/"))
            {
                return true;
            }

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
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            lock (TemplateCache)
            {
                if (TemplateCache.TryGetValue(path, out var t) && !string.IsNullOrWhiteSpace(t))
                {
                    return t;
                }

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
        if (string.IsNullOrEmpty(s))
        {
            return "-";
        }

        if (s == "5cb7e6e2fda1ce4ca0020004")
        {
            return "致命";
        }

        if (s == "5cb7e6e2fda1ce4ca0020003")
        {
            return "严重";
        }

        if (s == "5cb7e6e2fda1ce4ca0020002")
        {
            return "一般";
        }

        if (s == "5cb7e6e2fda1ce4ca0020001")
        {
            return "建议";
        }

        if (s.Contains("critical") || s.Contains("致命"))
        {
            return "致命";
        }

        if (s.Contains("严重") || s.Contains("major"))
        {
            return "严重";
        }

        if (s.Contains("一般") || s.Contains("normal"))
        {
            return "一般";
        }

        if (s.Contains("建议") || s.Contains("minor") || s.Contains("suggest"))
        {
            return "建议";
        }

        return "-";
    }

    private static string FormatDate(DateTime? dt)
    {
        if (!dt.HasValue)
        {
            return "-";
        }

        var v = dt.Value;
        if (v == default)
        {
            return "-";
        }

        var local = v.Kind == DateTimeKind.Utc ? v.ToLocalTime() : v;
        return local.ToString("yyyy-MM-dd HH:mm");
    }

    private static string FormatFriendlyTime(DateTime? dt)
    {
        if (!dt.HasValue)
        {
            return "-";
        }
        var v = dt.Value;
        if (v == default)
        {
            return "-";
        }
        var local = v.Kind == DateTimeKind.Utc ? v.ToLocalTime() : v;
        return local.ToString("yyyy-MM-dd HH:mm");
    }

    private async Task<CoreWebView2> InitializeWebViewAsync()
    {
        var dataService = new DataPersistenceService();
        var userDataFolder = Path.Combine(dataService.GetDataFolderPath(), "WebView2Cache");
        Directory.CreateDirectory(userDataFolder);
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await DetailsWeb.EnsureCoreWebView2Async(env);
        var core = DetailsWeb.CoreWebView2;
        core.Settings.IsWebMessageEnabled = true;
        return core;
    }

    private string BuildDocBridgeScript()
    {
        var wi = JsEscape(Details.Id ?? "");
        var docJs =
            "(function(){try{function pv(v){try{var n=parseFloat(v);if(isNaN(n)||n<0){return 0;}return n;}catch(e){return 0;}}function bind(){try{if(window.__pm_bind_done){return;}window.__pm_bind_done=true;var ip=document.getElementById('spInput');if(ip){ip.addEventListener('blur',function(){try{var val=pv(ip.value);if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'updateStoryPoints',id:'" +
            wi +
            "',value:val});}}catch(e){}});ip.addEventListener('keydown',function(e){if(e.key==='Enter'){try{var val=pv(ip.value);if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'updateStoryPoints',id:'" +
            wi +
            "',value:val});}}catch(e){}}});}var sel=document.getElementById('stateSelect');if(sel){sel.addEventListener('change',function(){try{var val=sel&&sel.value;if(val&&window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'updateState',id:'" +
            wi +
            "',stateId:val});}}catch(e){}});} }catch(e){}}document.addEventListener('DOMContentLoaded',function(){try{bind();var has=document.getElementById('stateSelect')||document.getElementById('spInput');if(has&&window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'ready',id:'" +
            wi +
            "'});}}catch(e){}});document.addEventListener('readystatechange',function(){try{if(document.readyState==='interactive'||document.readyState==='complete'){bind();var has=document.getElementById('stateSelect')||document.getElementById('spInput');if(has&&window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'ready',id:'" +
            wi + "'});}}}catch(e){}});}catch(e){}})();";
        return docJs;
    }

    private async Task InjectDomReadyBridgeScript(CoreWebView2 core)
    {
        try
        {
            var mi = core.GetType().GetMethod("AddScriptToExecuteOnDocumentCreated");
            var docJs = BuildDocBridgeScript();
            if (mi != null)
            {
                mi.Invoke(core, new object[] { docJs });
                docBridgeInjectedOnDocumentCreated = true;
            }
            else
            {
                await DetailsWeb.CoreWebView2.ExecuteScriptAsync(docJs);
                docBridgeInjectedOnDocumentCreated = false;
            }
        }
        catch
        {
        }
    }

    private void RegisterCoreEvents(CoreWebView2 core)
    {
        core.WebMessageReceived += async (sender, args) =>
        {
            try
            {
                var msg = args.WebMessageAsJson ?? "";
                if (string.IsNullOrWhiteSpace(msg))
                {
                    return;
                }

                double val = 0;
                string id = Details.Id;
                string type = null;
                string stateId = null;
                bool handleSp = false;
                bool handleState = false;
                bool handleReady = false;
                bool handleSubmit = false;
                string commentHtml = null;
                Newtonsoft.Json.Linq.JArray contentPayload = null;
                string plainText = null;
                List<string> attachmentsFromClient = null;
                try
                {
                    var token = Newtonsoft.Json.Linq.JToken.Parse(msg);
                    if (token is Newtonsoft.Json.Linq.JObject obj)
                    {
                        type = obj.Value<string>("type");
                        string localId = null;
                        string dataUrl = null;
                        string contentType = null;
                        if (string.Equals(type, "updateStoryPoints", StringComparison.OrdinalIgnoreCase))
                        {
                            id = obj.Value<string>("id") ?? Details.Id;
                            val = obj.Value<double?>("value") ?? 0;
                            handleSp = true;
                        }
                        else if (string.Equals(type, "updateState", StringComparison.OrdinalIgnoreCase))
                        {
                            id = obj.Value<string>("id") ?? Details.Id;
                            stateId = obj.Value<string>("stateId") ?? obj.Value<string>("state_id");
                            handleState = true;
                        }
                        else if (string.Equals(type, "ready", StringComparison.OrdinalIgnoreCase))
                        {
                            handleReady = true;
                        }
                        else if (string.Equals(type, "submitComment", StringComparison.OrdinalIgnoreCase))
                        {
                            id = obj.Value<string>("id") ?? Details.Id;
                            commentHtml = obj.Value<string>("html") ?? obj.Value<string>("body") ?? obj.Value<string>("text");
                            contentPayload = obj["content"] as Newtonsoft.Json.Linq.JArray;
                            plainText = obj.Value<string>("text");
                            var arr = obj["attachments"] as Newtonsoft.Json.Linq.JArray;
                            if (arr != null)
                            {
                                attachmentsFromClient = arr
                                    .Select(x => ReadUrlFromAttachmentToken(x))
                                    .Where(s => !string.IsNullOrWhiteSpace(s))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                            }
                            handleSubmit = true;
                        }
                        else if (string.Equals(type, "uploadImageData", StringComparison.OrdinalIgnoreCase))
                        {
                            id = obj.Value<string>("id") ?? Details.Id;
                            localId = obj.Value<string>("localId");
                            dataUrl = obj.Value<string>("dataUrl");
                            contentType = obj.Value<string>("contentType");
                            if (!string.IsNullOrWhiteSpace(localId) && !string.IsNullOrWhiteSpace(dataUrl))
                            {
                                try
                                {
                                    var escUrl = JsEscape(dataUrl);
                                    var escId = JsEscape(localId);
                                    var script =
                                        "try{if(window.addAttachment){window.addAttachment('"+escUrl+"');}var im=document.querySelector('img[data-local-id=\""+escId+"\"]');if(im){im.remove();}}catch(e){}";
                                    await DetailsWeb.CoreWebView2.ExecuteScriptAsync(script);
                                }
                                catch
                                {
                                }
                            }
                            return;
                        }
                        else if (string.Equals(type, "loadChildren", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!childrenLoaded)
                            {
                                await InitializeChildWorkItemsAsync();
                            }
                            return;
                        }
                        else
                        {
                            return;
                        }
                    }
                    else if (token is Newtonsoft.Json.Linq.JValue jv && (jv.Type == Newtonsoft.Json.Linq.JTokenType.String))
                    {
                        var inner = jv.ToString() ?? "";
                        var innerTok = Newtonsoft.Json.Linq.JToken.Parse(inner);
                        if (innerTok is Newtonsoft.Json.Linq.JObject jobj)
                        {
                            type = jobj.Value<string>("type");
                            string localId = null;
                            string dataUrl = null;
                            string contentType = null;
                            if (string.Equals(type, "updateStoryPoints", StringComparison.OrdinalIgnoreCase))
                            {
                                id = jobj.Value<string>("id") ?? Details.Id;
                                val = jobj.Value<double?>("value") ?? 0;
                                handleSp = true;
                            }
                            else if (string.Equals(type, "updateState", StringComparison.OrdinalIgnoreCase))
                            {
                                id = jobj.Value<string>("id") ?? Details.Id;
                                stateId = jobj.Value<string>("stateId") ?? jobj.Value<string>("state_id");
                                handleState = true;
                            }
                            else if (string.Equals(type, "ready", StringComparison.OrdinalIgnoreCase))
                            {
                                handleReady = true;
                            }
                            else if (string.Equals(type, "submitComment", StringComparison.OrdinalIgnoreCase))
                            {
                                id = jobj.Value<string>("id") ?? Details.Id;
                                commentHtml = jobj.Value<string>("html") ?? jobj.Value<string>("body") ?? jobj.Value<string>("text");
                                contentPayload = jobj["content"] as Newtonsoft.Json.Linq.JArray;
                                plainText = jobj.Value<string>("text");
                                var arr = jobj["attachments"] as Newtonsoft.Json.Linq.JArray;
                                if (arr != null)
                                {
                                    attachmentsFromClient = arr
                                        .Select(x => ReadUrlFromAttachmentToken(x))
                                        .Where(s => !string.IsNullOrWhiteSpace(s))
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList();
                                }
                                handleSubmit = true;
                            }
                            else if (string.Equals(type, "uploadImageData", StringComparison.OrdinalIgnoreCase))
                            {
                                id = jobj.Value<string>("id") ?? Details.Id;
                                localId = jobj.Value<string>("localId");
                                dataUrl = jobj.Value<string>("dataUrl");
                                contentType = jobj.Value<string>("contentType");
                                if (!string.IsNullOrWhiteSpace(localId) && !string.IsNullOrWhiteSpace(dataUrl))
                                {
                                    try
                                    {
                                        var escUrl = JsEscape(dataUrl);
                                        var escId = JsEscape(localId);
                                        var script =
                                            "try{if(window.addAttachment){window.addAttachment('"+escUrl+"');}var im=document.querySelector('img[data-local-id=\""+escId+"\"]');if(im){im.remove();}}catch(e){}";
                                        await DetailsWeb.CoreWebView2.ExecuteScriptAsync(script);
                                    }
                                    catch
                                    {
                                    }
                                }
                                return;
                            }
                            else if (string.Equals(type, "loadChildren", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!childrenLoaded)
                                {
                                    await InitializeChildWorkItemsAsync();
                                }
                                return;
                            }
                            else
                            {
                                return;
                            }
                        }
                        else
                        {
                            var parts = inner.Split('|');
                            if ((parts.Length >= 2) && (parts[0] == "updateStoryPoints"))
                            {
                                id = parts[1];
                                double.TryParse(parts.Length > 2 ? parts[2] : "0", out val);
                                handleSp = true;
                            }
                            else if ((parts.Length >= 2) && (parts[0] == "updateState"))
                            {
                                id = parts[1];
                                stateId = parts.Length > 2 ? parts[2] : null;
                                handleState = true;
                            }
                            else if ((parts.Length >= 1) && (parts[0] == "ready"))
                            {
                                handleReady = true;
                            }
                            else if ((parts.Length >= 2) && (parts[0] == "submitComment"))
                            {
                                id = parts[1];
                                commentHtml = parts.Length > 2 ? parts[2] : null;
                                handleSubmit = true;
                            }
                            else
                            {
                                return;
                            }
                        }
                    }
                    else
                    {
                        return;
                    }
                }
                catch
                {
                    return;
                }

                if (handleReady)
                {
                    try
                    {
                        await InitializeStateDropdownAsync();
                        await InitializeProjectMembersAsync();
                    }
                    catch
                    {
                    }
                }

                if (handleSp)
                {
                    if (val < 0)
                    {
                        val = 0;
                    }

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        id = Details.Id;
                    }

                    if (Math.Abs(Details.StoryPoints - val) < 1e-3)
                    {
                        return;
                    }

                    try
                    {
                        var ok = await api.UpdateWorkItemStoryPointsAsync(id, val);
                        if (ok)
                        {
                            Details.StoryPoints = val;
                            var script = "try{var ip=document.getElementById('spInput');if(ip){ip.value='" + val.ToString("0.##") + "';}}catch(e){}";
                            await DetailsWeb.CoreWebView2.ExecuteScriptAsync(script);
                        }
                    }
                    catch
                    {
                    }
                }
                else if (handleState)
                {
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        id = Details.Id;
                    }

                    if (string.IsNullOrWhiteSpace(stateId))
                    {
                        return;
                    }

                    try
                    {
                        var ok = await api.UpdateWorkItemStateByIdAsync(id, stateId);
                        if (ok)
                        {
                            var st = availableStates?.FirstOrDefault(s => string.Equals(s?.Id ?? "",
                                                                                        stateId ?? "",
                                                                                        StringComparison.OrdinalIgnoreCase));
                            var newName = st?.Name ?? Details.StateName;
                            var newType = st?.Type ?? Details.StateType;
                            Details.StateName = newName;
                            Details.StateType = newType;
                            Details.StateId = stateId;
                            await RefreshAvailableStatesAndUpdateDropdownAsync();
                        }
                    }
                    catch
                    {
                    }
                }
                else if (string.Equals(type, "loadChildren", StringComparison.OrdinalIgnoreCase))
                {
                    if (!childrenLoaded)
                    {
                        await InitializeChildWorkItemsAsync();
                    }
                }
                else if (handleSubmit)
                {
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        id = Details.Id;
                    }

                    if (string.IsNullOrWhiteSpace(commentHtml))
                    {
                        commentHtml = "PingCode必须要评论要有文本内容，如果你没有它就把文件名当评论内容，真的蠢";
                    }

                    try
                    {
                        var processed = await ProcessCommentHtmlAsync(commentHtml, id);
                        var dataUrls = new List<string>();
                        if (attachmentsFromClient != null) { dataUrls.AddRange(attachmentsFromClient); }
                        if (processed?.AttachmentUrls != null) { dataUrls.AddRange(processed.AttachmentUrls); }
                        dataUrls = dataUrls.Where(s => !string.IsNullOrWhiteSpace(s) && s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        if (string.IsNullOrWhiteSpace(processed.Html) && dataUrls.Count == 0) { return; }

                        Newtonsoft.Json.Linq.JObject created = null;
                        if (contentPayload != null && ContainsMention(contentPayload))
                        {
                            created = await api.CreateWorkItemCommentWithPayloadAsync(id, contentPayload);
                        }
                        else
                        {
                            created = await api.CreateGenericWorkItemCommentAsync(id, processed.Html);
                        }

                        var commentId = created?.Value<string>("id")
                                         ?? created?["data"]?.Value<string>("id")
                                         ?? created?["value"]?.Value<string>("id")
                                         ?? created?["comment"]?.Value<string>("id");

                        var uploadResults = new List<Newtonsoft.Json.Linq.JObject>();
                        if (!string.IsNullOrWhiteSpace(commentId) && dataUrls.Count > 0)
                        {
                            var tasks = new List<Task<Newtonsoft.Json.Linq.JObject>>();
                            foreach (var du in dataUrls)
                            {
                                string mime;
                                byte[] bytes;
                                if (!TryParseDataUrl(du, out mime, out bytes) || (bytes == null) || (bytes.Length == 0)) { continue; }
                                var ext = "png";
                                var ct = (mime ?? "").ToLowerInvariant();
                                if (ct.Contains("jpeg") || ct.Contains("jpg")) ext = "jpg";
                                else if (ct.Contains("gif")) ext = "gif";
                                else if (ct.Contains("bmp")) ext = "bmp";
                                else if (ct.Contains("webp")) ext = "webp";
                                else if (ct.Contains("svg")) ext = "svg";
                                var name = $"image_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{ext}";
                                tasks.Add(UploadAttachmentViaApiAsync(bytes, name, mime, id, commentId));
                            }
                            var results = await Task.WhenAll(tasks);
                            foreach (var r in results)
                            {
                                if (r != null)
                                {
                                    RememberUploadedAttachment(r);
                                    uploadResults.Add(r);
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(commentId))
                        {
                            var createdObj = created?["data"] ?? created?["value"] ?? created?["comment"] ?? created;
                            var authorName = FirstNonEmpty(
                                createdObj?["created_by"]?.Value<string>("name"),
                                createdObj?["author"]?.Value<string>("name"),
                                createdObj?.Value<string>("created_by_name"),
                                createdObj?.Value<string>("author_name"),
                                createdObj?["user"]?.Value<string>("name")
                            );
                            var authorAvatar = FirstNonEmpty(
                                createdObj?.Value<string>("author_avatar"),
                                createdObj?.Value<string>("avatar"),
                                createdObj?.Value<string>("image_url"),
                                createdObj?["created_by"]?.Value<string>("avatar"),
                                createdObj?["created_by"]?.Value<string>("image_url"),
                                createdObj?["author"]?.Value<string>("avatar"),
                                createdObj?["author"]?.Value<string>("image_url"),
                                createdObj?["user"]?.Value<string>("avatar"),
                                createdObj?["user"]?.Value<string>("image_url")
                            );
                            DateTime? createdAt = ReadDateTimeFromSecondsLocal(createdObj?["created_at"]) ?? ReadDateTimeFromSecondsLocal(createdObj?["timestamp"]) ?? DateTime.Now;
                            var appended = BuildSingleCommentHtml(processed?.Html ?? "", uploadResults, authorName, authorAvatar, createdAt);
                            var escaped = JsEscape(appended);
                            var script =
                                "try{var c=document.querySelector('.comments-card');if(c){c.insertAdjacentHTML('beforeend','" + escaped +
                                "');}var ed=document.getElementById('commentEdit');if(ed){ed.innerHTML='';}var pre=document.getElementById('attachmentsPreview');if(pre){pre.innerHTML='';}try{if(typeof pendingAttachments!=='undefined'){pendingAttachments=[];}}catch(ex){}if(window.pendingAttachments){try{window.pendingAttachments=[];}catch(ex){}}var body=document.querySelector('.ant-drawer-body');var doScroll=function(){try{var expanded=document.getElementById('commentExpanded');var submit=document.getElementById('commentSubmitBtn');var cancel=document.getElementById('commentCancelBtn');var editor=document.getElementById('commentEditor');var target=submit||cancel||expanded||editor;if(target&&target.scrollIntoView){ target.scrollIntoView({block:'end'}); }if(body&&typeof body.scrollTo==='function'){ body.scrollTo({top: body.scrollHeight}); }else if(body){ body.scrollTop=body.scrollHeight; }var sc=document.scrollingElement||document.documentElement;if(sc){ sc.scrollTop=sc.scrollHeight; } else { window.scrollTo(0, (document.documentElement&&document.documentElement.scrollHeight)||document.body.scrollHeight); }}catch(e){}};var collapse=function(){try{var col=document.getElementById('commentCollapsed');var exp=document.getElementById('commentExpanded');if(col&&exp){col.style.display='flex';exp.style.display='none';}var ed2=document.getElementById('commentEdit');if(ed2){ed2.innerHTML='';}var pre2=document.getElementById('attachmentsPreview');if(pre2){pre2.innerHTML='';}if(window.pendingAttachments){try{window.pendingAttachments=[];}catch(ex){}}}catch(e){}};try{if(window.requestAnimationFrame){window.requestAnimationFrame(function(){window.requestAnimationFrame(doScroll);});}else{setTimeout(doScroll,30);}}catch(e){};try{setTimeout(doScroll,60);}catch(e){};try{setTimeout(doScroll,160);}catch(e){};try{setTimeout(doScroll,360);}catch(e){};try{setTimeout(collapse,100);}catch(e){};try{setTimeout(collapse,180);}catch(e){} }catch(e){}";
                            await DetailsWeb.CoreWebView2.ExecuteScriptAsync(script);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        };
        core.NavigationCompleted += async (sender, args) =>
        {
            try
            {
                ShowLoading(false);
                if (!docBridgeInjectedOnDocumentCreated)
                {
                    try
                    {
                        await DetailsWeb.CoreWebView2.ExecuteScriptAsync(BuildDocBridgeScript());
                    }
                    catch
                    {
                    }
                }

                if ((availableStates?.Count ?? 0) > 0)
                {
                    await RebuildStateSelectOptionsAsync(Details.StateName, availableStates);
                }
                else
                {
                    await InitializeStateDropdownAsync();
                }
                if (!string.IsNullOrWhiteSpace(cachedMembersJson))
                {
                    var script = "try{if(window.setProjectMembers){window.setProjectMembers(" + cachedMembersJson + ");}}catch(e){}";
                    await DetailsWeb.CoreWebView2.ExecuteScriptAsync(script);
                }
                else
                {
                    await InitializeProjectMembersAsync();
                }
            }
            catch
            {
            }
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
                if (isAtlasPublic && (args.ResourceContext == CoreWebView2WebResourceContext.Image))
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
                    var tk = await api.GetAccessTokenAsync();
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
        core.NewWindowRequested += (sender, e) =>
        {
            try
            {
                var url = e.Uri ?? "";
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }

                var lower = url.ToLowerInvariant();
                if (lower.StartsWith("http://") || lower.StartsWith("https://"))
                {
                    if ((lower.Contains("pingcode.com") || lower.Contains(".pingcode.com")) && !lower.Contains("access_token=") &&
                        !string.IsNullOrWhiteSpace(accessToken))
                    {
                        url = url.Contains("?")
                                  ? $"{url}&access_token={Uri.EscapeDataString(accessToken)}"
                                  : $"{url}?access_token={Uri.EscapeDataString(accessToken)}";
                    }

                    e.Handled = true;
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
                        System.Diagnostics.Process.Start(psi);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        };
        core.NavigationStarting += async (sender, args) =>
        {
            try
            {
                var u = args.Uri ?? "";
                if (u.StartsWith("pm://", StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                    var uri = new Uri(u);
                    var host = (uri.Host ?? "").ToLowerInvariant();
                    if (host == "workitem")
                    {
                        var id = uri.AbsolutePath.Trim('/');
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            id = GetQueryParam(uri, "id");
                        }

                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            try
                            {
                                var parentDetails = await api.GetWorkItemDetailsAsync(id);
                                if (parentDetails != null)
                                {
                                    var win = new WorkItemDetailsWindow(parentDetails, api);
                                    win.Owner = this;
                                    win.ShowDialog();
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                else if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                    var url = u;
                    try
                    {
                        var lower = url.ToLowerInvariant();
                        if ((lower.Contains("pingcode.com") || lower.Contains(".pingcode.com")) && !lower.Contains("access_token=") &&
                            !string.IsNullOrWhiteSpace(accessToken))
                        {
                            url = url.Contains("?")
                                      ? $"{url}&access_token={Uri.EscapeDataString(accessToken)}"
                                      : $"{url}?access_token={Uri.EscapeDataString(accessToken)}";
                        }

                        var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
                        System.Diagnostics.Process.Start(psi);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        };
    }

    private async Task NavigateAndInitAsync()
    {
        try
        {
            var tokenTask = api.GetAccessTokenAsync();
            var countTask = api.GetChildWorkItemCountAsync(Details.Id);
            var membersTask = PreloadProjectMembersJsonAsync();
            var statesTask = PreloadAvailableStatesAsync();
            await Task.WhenAll(tokenTask, countTask, membersTask, statesTask);
            accessToken = tokenTask.Result;
            Details.ChildrenCount = countTask.Result;
            cachedMembersJson = membersTask.Result;
            if (statesTask.Result != null && statesTask.Result.Count > 0)
            {
                availableStates = statesTask.Result;
            }
        }
        catch
        {
        }
        var html = await Task.Run(() => BuildHtml());
        DetailsWeb.CoreWebView2.NavigateToString(html);
    }

    private async Task<string> PreloadProjectMembersJsonAsync()
    {
        try
        {
            var projectId = (Details?.ProjectId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return null;
            }
            if (MembersJsonCache.TryGetValue(projectId, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }
            var members = await api.GetProjectMembersAsync(projectId);
            var arr = new Newtonsoft.Json.Linq.JArray();
            foreach (var m in members ?? new List<PackageManager.Services.PingCode.Model.Entity>())
            {
                var id = (m?.Id ?? "").Trim();
                var nm = (m?.Name ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(nm))
                {
                    var o = new Newtonsoft.Json.Linq.JObject { ["id"] = id, ["name"] = nm };
                    arr.Add(o);
                }
            }
            var json = arr.ToString(Newtonsoft.Json.Formatting.None);
            MembersJsonCache[projectId] = json;
            return json;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<StateDto>> PreloadAvailableStatesAsync()
    {
        try
        {
            var projectId = (Details?.ProjectId ?? "").Trim();
            var type = (Details?.Type ?? "").Trim();
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(type))
            {
                return new List<StateDto>();
            }
            var cacheKey = $"{projectId}::{type}::{Details.StateId ?? ""}";
            if (StateFlowsCache.TryGetValue(cacheKey, out var cachedFlows) && (cachedFlows?.Count ?? 0) > 0)
            {
                return cachedFlows;
            }
            var plans = await api.GetWorkItemStatePlansAsync(projectId);
            var plan = plans.FirstOrDefault(p => string.Equals((p?.WorkItemType ?? "").Trim(), type, StringComparison.OrdinalIgnoreCase));
            if ((plan == null) || string.IsNullOrWhiteSpace(plan.Id))
            {
                return new List<StateDto>();
            }
            var flows = await api.GetWorkItemStateFlowsAsync(plan.Id, Details.StateId);
            flows = flows ?? new List<StateDto>();
            if (flows.Count > 0)
            {
                StateFlowsCache[cacheKey] = flows;
            }
            return flows;
        }
        catch
        {
            return new List<StateDto>();
        }
    }

    private async Task InitializeProjectMembersAsync()
    {
        try
        {
            var projectId = (Details?.ProjectId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return;
            }
            var members = await api.GetProjectMembersAsync(projectId);
            var arr = new Newtonsoft.Json.Linq.JArray();
            foreach (var m in members ?? new List<PackageManager.Services.PingCode.Model.Entity>())
            {
                var id = (m?.Id ?? "").Trim();
                var nm = (m?.Name ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(nm))
                {
                    var o = new Newtonsoft.Json.Linq.JObject { ["id"] = id, ["name"] = nm };
                    arr.Add(o);
                }
            }
            var json = arr.ToString(Newtonsoft.Json.Formatting.None);
            var script = "try{if(window.setProjectMembers){window.setProjectMembers(" + json + ");}}catch(e){}";
            await DetailsWeb.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch
        {
        }
    }

    private async Task InitializeChildWorkItemsAsync()
    {
        try
        {
            var list = await api.GetChildWorkItemsAsync(Details.Id);
            var html = BuildChildrenTableHtml(list);
            var escaped = JsEscape(html);
            var script = "try{var c=document.getElementById('childrenList');if(c){c.innerHTML='" + escaped + "';}}catch(e){}";
            await DetailsWeb.CoreWebView2.ExecuteScriptAsync(script);
            childrenLoaded = true;
        }
        catch
        {
        }
    }

    private string BuildChildrenTableHtml(List<WorkItemInfo> list)
    {
        var items = list ?? new List<WorkItemInfo>();
        if (items.Count == 0)
        {
            return "<div>无子工作项</div>";
        }

        var sb = new StringBuilder();
        sb.Append("<div class=\"children-rows\">");
        foreach (var c in items)
        {
            var id = c?.Id ?? "";
            var identifier = HtmlEscape(c?.Identifier ?? id);
            var title = HtmlEscape(c?.Title ?? "");
            var statusText = DashText(c?.Status);
            var assignee = DashText(c?.AssigneeName);
            var sa = FormatDate(c?.StartAt);
            var ea = FormatDate(c?.EndAt);
            var linkId = System.Net.WebUtility.HtmlEncode(id);
            var s = (c?.Status ?? "").Trim().ToLowerInvariant();
            var cls = "state-pending";
            if (s.Contains("完成")) { cls = "state-done"; }
            else if (s.Contains("关闭")) { cls = "state-closed"; }
            else if (s.Contains("测试中")) { cls = "state-testing"; }
            else if (s.Contains("可测试")) { cls = "state-testable"; }
            else if (s.Contains("进行中") || s.Contains("开发中") || s.Contains("处理中") || s.Contains("progress") || s.Contains("in_progress")) { cls = "state-inprogress"; }
            sb.Append("<div class=\"children-row\">");
            sb.Append("<div class=\"id\"><a href=\"pm://workitem/" + linkId + "\">" + identifier + "</a></div>");
            sb.Append("<div class=\"title\"><a href=\"pm://workitem/" + linkId + "\">" + title + "</a></div>");
            sb.Append("<div class=\"status\"><span class=\"state-badge " + cls + "\">" + statusText + "</span></div>");
            sb.Append("<div class=\"assignee\">" + assignee + "</div>");
            sb.Append("<div class=\"start\">" + sa + "</div>");
            sb.Append("<div class=\"end\">" + ea + "</div>");
            sb.Append("</div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private string HtmlEscape(string s)
    {
        return System.Net.WebUtility.HtmlEncode(s ?? "");
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
        var crumb = BuildCrumbHtml();
        if (!string.IsNullOrWhiteSpace(crumb))
        {
            sb.AppendLine(crumb);
        }

        sb.AppendLine("<div class=\"ant-drawer ant-drawer-open ant-drawer-right\">");
        sb.AppendLine("<div class=\"ant-drawer-content-wrapper drawer\"><div class=\"ant-drawer-content\"><div class=\"ant-drawer-wrapper-body\"><div class=\"ant-drawer-body\" style=\"padding:0;height:100%;overflow:auto\">");
        sb.AppendLine("<div class=\"header\"><span class=\"id\">" + HtmlEscape(Details.Identifier) + "</span><span class=\"title\">" +
                      HtmlEscape(Details.Title) + "</span></div>");
        var stateType = (Details.StateType ?? "").Trim().ToLowerInvariant();
        var stateName = (Details.StateName ?? "").Trim().ToLowerInvariant();
        var stateCls = "state-pending";
        if (stateType.Contains("done") || stateName.Contains("完成"))
        {
            stateCls = "state-done";
        }
        else if (stateType.Contains("clos") || stateName.Contains("关闭") || stateName.Contains("拒绝"))
        {
            stateCls = "state-closed";
        }
        else if (stateName.Contains("可测试"))
        {
            stateCls = "state-testable";
        }
        else if (stateName.Contains("测试中"))
        {
            stateCls = "state-testing";
        }
        else if (stateType.Contains("progress") || stateType.Contains("in_progress") || stateName.Contains("进行中") || stateName.Contains("开发中") ||
                 stateName.Contains("处理中"))
        {
            stateCls = "state-inprogress";
        }

        var startText = FormatDate(Details.StartAt);
        var endText = FormatDate(Details.EndAt);
        sb.AppendLine("<div class=\"quick\">");
        sb.AppendLine($"<div class=\"item\"><div class=\"label\">负责人</div><div class=\"value\">{HtmlEscape(Details.AssigneeName)}</div></div>");
        sb.AppendLine($"<div class=\"item\"><div class=\"label\">状态</div><div class=\"value\"><select id=\"stateSelect\" style=\"width:160px;padding:4px;border:1px solid #D1D5DB;border-radius:6px\"><option selected>{HtmlEscape(Details.StateName)}</option></select></div></div>");
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
        var spInputVal = Math.Abs(Details.StoryPoints) < 0.000001 ? "0" : Details.StoryPoints.ToString("0.##");
        sb.AppendLine($"<div class=\"item\"><div class=\"label\">故事点</div><div class=\"value\"><input id=\"spInput\" type=\"number\" min=\"0\" step=\"0.01\" style=\"width:120px;padding:4px;border:1px solid #D1D5DB;border-radius:6px\" value=\"{spInputVal}\"></div></div>");
        sb.AppendLine($"<div class=\"item\"><div class=\"label\">所属产品</div><div class=\"value\">{DashText(Details.ProductName)}</div></div>");
        sb.AppendLine($"<div class=\"item\"><div class=\"label\">缺陷类别</div><div class=\"value\">{DashText(Details.DefectCategory)}</div></div>");
        sb.AppendLine($"<div class=\"item\"><div class=\"label\">复现版本号</div><div class=\"value\">{DashText(Details.ReproduceVersion)}</div></div>");
        sb.AppendLine($"<div class=\"item\"><div class=\"label\">复现概率</div><div class=\"value\">{DashText(Details.ReproduceProbability)}</div></div>");
        sb.AppendLine($"<div class=\"item\"><div class=\"label\">故事点汇总</div><div class=\"value\">{(Math.Abs(Details.StoryPointsSummary) < 0.000001 ? "-" : Details.StoryPointsSummary.ToString("0.##"))}</div></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"section-title\">标签</div>");
        sb.AppendLine("<div>");
        foreach (var t in Details.Tags ?? new List<string>())
        {
            sb.AppendLine($"<span class=\"ant-tag tag ant-tag-pink\">{HtmlEscape(t)}</span>");
        }

        if ((Details.Tags?.Count ?? 0) == 0)
        {
            sb.AppendLine("<span>-</span>");
        }

        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"section-title\">描述</div>");
        sb.AppendLine("<div class=\"desc-card\">");
        if (!string.IsNullOrWhiteSpace(Details.DescriptionHtml))
        {
            sb.AppendLine(NormalizeImages(Details.DescriptionHtml));
        }
        else
        {
            sb.AppendLine("<div>-</div>");
        }

        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"section-title\">示意图</div>");
        sb.AppendLine("<div class=\"sketch-card\">");
        if (!string.IsNullOrWhiteSpace(Details.SketchHtml))
        {
            sb.AppendLine(NormalizeImages(Details.SketchHtml));
        }
        else
        {
            sb.AppendLine("<div>-</div>");
        }

        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"section-title\">评论</div>");
        sb.AppendLine("<div class=\"comments-card\">");
        var commentsBlock = BuildCommentsHtml(Details.Comments);
        sb.AppendLine(commentsBlock);
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div></div></div></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<script>(function(){function parseVal(v){try{var n=parseFloat(v);if(isNaN(n)||n<0){return 0;}return n;}catch(e){return 0;}}function save(){try{var ip=document.getElementById('spInput');if(!ip){return;}var val=parseVal(ip.value);if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'updateStoryPoints', id:'" +
                      HtmlEscape(Details.Id) +
                      "', value:val});}}catch(e){}}function onStateChange(){try{var sel=document.getElementById('stateSelect');var val=sel&&sel.value;if(val&&window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'updateState', id:'" +
                      HtmlEscape(Details.Id) +
                      "', stateId:val});}}catch(e){}}try{var ip=document.getElementById('spInput');if(ip){ip.addEventListener('blur', save);ip.addEventListener('keydown', function(e){ if(e.key==='Enter'){ save(); } });}var sel=document.getElementById('stateSelect');if(sel){sel.addEventListener('change', onStateChange);}}catch(e){}})();</script>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private async Task InitializeStateDropdownAsync()
    {
        try
        {
            var projectId = (Details?.ProjectId ?? "").Trim();
            var type = (Details?.Type ?? "").Trim();
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            var plans = await api.GetWorkItemStatePlansAsync(projectId);
            var plan = plans.FirstOrDefault(p => string.Equals((p?.WorkItemType ?? "").Trim(), type, StringComparison.OrdinalIgnoreCase));
            if ((plan == null) || string.IsNullOrWhiteSpace(plan.Id))
            {
                return;
            }

            var flows = await api.GetWorkItemStateFlowsAsync(plan.Id, Details.StateId);
            availableStates = flows ?? new List<StateDto>();
            await RebuildStateSelectOptionsAsync(Details.StateName, availableStates);
        }
        catch
        {
        }
    }

    private async Task RefreshAvailableStatesAndUpdateDropdownAsync()
    {
        try
        {
            var projectId = (Details?.ProjectId ?? "").Trim();
            var type = (Details?.Type ?? "").Trim();
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            var plans = await api.GetWorkItemStatePlansAsync(projectId);
            var plan = plans.FirstOrDefault(p => string.Equals((p?.WorkItemType ?? "").Trim(), type, StringComparison.OrdinalIgnoreCase));
            if ((plan == null) || string.IsNullOrWhiteSpace(plan.Id))
            {
                return;
            }

            var flows = await api.GetWorkItemStateFlowsAsync(plan.Id, Details.StateId);
            availableStates = flows ?? new List<StateDto>();
            await RebuildStateSelectOptionsAsync(Details.StateName, availableStates);
        }
        catch
        {
        }
    }

    private async Task RebuildStateSelectOptionsAsync(string currentStateName, IEnumerable<StateDto> flows)
    {
        var js = new StringBuilder();
        var nm = JsEscape(currentStateName ?? "");
        js
            .Append("try{var sel=document.getElementById('stateSelect');if(sel){sel.innerHTML='';var first=document.createElement('option');first.selected=true;first.textContent='")
            .Append(nm).Append("';sel.appendChild(first);");
        foreach (var st in flows ?? Enumerable.Empty<StateDto>())
        {
            var id = JsEscape(st?.Id ?? "");
            var txt = JsEscape(st?.Name ?? "");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            js.Append("var o=document.createElement('option');o.value='").Append(id).Append("';o.textContent='").Append(txt)
              .Append("';sel.appendChild(o);");
        }

        js.Append("sel.selectedIndex=0;}}catch(e){}");
        await DetailsWeb.CoreWebView2.ExecuteScriptAsync(js.ToString());
    }

    private string BuildHtmlFromTemplate(string tpl)
    {
        var stateType = (Details.StateType ?? "").Trim().ToLowerInvariant();
        var stateNameRaw = (Details.StateName ?? "").Trim().ToLowerInvariant();
        var stateCls = "state-pending";
        if (stateType.Contains("done") || stateNameRaw.Contains("完成"))
        {
            stateCls = "state-done";
        }
        else if (stateType.Contains("clos") || stateNameRaw.Contains("关闭") || stateNameRaw.Contains("拒绝"))
        {
            stateCls = "state-closed";
        }
        else if (stateNameRaw.Contains("可测试"))
        {
            stateCls = "state-testable";
        }
        else if (stateNameRaw.Contains("测试中"))
        {
            stateCls = "state-testing";
        }
        else if (stateType.Contains("progress") || stateType.Contains("in_progress") || stateNameRaw.Contains("进行中") ||
                 stateNameRaw.Contains("开发中") || stateNameRaw.Contains("处理中"))
        {
            stateCls = "state-inprogress";
        }

        var startText = FormatDate(Details.StartAt);
        var endText = FormatDate(Details.EndAt);
        var severityZh = MapSeverityText(Details.SeverityName);
        var storyPointsText = Math.Abs(Details.StoryPoints) < 0.000001 ? "-" : Details.StoryPoints.ToString("0.##");
        var storyPointsSumText = Math.Abs(Details.StoryPointsSummary) < 0.000001 ? "-" : Details.StoryPointsSummary.ToString("0.##");
        var tagsHtml = BuildTagsHtml(Details.Tags);
        var descriptionHtml = string.IsNullOrWhiteSpace(Details.DescriptionHtml) ? "<div>-</div>" : NormalizeImages(Details.DescriptionHtml);
        var sketchHtml = string.IsNullOrWhiteSpace(Details.SketchHtml) ? "<div>-</div>" : NormalizeImages(Details.SketchHtml);
        var commentsHtml = BuildCommentsHtml(Details.Comments);
        var propertiesHtml = BuildPropertiesHtml(Details.Properties);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{{Identifier}}"] = HtmlEscape(Details.Identifier),
            ["{{Title}}"] = HtmlEscape(Details.Title),
            ["{{ParentCrumbHtml}}"] = BuildCrumbHtml() ?? "",
            ["{{AssigneeName}}"] = DashText(Details.AssigneeName),
            ["{{StateClass}}"] = stateCls,
            ["{{StateName}}"] = HtmlEscape(Details.StateName),
            ["{{StartAt}}"] = startText,
            ["{{EndAt}}"] = endText,
            ["{{PriorityName}}"] = HtmlEscape(Details.PriorityName),
            ["{{SeverityText}}"] = HtmlEscape(severityZh),
            ["{{StoryPoints}}"] = storyPointsText,
            ["{{StoryPointsInput}}"] = Math.Abs(Details.StoryPoints) < 0.000001 ? "0" : Details.StoryPoints.ToString("0.##"),
            ["{{WorkItemId}}"] = HtmlEscape(Details.Id),
            ["{{ProductName}}"] = DashText(Details.ProductName),
            ["{{DefectCategory}}"] = DashText(Details.DefectCategory),
            ["{{ReproduceVersion}}"] = DashText(Details.ReproduceVersion),
            ["{{ReproduceProbability}}"] = DashText(Details.ReproduceProbability),
            ["{{StoryPointsSum}}"] = storyPointsSumText,
            ["{{TagsHtml}}"] = tagsHtml,
            ["{{DescriptionHtml}}"] = descriptionHtml,
            ["{{SketchHtml}}"] = sketchHtml,
            ["{{CommentsHtml}}"] = commentsHtml,
            ["{{PropertiesHtml}}"] = propertiesHtml,
            ["{{ChildrenTabStyle}}"] = (Details.ChildrenCount > 0) ? "" : "display:none",
            ["{{ChildrenTabText}}"] = (Details.ChildrenCount > 0) ? ("子工作项 " + Details.ChildrenCount) : "子工作项",
        };
        return ReplaceTokens(tpl, dict);
    }

    private string BuildCrumbHtml()
    {
        try
        {
            var pid = (Details.ParentId ?? "").Trim();
            var ptitle = (Details.ParentTitle ?? Details.ParentIdentifier ?? "").Trim();
            var curUrl = (Details.HtmlUrl ?? "").Trim();
            var cur = string.IsNullOrWhiteSpace(curUrl)
                          ? $"<span class=\"crumb-current\">{HtmlEscape(Details.Identifier)}</span>"
                          : $"<a class=\"crumb-current crumb-link\" target=\"_blank\" rel=\"noopener\" href=\"{HtmlEscape(curUrl)}\">{HtmlEscape(Details.Identifier)}</a>";
            if (string.IsNullOrWhiteSpace(pid) || string.IsNullOrWhiteSpace(ptitle))
            {
                return $"<div class=\"crumb\">{cur}</div>";
            }
            var link =
                $"<a class=\"crumb-link\" href=\"pm://workitem/{System.Net.WebUtility.HtmlEncode(pid)}\" title=\"{HtmlEscape(ptitle)}\">{HtmlEscape(ptitle)}</a>";
            return $"<div class=\"crumb\"><span class=\"crumb-parent\">{link}</span><span class=\"crumb-sep\">/</span>{cur}</div>";
        }
        catch
        {
            return null;
        }
    }

    private string BuildCommentsHtml(List<WorkItemComment> comments)
    {
        var list = comments ?? new List<WorkItemComment>();
        if (list.Count == 0)
        {
            return "<div>-</div>";
        }

        var sb = new StringBuilder();
        foreach (var c in list)
        {
            var tm = FormatFriendlyTime(c.CreatedAt);
            var nm = (c.AuthorName ?? "").Trim();
            var initial = string.IsNullOrWhiteSpace(nm) ? "-" : nm.Substring(0, Math.Min(1, nm.Length));
            var avatarHtml = string.IsNullOrWhiteSpace(c.AuthorAvatar)
                                 ? $"<span class=\"ant-avatar ant-avatar-circle\"><span class=\"ant-avatar-string\">{HtmlEscape(initial)}</span></span>"
                                 : $"<span class=\"ant-avatar ant-avatar-circle ant-avatar-image\"><img src=\"{HtmlEscape(c.AuthorAvatar)}\"/></span>";
            sb.Append("<div class=\"comment-item\">");
            sb.Append("<div class=\"comment-row\">");
            sb.Append($"<div class=\"comment-avatar\">{avatarHtml}</div>");
            sb.Append("<div class=\"comment-main\">");
            sb.Append($"<div class=\"comment-meta\"><span class=\"comment-author\">{HtmlEscape(nm)}</span> <span class=\"comment-time\">{tm}</span></div>");
            var content = string.IsNullOrWhiteSpace(c.ContentHtml) ? "-" : NormalizeImages(c.ContentHtml);
            sb.Append($"<div class=\"comment-body\">{content}</div>");
            sb.Append("</div></div>");
            sb.Append("</div>");
        }

        return sb.ToString();
    }

    private static string FirstNonEmpty(params string[] arr)
    {
        try
        {
            foreach (var s in arr ?? Array.Empty<string>())
            {
                var t = (s ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(t))
                {
                    return s;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private string BuildAttachmentsHtmlFromUploads(List<Newtonsoft.Json.Linq.JObject> uploads)
    {
        try
        {
            var list = uploads ?? new List<Newtonsoft.Json.Linq.JObject>();
            if (list.Count == 0)
            {
                return "";
            }
            var sb = new StringBuilder();
            foreach (var a in list)
            {
                if (a == null) { continue; }
                var url = a.Value<string>("download_url");
                if (string.IsNullOrWhiteSpace(url)) { url = a.Value<string>("url"); }
                if (string.IsNullOrWhiteSpace(url)) { continue; }
                var title = FirstNonEmpty(a.Value<string>("title"), a.Value<string>("name"), a.Value<string>("filename"));
                var fileType = FirstNonEmpty(a.Value<string>("file_type"), a.Value<string>("content_type"));
                var tt = string.IsNullOrWhiteSpace(title) ? url : title;
                var u = AppendAccessTokenQueryIfNeeded(url, accessToken);
                var typeLower = (fileType ?? "").Trim().ToLowerInvariant();
                var isImg = (!string.IsNullOrWhiteSpace(typeLower) && typeLower.StartsWith("image/")) || GuessAttachmentTypeByUrl(u) == "image";
                if (isImg)
                {
                    sb.Append($"<div class=\"comment-attachment\"><img loading=\"lazy\" src=\"{System.Net.WebUtility.HtmlEncode(u)}\" alt=\"{System.Net.WebUtility.HtmlEncode(tt)}\"/></div>");
                }
                else
                {
                    sb.Append($"<div class=\"comment-attachment\"><a href=\"{System.Net.WebUtility.HtmlEncode(u)}\" target=\"_blank\" rel=\"noopener\">{System.Net.WebUtility.HtmlEncode(tt)}</a></div>");
                }
            }
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static DateTime? ReadDateTimeFromSecondsLocal(Newtonsoft.Json.Linq.JToken t)
    {
        try
        {
            if (t == null) return null;
            var s = t.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (double.TryParse(s, out var dv))
            {
                var sec = (long)Math.Round(dv);
                return DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime;
            }
            if (long.TryParse(s, out var lv))
            {
                return DateTimeOffset.FromUnixTimeSeconds(lv).LocalDateTime;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private string BuildSingleCommentHtml(string contentHtml, List<Newtonsoft.Json.Linq.JObject> uploads, string authorName, string authorAvatar, DateTime? createdAt)
    {
        var tm = FormatFriendlyTime(createdAt ?? DateTime.Now);
        var nm = (authorName ?? "").Trim();
        var initial = string.IsNullOrWhiteSpace(nm) ? "-" : nm.Substring(0, Math.Min(1, nm.Length));
        var avatarUrl = string.IsNullOrWhiteSpace(authorAvatar) ? null : AppendAccessTokenQueryIfNeeded(authorAvatar, accessToken);
        var avatarHtml = string.IsNullOrWhiteSpace(avatarUrl)
                             ? $"<span class=\"ant-avatar ant-avatar-circle\"><span class=\"ant-avatar-string\">{HtmlEscape(initial)}</span></span>"
                             : $"<span class=\"ant-avatar ant-avatar-circle ant-avatar-image\"><img src=\"{HtmlEscape(avatarUrl)}\"/></span>";
        var body = NormalizeImages(contentHtml ?? "");
        var attachmentsHtml = BuildAttachmentsHtmlFromUploads(uploads);
        var finalContent = string.IsNullOrWhiteSpace(attachmentsHtml) ? body : (string.IsNullOrWhiteSpace(body) ? attachmentsHtml : body + attachmentsHtml);
        var sb = new StringBuilder();
        sb.Append("<div class=\"comment-item\">");
        sb.Append("<div class=\"comment-row\">");
        sb.Append($"<div class=\"comment-avatar\">{avatarHtml}</div>");
        sb.Append("<div class=\"comment-main\">");
        sb.Append($"<div class=\"comment-meta\"><span class=\"comment-author\">{HtmlEscape(nm)}</span> <span class=\"comment-time\">{tm}</span></div>");
        sb.Append($"<div class=\"comment-body\">{finalContent}</div>");
        sb.Append("</div></div>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private void TryEnsureLatestTemplateExtracted(string embeddedText, string fileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(embeddedText) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            var data = new DataPersistenceService();
            var targetDir = Path.Combine(data.GetDataFolderPath(), "Views", "Templates");
            var targetPath = Path.Combine(targetDir, fileName);
            Directory.CreateDirectory(targetDir);
            var verEmbedded = ExtractTemplateVersion(embeddedText);
            var verLocal = ExtractTemplateVersionFromFile(targetPath);
            var need = string.IsNullOrWhiteSpace(verLocal) || (CompareVersion(verEmbedded, verLocal) > 0) || !File.Exists(targetPath);
            if (need)
            {
                File.WriteAllText(targetPath, embeddedText, Encoding.UTF8);
            }
        }
        catch
        {
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
            var result = ImgTagRegex.Replace(html,
                                             m =>
                                             {
                                                 var tag = m.Value;
                                                 var originUrl = ExtractAttr(tag, "originUrl");
                                                 var src = ExtractAttr(tag, "src");
                                                 var use = string.IsNullOrWhiteSpace(src) ? originUrl : src;
                                                 use = System.Net.WebUtility.HtmlDecode(use ?? "");
                                                 var withToken = AppendPublicImageTokenIfNeeded(use, Details?.PublicImageToken);
                                                 var finalUse = string.IsNullOrWhiteSpace(withToken) ? use : withToken;
                                                 finalUse = AppendAccessTokenQueryIfNeeded(finalUse, accessToken);
                                                 if (string.IsNullOrWhiteSpace(use))
                                                 {
                                                     return tag;
                                                 }

                                                 var encodedUse = System.Net.WebUtility.HtmlEncode(finalUse ?? "");
                                                 var updated = tag;
                                                 if (string.IsNullOrWhiteSpace(src))
                                                 {
                                                     updated = updated.Replace("<img", $"<img src=\"{encodedUse}\"");
                                                 }
                                                 else
                                                 {
                                                     updated = Regex.Replace(updated,
                                                                             "\\bsrc\\s*=\\s*\"([^\"]+)\"",
                                                                             $"src=\"{encodedUse}\"",
                                                                             RegexOptions.IgnoreCase);
                                                     updated = Regex.Replace(updated,
                                                                             "\\bsrc\\s*=\\s*'([^']+)'",
                                                                             $"src='{encodedUse}'",
                                                                             RegexOptions.IgnoreCase);
                                                     updated = Regex.Replace(updated,
                                                                             "\\bsrc\\s*=\\s*([^\\s>]+)",
                                                                             $"src=\"{encodedUse}\"",
                                                                             RegexOptions.IgnoreCase);
                                                 }

                                                 var host = "";
                                                 try
                                                 {
                                                     host = new Uri(finalUse).Host.ToLowerInvariant();
                                                 }
                                                 catch
                                                 {
                                                 }

                                                 var isAtlasPublic = host.Contains("atlas.pingcode.com") ||
                                                                     finalUse.ToLowerInvariant().Contains("/files/public/");
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
            result = AnchorTagRegex.Replace(result,
                                            m =>
                                            {
                                                var tag = m.Value;
                                                var href = ExtractAttr(tag, "href");
                                                var text = m.Groups[1].Value;
                                                var decodedHref = System.Net.WebUtility.HtmlDecode(href ?? "");
                                                var withToken = AppendPublicImageTokenIfNeeded(decodedHref, Details?.PublicImageToken);
                                                var finalUse = string.IsNullOrWhiteSpace(withToken) ? decodedHref : withToken;
                                                finalUse = AppendAccessTokenQueryIfNeeded(finalUse, accessToken);
                                                if (string.IsNullOrWhiteSpace(finalUse))
                                                {
                                                    return tag;
                                                }

                                                if (!LooksLikeImageUrl(finalUse))
                                                {
                                                    return tag;
                                                }

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

    private void InferPublicImageToken()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Details?.PublicImageToken))
            {
                return;
            }

            var t = TryExtractTokenFromHtml(Details?.DescriptionHtml);
            if (string.IsNullOrWhiteSpace(t))
            {
                t = TryExtractTokenFromHtml(Details?.SketchHtml);
            }

            if (string.IsNullOrWhiteSpace(t) && (Details?.Comments != null))
            {
                foreach (var c in Details.Comments)
                {
                    t = TryExtractTokenFromHtml(c?.ContentHtml);
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(t))
            {
                Details.PublicImageToken = t;
            }
        }
        catch
        {
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private class ProcessedComment
    {
        public string Html { get; }
        public List<string> AttachmentUrls { get; }
        public ProcessedComment(string html, List<string> attachmentUrls)
        {
            Html = html ?? "";
            AttachmentUrls = attachmentUrls ?? new List<string>();
        }
    }

    private static bool TryParseDataUrl(string src, out string mime, out byte[] bytes)
    {
        mime = null;
        bytes = null;
        try
        {
            var s = (src ?? "").Trim();
            if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var idx = s.IndexOf(",");
            if (idx <= 0)
            {
                return false;
            }
            var meta = s.Substring(0, idx);
            var payload = s.Substring(idx + 1);
            var m = Regex.Match(meta, @"^data:([^;]+);base64", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return false;
            }
            mime = m.Groups[1].Value;
            bytes = Convert.FromBase64String(payload);
            return (bytes != null) && (bytes.Length > 0);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadUrlFromAttachmentToken(Newtonsoft.Json.Linq.JToken x)
    {
        try
        {
            if (x == null) return null;
            if (x.Type == Newtonsoft.Json.Linq.JTokenType.String) return x.ToString();
            var xo = x as Newtonsoft.Json.Linq.JObject;
            if (xo != null)
            {
                var u = xo.Value<string>("url") ?? xo.Value<string>("download_url") ?? xo.Value<string>("src");
                if (!string.IsNullOrWhiteSpace(u)) return u;
            }
            var jv = x as Newtonsoft.Json.Linq.JValue;
            if (jv != null)
            {
                var obj = jv.Value;
                return obj?.ToString();
            }
            return x.ToString();
        }
        catch
        {
            return null;
        }
    }

    private void RememberUploadedAttachment(Newtonsoft.Json.Linq.JObject uploaded)
    {
        try
        {
            if (uploaded == null) return;
            var urls = new List<string>();
            var u1 = uploaded.Value<string>("url");
            var u2 = uploaded.Value<string>("download_url");
            if (!string.IsNullOrWhiteSpace(u1)) urls.Add(u1);
            if (!string.IsNullOrWhiteSpace(u2)) urls.Add(u2);
            foreach (var u in urls)
            {
                uploadedAttachmentMap[u] = uploaded;
                var safe = AppendAccessTokenQueryIfNeeded(u, accessToken);
                uploadedAttachmentMap[safe] = uploaded;
            }
        }
        catch
        {
        }
    }

    private static string GuessAttachmentTypeByUrl(string url)
    {
        try
        {
            var u = (url ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(u)) return "file";
            if (u.EndsWith(".png") || u.EndsWith(".jpg") || u.EndsWith(".jpeg") || u.EndsWith(".gif") ||
                u.EndsWith(".bmp") || u.EndsWith(".webp") || u.EndsWith(".svg") || u.Contains("file_type=image") || u.Contains("content_type=image"))
            {
                return "image";
            }
            return "file";
        }
        catch
        {
            return "file";
        }
    }

    private async Task<ProcessedComment> ProcessCommentHtmlAsync(string html, string workItemId)
    {
        try
        {
            var h = html ?? "";
            var attachments = new List<string>();
            var matches = ImgTagRegex.Matches(h);
            if ((matches != null) && (matches.Count > 0))
            {
                foreach (Match m in matches)
                {
                    var tag = m.Value ?? "";
                    var src = ExtractAttr(tag, "src");
                    if (string.IsNullOrWhiteSpace(src))
                    {
                        continue;
                    }
                    if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        attachments.Add(src);
                        h = h.Replace(tag, "");
                    }
                }
            }
            return new ProcessedComment(h, attachments);
        }
        catch
        {
            return new ProcessedComment(html ?? "", new List<string>());
        }
    }

    private async Task<Newtonsoft.Json.Linq.JObject> UploadAttachmentViaApiAsync(byte[] data, string fileName, string contentType, string workItemId = null, string commentId = null)
    {
        try
        {
            if ((data == null) || (data.Length == 0))
            {
                return null;
            }
            var tk = await api.GetAccessTokenAsync();
            var url = "https://open.pingcode.com/v1/attachments";
            var qs = new List<string>();
            if (!string.IsNullOrWhiteSpace(workItemId))
            {
                qs.Add("principal_type=work_item");
                qs.Add($"principal_id={Uri.EscapeDataString(workItemId)}");
            }
            if (!string.IsNullOrWhiteSpace(commentId))
            {
                qs.Add($"comment_id={Uri.EscapeDataString(commentId)}");
            }
            if (qs.Count > 0)
            {
                url = $"{url}?{string.Join("&", qs)}";
            }
            using var http = new System.Net.Http.HttpClient();
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url);
            var mp = new System.Net.Http.MultipartFormDataContent();
            var fc = new System.Net.Http.ByteArrayContent(data);
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }
            var name = string.IsNullOrWhiteSpace(fileName) ? $"image_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png" : fileName;
            mp.Add(fc, "file", name);
            // parameters already in query; do not duplicate in form
            req.Content = mp;
            if (!string.IsNullOrWhiteSpace(tk))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tk);
            }
            using var resp = await http.SendAsync(req);
            var txt = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            try
            {
                return string.IsNullOrWhiteSpace(txt) ? new Newtonsoft.Json.Linq.JObject() : Newtonsoft.Json.Linq.JObject.Parse(txt);
            }
            catch
            {
                return new Newtonsoft.Json.Linq.JObject();
            }
        }
        catch
        {
            return null;
        }
    }

    private Newtonsoft.Json.Linq.JArray BuildStructuredContentFromText(string text)
    {
        var arr = new Newtonsoft.Json.Linq.JArray();
        var para = new Newtonsoft.Json.Linq.JObject();
        para["type"] = "paragraph";
        para["key"] = Guid.NewGuid().ToString("N").Substring(0, 5);
        var children = new Newtonsoft.Json.Linq.JArray();
        var t = new Newtonsoft.Json.Linq.JObject();
        t["text"] = text ?? "";
        children.Add(t);
        para["children"] = children;
        arr.Add(para);
        return arr;
    }

    private string RenderHtmlFromStructuredContent(Newtonsoft.Json.Linq.JArray content)
    {
        if (content == null) return "";
        var sb = new StringBuilder();
        foreach (var block in content)
        {
            if (block is Newtonsoft.Json.Linq.JObject obj)
            {
                var type = obj.Value<string>("type");
                if (string.Equals(type, "paragraph", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("<p>");
                    var children = obj["children"] as Newtonsoft.Json.Linq.JArray;
                    if (children != null)
                    {
                        foreach (var child in children)
                        {
                            if (child is Newtonsoft.Json.Linq.JObject cObj)
                            {
                                var cType = cObj.Value<string>("type");
                                if (string.Equals(cType, "mention", StringComparison.OrdinalIgnoreCase))
                                {
                                    var name = cObj["data"]?.Value<string>("name") ?? "unknown";
                                    sb.Append($"<span class=\"mention\">@{System.Net.WebUtility.HtmlEncode(name)}</span>");
                                }
                                else
                                {
                                    var txt = cObj.Value<string>("text") ?? "";
                                    sb.Append(System.Net.WebUtility.HtmlEncode(txt).Replace("\n", "<br>"));
                                }
                            }
                        }
                    }
                    sb.Append("</p>");
                }
            }
        }
        return sb.ToString();
    }

    private bool ContainsMention(Newtonsoft.Json.Linq.JArray content)
    {
        if (content == null) return false;
        var s = content.ToString();
        return s.Contains("\"type\": \"mention\"") || s.Contains("\"type\":\"mention\"");
    }
}
