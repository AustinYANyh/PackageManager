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
using PackageManager.Features.PingCode.Services;
using PackageManager.Services;
using PackageManager.Services.PingCode;
using PackageManager.Services.PingCode.Dto;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Views.KanBan;

/// <summary>
/// 工作项详情窗口，使用 WebView2 展示工作项的详细信息和评论。
/// </summary>
public partial class WorkItemDetailsWindow : Window, INotifyPropertyChanged
{
    private static readonly Regex ImgTagRegex = new("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnchorTagRegex = new("<a\\b[^>]*>([\\s\\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> TemplateCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly PingCodeApiService api;

    private string accessToken;

    private bool docBridgeInjectedOnDocumentCreated;

    private List<StateDto> availableStates = new();

    private readonly Dictionary<string, Newtonsoft.Json.Linq.JObject> uploadedAttachmentMap = new(StringComparer.OrdinalIgnoreCase);

    private bool childrenLoaded;

    private string pendingWorkItemId;

    private Func<string, Task<WorkItemDetails>> fetchDetailsAsync;

    /// <summary>共享单例（看板/子项跳转/经典看板共用），关闭时隐藏复用，WebView 控件全程只初始化一次。</summary>
    private static WorkItemDetailsWindow sharedInstance;

    private static readonly object SharedSync = new object();

    /// <summary>WebView 控件一次性初始化完成信号；后续打开详情仅重新导航，不再创建控件。</summary>
    private readonly TaskCompletionSource<bool> webViewReadyTcs =
        new TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>等待最终内容页导航完成的信号（每轮导航重建）。</summary>
    private TaskCompletionSource<bool> navigationCompletedTcs =
        new TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>标记当前导航为最终内容页：其 NavigationCompleted 是撤遮罩/放行注入的唯一时机。</summary>
    private bool awaitFinalNavigation;

    /// <summary>切换序列号：每次 ShowWorkItemAsync 自增，旧一轮的异步注入回调据此丢弃，防数据串扰。</summary>
    private int contentSequence;

    private bool forReuse;

    private bool webViewInitSucceeded;

    /// <summary>
    /// 详情预取缓存（悬停触发，pm:// 跳转复用）：条目 3 分钟过期后自动重拉，失败出缓存可重试。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<WorkItemDetails>> detailPrefetchCache =
        new System.Collections.Concurrent.ConcurrentDictionary<string, Task<WorkItemDetails>>(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> detailPrefetchStamp =
        new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取共享详情窗口单例：首次调用在屏幕外完成 WebView 一次性初始化后自动隐藏；
    /// 并发调用共享等待同一次初始化。运行期打开详情不再创建 WebView 控件（规避实测数十秒的控件创建阻塞）。
    /// </summary>
    /// <param name="api">PingCode API 服务实例。</param>
    /// <returns>已就绪的共享详情窗口。</returns>
    public static async Task<WorkItemDetailsWindow> GetSharedAsync(PingCodeApiService api)
    {
        lock (SharedSync)
        {
            if (sharedInstance == null)
            {
                sharedInstance = new WorkItemDetailsWindow(new WorkItemDetails(), api)
                {
                    forReuse = true,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };
                sharedInstance.Left = -32000;
                sharedInstance.Top = -32000;
                sharedInstance.Show();
                // 关键：预热的共享窗先于主窗口创建，会被 WPF 自动认作 Application.MainWindow，
                // 导致 ShutdownMode=OnMainWindowClose 永不触发（关闭主界面进程残留）。
                // 归还身份，让随后的启动主窗口重新认领 MainWindow。
                if (System.Windows.Application.Current?.MainWindow == sharedInstance)
                {
                    System.Windows.Application.Current.MainWindow = null;
                }

                _ = sharedInstance.HideWhenPrewarmAsync();
            }
        }

        await sharedInstance.webViewReadyTcs.Task;
        return sharedInstance;
    }

    /// <summary>
    /// 预热形态的自动隐藏：WebView 就绪后若仍停在屏幕外（未被真实使用）则隐藏并复位坐标。
    /// </summary>
    private async Task HideWhenPrewarmAsync()
    {
        try
        {
            await webViewReadyTcs.Task;
            await Dispatcher.InvokeAsync(() =>
            {
                if (forReuse && Visibility == Visibility.Visible && Left < -10000)
                {
                    Left = double.NaN;
                    Top = double.NaN;
                    Hide();
                    ShowInTaskbar = true;
                    ShowActivated = true;
                    WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
            });
        }
        catch
        {
        }
    }

    /// <summary>
    /// 在共享窗口中展示指定工作项：复用已初始化的 WebView，仅重新导航。
    /// 手动模态：显示期间禁用宿主窗口，关闭（隐藏）时还原，保留原 ShowDialog 的交互语义。
    /// </summary>
    /// <param name="workItemId">工作项唯一标识。</param>
    /// <param name="summary">看板侧摘要（占位头部），可为 null。</param>
    /// <param name="fetcher">详情拉取委托（可携带预取缓存），为 null 时直接走 API。</param>
    public async Task ShowWorkItemAsync(string workItemId, WorkItemInfo summary, Func<string, Task<WorkItemDetails>> fetcher)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
        {
            return;
        }

        await webViewReadyTcs.Task;

        var sequence = ++contentSequence;
        ResetForReuse();
        Details = BuildPlaceholderDetails(workItemId, summary);
        pendingWorkItemId = workItemId;
        fetchDetailsAsync = fetcher;

        // 遮罩先行：不透明白遮住旧内容，杜绝"旧内容→loading→新内容"三段感
        ShowLoading(true);
        awaitFinalNavigation = false;

        // 切换第一拍清空旧内容：导航到轻量加载页，旧工作项内容零泄漏
        try
        {
            DetailsWeb.CoreWebView2?.NavigateToString(BuildLoadingHtml());
        }
        catch
        {
        }

        // 无条件手动居中：预热窗口可能仍处于 Visible 且停在屏幕外，显式计算目标矩形
        CenterOverOwnerOrScreen();

        if (Visibility != Visibility.Visible)
        {
            // 手动模态：禁用宿主，等价于原 ShowDialog 的模态语义
            if (Owner != null)
            {
                Owner.IsEnabled = false;
            }

            Show();
        }

        Activate();
        await InitializeContentAsync(sequence);
    }

    /// <summary>
    /// 显式计算窗口位置：宿主可用时居于宿主中央，否则主屏中央；覆盖屏幕外/NaN 坐标。
    /// </summary>
    private void CenterOverOwnerOrScreen()
    {
        try
        {
            var workArea = System.Windows.SystemParameters.WorkArea;
            double refX, refY, refW, refH;
            if (Owner != null && Owner.IsLoaded && Owner.WindowState != WindowState.Minimized)
            {
                refX = Owner.Left;
                refY = Owner.Top;
                refW = Owner.ActualWidth > 0 ? Owner.ActualWidth : Owner.Width;
                refH = Owner.ActualHeight > 0 ? Owner.ActualHeight : Owner.Height;
            }
            else
            {
                refX = workArea.X;
                refY = workArea.Y;
                refW = workArea.Width;
                refH = workArea.Height;
            }

            if (double.IsNaN(refW) || refW <= 0 || double.IsNaN(refH) || refH <= 0)
            {
                return;
            }

            var width = ActualWidth > 0 ? ActualWidth : (double.IsNaN(Width) ? 1300 : Width);
            var height = ActualHeight > 0 ? ActualHeight : (double.IsNaN(Height) ? 760 : Height);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Max(workArea.X, refX + (refW - width) / 2);
            Top = Math.Max(workArea.Y, refY + (refH - height) / 2);
        }
        catch
        {
            // 定位失败退回默认，不阻塞展示
        }
    }

    /// <summary>
    /// 手动模态收尾：还原宿主窗口可用并激活（隐藏复用时调用）。
    /// </summary>
    private void RestoreOwnerWindow()
    {
        var owner = Owner;
        if (owner != null && !owner.IsEnabled)
        {
            owner.IsEnabled = true;
            try
            {
                owner.Activate();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 用已就绪的详情数据打开共享窗口（经典看板/面包屑跳转等入口）。
    /// </summary>
    /// <param name="details">已拉取的详情数据。</param>
    /// <param name="api">PingCode API 服务实例。</param>
    /// <param name="owner">宿主窗口，可为 null。</param>
    public static async Task ShowDetailsAsync(WorkItemDetails details, PingCodeApiService api, Window owner)
    {
        if (details == null)
        {
            return;
        }

        var win = await GetSharedAsync(api);
        if (owner != null && owner.IsLoaded)
        {
            win.Owner = owner;
        }

        var local = details;
        await win.ShowWorkItemAsync(local.Id, null, _ => Task.FromResult(local));
    }

    /// <summary>
    /// 复用前重置窗口内会话状态（附件映射/子项懒加载标记/令牌/可选状态）。
    /// </summary>
    private void ResetForReuse()
    {
        uploadedAttachmentMap.Clear();
        childrenLoaded = false;
        accessToken = null;
        availableStates = new List<StateDto>();
    }

    /// <summary>
    /// 悬停预取的缓存入口：条目 3 分钟过期自动重拉，并发共享同一在途任务。
    /// </summary>
    /// <param name="workItemId">工作项唯一标识。</param>
    /// <returns>详情任务。</returns>
    private Task<WorkItemDetails> GetDetailsCachedAsync(string workItemId)
    {
        if (detailPrefetchStamp.TryGetValue(workItemId, out var stamp) && (DateTime.UtcNow - stamp).TotalMinutes > 3)
        {
            detailPrefetchCache.TryRemove(workItemId, out _);
        }

        detailPrefetchStamp[workItemId] = DateTime.UtcNow;
        return detailPrefetchCache.GetOrAdd(workItemId, async key =>
        {
            try
            {
                return await api.GetWorkItemDetailsAsync(key);
            }
            catch
            {
                detailPrefetchCache.TryRemove(key, out _);
                throw;
            }
        });
    }

    /// <summary>
    /// 用看板摘要构建占位详情：仅头部/快速信息字段，完整字段待真实详情到达后替换。
    /// </summary>
    /// <param name="workItemId">工作项唯一标识。</param>
    /// <param name="summary">看板摘要。</param>
    /// <returns>占位详情。</returns>
    private static WorkItemDetails BuildPlaceholderDetails(string workItemId, WorkItemInfo summary)
    {
        return new WorkItemDetails
        {
            Id = string.IsNullOrWhiteSpace(summary?.Id) ? workItemId : summary.Id,
            Identifier = summary?.Identifier,
            Title = summary?.Title,
            AssigneeName = summary?.AssigneeName,
            StateName = summary?.Status,
            StateId = summary?.StateId,
            StartAt = summary?.StartAt,
            EndAt = summary?.EndAt,
            ProjectId = summary?.ProjectId,
            Type = summary?.Type,
            PriorityName = summary?.Priority,
            SeverityName = summary?.Severity,
            StoryPoints = summary?.StoryPoints ?? 0,
        };
    }

    /// <summary>
    /// 初始化 <see cref="WorkItemDetailsWindow"/> 的新实例。
    /// </summary>
    /// <param name="details">工作项详情数据，为 null 时使用空对象。</param>
    /// <param name="api">PingCode API 服务实例，为 null 时创建新实例。</param>
    public WorkItemDetailsWindow(WorkItemDetails details, PingCodeApiService api)
    {
        Details = details ?? new WorkItemDetails();
        this.api = api ?? new PingCodeApiService();
        InitializeComponent();
        DataContext = this;
        // Loaded 只做一次性 WebView 初始化；内容由 ShowWorkItemAsync 驱动。
        Loaded += async (s, e) =>
        {
            if (webViewInitSucceeded)
            {
                return;
            }

            var totalWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                ShowLoading(true);
                var envWatch = System.Diagnostics.Stopwatch.StartNew();
                var env = await Infrastructure.WebView2EnvironmentService.GetDetailsEnvironmentAsync();
                envWatch.Stop();
                LoggingService.LogInfo($"[详情桥接] 专用环境就绪 {envWatch.ElapsedMilliseconds}ms");

                var ensureWatch = System.Diagnostics.Stopwatch.StartNew();
                await DetailsWeb.EnsureCoreWebView2Async(env);
                ensureWatch.Stop();
                LoggingService.LogInfo($"[详情桥接] EnsureCoreWebView2 {ensureWatch.ElapsedMilliseconds}ms");

                var core = DetailsWeb.CoreWebView2;
                core.Settings.IsWebMessageEnabled = true;
                await InjectDomReadyBridgeScript(core);
                RegisterCoreEvents(core);
                webViewInitSucceeded = true;
                LoggingService.LogInfo($"[详情桥接] WebView 一次性初始化完成（累计 {totalWatch.ElapsedMilliseconds}ms）");

                if (string.IsNullOrWhiteSpace(pendingWorkItemId))
                {
                    // 启动预热形态：无待展示工作项，仅停在空闲页等待首次使用
                    core.NavigateToString(BuildLoadingHtml());
                }
            }
            catch (Exception initEx)
            {
                LoggingService.LogError(initEx, $"[详情桥接] WebView 初始化失败（累计 {totalWatch.ElapsedMilliseconds}ms）");
                try
                {
                    ShowLoading(false);
                }
                catch
                {
                }
            }
            finally
            {
                webViewReadyTcs.TrySetResult(true);
            }
        };
    }

    /// <summary>
    /// 内容初始化：拉取详情（命中预取缓存）→ 导航完整页面 → 状态/成员并行就绪。
    /// 子项计数只需占位里的 Id，与详情拉取并行。
    /// </summary>
    /// <param name="sequence">本轮切换序列号。</param>
    private async Task InitializeContentAsync(int sequence)
    {
        var totalWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ShowLoading(true);
            var childCountTask = CountChildrenSafeAsync(Details?.Id);

            if (!string.IsNullOrWhiteSpace(pendingWorkItemId))
            {
                var fetchWatch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var fetched = await (fetchDetailsAsync?.Invoke(pendingWorkItemId)
                                         ?? api.GetWorkItemDetailsAsync(pendingWorkItemId));
                    fetchWatch.Stop();
                    LoggingService.LogInfo($"[详情桥接] 详情拉取 {fetchWatch.ElapsedMilliseconds}ms（{(fetched == null ? "null" : "ok")}）");
                    if (sequence != contentSequence)
                    {
                        return;
                    }

                    if (fetched != null)
                    {
                        Details = fetched;
                    }
                }
                catch (Exception fetchEx)
                {
                    fetchWatch.Stop();
                    LoggingService.LogInfo($"[详情桥接] 详情拉取失败 {fetchWatch.ElapsedMilliseconds}ms：{fetchEx.Message}（保留占位继续）");
                }
            }

            InferPublicImageToken();
            await NavigateAndInitAsync(sequence, childCountTask);
            LoggingService.LogInfo($"[详情桥接] 内容就绪（累计 {totalWatch.ElapsedMilliseconds}ms）");
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, $"[详情桥接] 内容初始化失败（{totalWatch.ElapsedMilliseconds}ms）");
            try
            {
                ShowLoading(false);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 安全获取子工作项数量：失败返回 null（保持占位）。
    /// </summary>
    /// <param name="workItemId">工作项唯一标识。</param>
    /// <returns>子项数量；失败为 null。</returns>
    private async Task<int?> CountChildrenSafeAsync(string workItemId)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
        {
            return null;
        }

        try
        {
            return await api.GetChildWorkItemCountAsync(workItemId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 共享窗口关闭请求转为隐藏以复用，同时还原手动模态禁用的宿主窗口；非共享实例按常规关闭。
    /// 应用退出（Dispatcher 关闭中）时不拦截，确保进程能正常结束。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (forReuse && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
            RestoreOwnerWindow();
            return;
        }

        base.OnClosing(e);
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 获取当前显示的工作项详情数据（先开窗模式下先为占位、拉取完成后替换为完整数据）。
    /// </summary>
    public WorkItemDetails Details { get; private set; }

    public string AiActionButtonText => new PingCodeWorkItemPromptService().IsFixWorkItem(Details) ? "AI 修复" : "AI 实现";

    /// <summary>
    /// 触发 <see cref="PropertyChanged"/> 事件。
    /// </summary>
    /// <param name="name">发生更改的属性名称，默认为调用方成员名。</param>
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

    /// <summary>
    /// 执行 AI 实现/AI 修复动作：拉最新详情构建 Prompt 并打开 AI 执行窗口。
    /// </summary>
    private async Task RunAiActionAsync()
    {
        try
        {
            ShowLoading(true);
            var latestDetails = Details;
            if (!string.IsNullOrWhiteSpace(Details?.Id))
            {
                latestDetails = await api.GetWorkItemDetailsAsync(Details.Id) ?? Details;
            }

            var token = await api.GetAccessTokenAsync();
            var promptService = new PingCodeWorkItemPromptService();
            var request = promptService.BuildRequest(latestDetails);
            var window = new PingCodeAiExecutionWindow(request, latestDetails, token)
            {
                Owner = this,
            };
            ShowLoading(false);
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowLoading(false);
            MessageBox.Show("生成 AI 执行 Prompt 失败：" + ex.Message, "PingCode AI 执行", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 执行 AI 拆解动作：拉最新详情与既有子项构建 Prompt 并打开 AI 执行窗口。
    /// </summary>
    private async Task RunAiDecomposeAsync()
    {
        try
        {
            ShowLoading(true);
            var latestDetails = Details;
            if (!string.IsNullOrWhiteSpace(Details?.Id))
            {
                latestDetails = await api.GetWorkItemDetailsAsync(Details.Id) ?? Details;
            }

            var tokenTask = api.GetAccessTokenAsync();
            var childrenTask = GetExistingChildrenSafeAsync(latestDetails?.Id);
            await Task.WhenAll(tokenTask, childrenTask);

            var token = await tokenTask;
            var existingChildren = await childrenTask;
            var promptService = new PingCodeWorkItemPromptService();
            var request = promptService.BuildDecomposeRequest(latestDetails, existingChildren);
            var window = new PingCodeAiExecutionWindow(request, latestDetails, token)
            {
                Owner = this,
            };
            ShowLoading(false);
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowLoading(false);
            MessageBox.Show("生成 AI 拆解 Prompt 失败：" + ex.Message, "PingCode AI 拆解", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<List<WorkItemInfo>> GetExistingChildrenSafeAsync(string parentWorkItemId)
    {
        if (string.IsNullOrWhiteSpace(parentWorkItemId))
        {
            return new List<WorkItemInfo>();
        }

        try
        {
            return await api.GetChildWorkItemsAsync(parentWorkItemId) ?? new List<WorkItemInfo>();
        }
        catch
        {
            return new List<WorkItemInfo>();
        }
    }

    private static bool IsTaskType(string type)
    {
        var text = (type ?? "").Trim().ToLowerInvariant();
        return text.Contains("task") || text.Contains("任务");
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
        // 详情专用环境（独立用户数据目录）：与看板 WebView 共存时避免同目录多控件通道排队
        var env = await Infrastructure.WebView2EnvironmentService.GetDetailsEnvironmentAsync();
        await DetailsWeb.EnsureCoreWebView2Async(env);
        var core = DetailsWeb.CoreWebView2;
        core.Settings.IsWebMessageEnabled = true;
        return core;
    }

    private string BuildDocBridgeScript()
    {
        // 不烘焙工作项 ID：单例复用时消息不带 id，宿主按当前 Details 兜底，避免陈旧 ID 误更新
        var docJs =
            "(function(){try{function pv(v){try{var n=parseFloat(v);if(isNaN(n)||n<0){return 0;}return n;}catch(e){return 0;}}function bind(){try{if(window.__pm_bind_done){return;}window.__pm_bind_done=true;var ip=document.getElementById('spInput');if(ip){ip.addEventListener('blur',function(){try{var val=pv(ip.value);if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'updateStoryPoints',value:val});}}catch(e){}});ip.addEventListener('keydown',function(e){if(e.key==='Enter'){try{var val=pv(ip.value);if(window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'updateStoryPoints',value:val});}}catch(e){}}});}var sel=document.getElementById('stateSelect');if(sel){sel.addEventListener('change',function(){try{var val=sel&&sel.value;if(val&&window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'updateState',stateId:val});}}catch(e){}});} }catch(e){}}document.addEventListener('DOMContentLoaded',function(){try{bind();var has=document.getElementById('stateSelect')||document.getElementById('spInput');if(has&&window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'ready'});}}catch(e){}});document.addEventListener('readystatechange',function(){try{if(document.readyState==='interactive'||document.readyState==='complete'){bind();var has=document.getElementById('stateSelect')||document.getElementById('spInput');if(has&&window.chrome&&window.chrome.webview){window.chrome.webview.postMessage({type:'ready'});}}}catch(e){}});}catch(e){}})();";
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

                    // AI 按钮动作统一拦截（JObject 或字符串包装均适配），复用原 WPF 按钮逻辑
                    var aiSource = token as Newtonsoft.Json.Linq.JObject;
                    if (aiSource == null && token is Newtonsoft.Json.Linq.JValue jvStr && jvStr.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    {
                        var innerParsed = Newtonsoft.Json.Linq.JToken.Parse(jvStr.ToString() ?? "");
                        aiSource = innerParsed as Newtonsoft.Json.Linq.JObject;
                    }

                    if (aiSource != null)
                    {
                        var earlyType = aiSource.Value<string>("type");
                        if (string.Equals(earlyType, "aiAction", StringComparison.OrdinalIgnoreCase))
                        {
                            await RunAiActionAsync();
                            return;
                        }

                        if (string.Equals(earlyType, "aiDecompose", StringComparison.OrdinalIgnoreCase))
                        {
                            await RunAiDecomposeAsync();
                            return;
                        }

                        // 悬停预取：子项行/面包屑父项链接 hover 即后台拉取，点击跳转时命中缓存
                        if (string.Equals(earlyType, "prefetchDetail", StringComparison.OrdinalIgnoreCase))
                        {
                            var prefetchId = aiSource.Value<string>("id");
                            if (!string.IsNullOrWhiteSpace(prefetchId))
                            {
                                _ = GetDetailsCachedAsync(prefetchId);
                            }

                            return;
                        }
                    }

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
                    LoggingService.LogInfo("[详情桥接] 收到页面 ready");
                    // 状态下拉/成员初始化由 NavigateAndInitAsync 统一执行，此处不重复请求
                    try
                    {
                        ShowLoading(false);
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
                                tasks.Add(api.UploadAttachmentViaApiAsync(bytes, name, mime, id, commentId));
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
                LoggingService.LogInfo($"[详情桥接] NavigationCompleted IsSuccess={args.IsSuccess} Status={args.HttpStatusCode} final={awaitFinalNavigation}");
                // 只有最终内容页完成才撤遮罩/放行注入；清屏加载页的完成（含被终止时的 IsSuccess=false）一律忽略
                if (awaitFinalNavigation && args.IsSuccess)
                {
                    awaitFinalNavigation = false;
                    navigationCompletedTcs.TrySetResult(true);
                    ShowLoading(false);
                }

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
                            // 推迟到导航事件之外：NavigationStarting 处理器内嵌套发起导航会被运行时丢弃
                            var localId = id;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _ = ShowWorkItemAsync(localId, null, GetDetailsCachedAsync);
                            }));
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

    private async Task NavigateAndInitAsync(int sequence, Task<int?> childCountTask = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var loadingHtml = BuildLoadingHtml();
        DetailsWeb.CoreWebView2.NavigateToString(loadingHtml);
        accessToken = await api.GetAccessTokenAsync();
        watch.Stop();
        LoggingService.LogInfo($"[详情桥接] token 就绪 {watch.ElapsedMilliseconds}ms");
        var childWatch = System.Diagnostics.Stopwatch.StartNew();
        var cnt = childCountTask != null ? await childCountTask : await CountChildrenSafeAsync(Details.Id);
        childWatch.Stop();
        LoggingService.LogInfo($"[详情桥接] 子项计数 {childWatch.ElapsedMilliseconds}ms（{cnt?.ToString() ?? "失败"}）");
        if (sequence == contentSequence && cnt.HasValue)
        {
            Details.ChildrenCount = cnt.Value;
        }

        var buildWatch = System.Diagnostics.Stopwatch.StartNew();
        var html = await Task.Run(() => BuildHtml());
        buildWatch.Stop();
        LoggingService.LogInfo($"[详情桥接] HTML 构建 {buildWatch.ElapsedMilliseconds}ms（{html?.Length ?? 0} 字符）");
        if (sequence != contentSequence)
        {
            return;
        }

        // 真实页面导航前重建完成信号：避免被加载页的 NavigationCompleted 提前置位
        navigationCompletedTcs = new TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        awaitFinalNavigation = true;
        DetailsWeb.CoreWebView2.NavigateToString(html);
        LoggingService.LogInfo("[详情桥接] 真实页面导航已发出");
        var initWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await navigationCompletedTcs.Task;
        }
        catch
        {
        }

        await Task.WhenAll(InitializeStateDropdownAsync(sequence), InitializeProjectMembersAsync(sequence));
        initWatch.Stop();
        LoggingService.LogInfo($"[详情桥接] 状态/成员并行就绪 {initWatch.ElapsedMilliseconds}ms");
        // 保险带：最终导航若未按预期完成（异常态），此处确保遮罩撤除
        awaitFinalNavigation = false;
        ShowLoading(false);
    }

    private async Task InitializeProjectMembersAsync(int sequence)
    {
        try
        {
            var projectId = (Details?.ProjectId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return;
            }
            var members = await api.GetProjectMembersAsync(projectId);
            if (sequence != contentSequence)
            {
                return;
            }

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
            return "<div class=\"children-empty\">无子工作项</div>";
        }

        // 统计条：数量 / 已完成 / 故事点合计 + 三色进度（绿=已完成 橙=进行中 灰=未开始）
        var total = items.Count;
        var done = items.Count(c =>
        {
            var s = (c?.Status ?? "").Trim();
            return s.Contains("完成") || s.Contains("关闭");
        });
        var inProgress = items.Count(c =>
        {
            var s = (c?.Status ?? "").Trim();
            return s.Contains("进行中") || s.Contains("开发中") || s.Contains("处理中") || s.Contains("测试中") || s.Contains("可测试");
        });
        var pointsSum = items.Sum(c => c?.StoryPoints ?? 0);
        var donePct = Math.Round(done * 100.0 / Math.Max(1, total));
        var inProgPct = Math.Round(inProgress * 100.0 / Math.Max(1, total));

        var sb = new StringBuilder();
        sb.Append("<div class=\"children-toolbar\">");
        sb.Append($"<span class=\"child-stat\">共 <b>{total}</b> 项</span>");
        sb.Append($"<span class=\"child-stat\">已完成 <b>{done}</b></span>");
        sb.Append($"<span class=\"child-stat\">故事点合计 <b>{pointsSum:0.##}</b></span>");
        sb.Append("<div class=\"child-bar-track\">");
        sb.Append($"<div class=\"child-bar-done\" style=\"width:{donePct:0.##}%\"></div>");
        sb.Append($"<div class=\"child-bar-inprogress\" style=\"width:{inProgPct:0.##}%\"></div>");
        sb.Append("</div></div>");

        sb.Append("<table class=\"children-table\"><thead><tr>");
        sb.Append("<th style=\"width:110px\">编号</th>");
        sb.Append("<th>标题</th>");
        sb.Append("<th style=\"width:100px\">状态</th>");
        sb.Append("<th style=\"width:80px\">负责人</th>");
        sb.Append("<th style=\"width:70px\">故事点</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var c in items)
        {
            var id = c?.Id ?? "";
            var identifier = HtmlEscape(c?.Identifier ?? id);
            var title = HtmlEscape(c?.Title ?? "");
            var statusText = DashText(c?.Status);
            var assignee = DashText(c?.AssigneeName);
            var sa = FormatDate(c?.StartAt);
            var ea = FormatDate(c?.EndAt);
            var range = (sa == "-" && ea == "-") ? "" : ((sa == "-" ? "" : sa) + (ea == "-" ? "" : " ~ " + ea)).TrimStart(' ', '~');
            var points = (c?.StoryPoints ?? 0) == 0 ? "-" : c.StoryPoints.ToString("0.##");
            var linkId = System.Net.WebUtility.HtmlEncode(id);
            var s = (c?.Status ?? "").Trim().ToLowerInvariant();
            var cls = "state-pending";
            if (s.Contains("完成")) { cls = "state-done"; }
            else if (s.Contains("关闭")) { cls = "state-closed"; }
            else if (s.Contains("测试中")) { cls = "state-testing"; }
            else if (s.Contains("可测试")) { cls = "state-testable"; }
            else if (s.Contains("进行中") || s.Contains("开发中") || s.Contains("处理中") || s.Contains("progress") || s.Contains("in_progress")) { cls = "state-inprogress"; }
            var sub = string.IsNullOrWhiteSpace(range) ? "" : $"<span class=\"child-sub\">{range}</span>";
            sb.Append("<tr>");
            sb.Append($"<td><a class=\"child-id\" href=\"pm://workitem/{linkId}\">{identifier}</a></td>");
            sb.Append($"<td><a class=\"child-title\" href=\"pm://workitem/{linkId}\">{title}</a>{sub}</td>");
            sb.Append($"<td><span class=\"state-badge {cls}\">{statusText}</span></td>");
            sb.Append($"<td>{assignee}</td>");
            sb.Append($"<td><span class=\"child-points\">{points}</span></td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
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
#pragma warning disable CS0219 // 变量已被赋值，但从未使用过它的值
        var stateCls = "state-pending";
#pragma warning restore CS0219 // 变量已被赋值，但从未使用过它的值
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

    private async Task InitializeStateDropdownAsync(int sequence)
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
            if (sequence != contentSequence)
            {
                return;
            }

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
            ["{{AiButtonsHtml}}"] = BuildAiButtonsHtml(),
        };
        return ReplaceTokens(tpl, dict);
    }

    /// <summary>
    /// 生成 HTML 头部的 AI 操作按钮（与原 WPF 工具栏同源逻辑：特定账户 + 任务类型隐藏拆解按钮）。
    /// </summary>
    /// <returns>按钮 HTML；无权限时为空串。</returns>
    private string BuildAiButtonsHtml()
    {
        if (!UserFeatureAccessService.CanUseAustinOnlyFeatures)
        {
            return string.Empty;
        }

        var actionText = AiActionButtonText;
        // 与原 WPF 工具栏同语义：任务类型才隐藏拆解按钮（需求/缺陷均可拆解）
        var decomposeButton = IsTaskType(Details?.Type)
            ? string.Empty
            : "<button class=\"ai-btn ai-btn-purple\" data-ai=\"decompose\">AI 拆解</button>";
        return $"<button class=\"ai-btn ai-btn-primary\" data-ai=\"action\">{HtmlEscape(actionText)}</button>{decomposeButton}";
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
            if (!string.IsNullOrWhiteSpace(c.RepliedContentHtml) || !string.IsNullOrWhiteSpace(c.RepliedAuthorName))
            {
                var head = string.IsNullOrWhiteSpace(c.RepliedAuthorName) ? "" : HtmlEscape(c.RepliedAuthorName) + "：";
                var body = NormalizeImages(c.RepliedContentHtml ?? "");
                sb.Append("<div class=\"comment-replied\" style=\"background:#F7F7F9;border:1px solid #E5E7EB;border-radius:6px;padding:8px 10px;margin:6px 0;\">");
                if (!string.IsNullOrWhiteSpace(head))
                {
                    sb.Append($"<div class=\"comment-replied-head\" style=\"color:#6B7280;margin-bottom:4px;\">{head}</div>");
                }
                sb.Append($"<div class=\"comment-replied-body\">{body}</div>");
                sb.Append("</div>");
            }
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
