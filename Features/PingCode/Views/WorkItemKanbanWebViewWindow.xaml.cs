using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using PackageManager.Services;
using PackageManager.Services.PingCode;
using PackageManager.Services.PingCode.Dto;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Views.KanBan;

/// <summary>
/// 迭代看板窗口（WebView2 渲染版）：WPF 壳承载工具栏与刷新调度，看板主体由内嵌浏览器渲染。
/// 整页滚动、sticky 列头、悬停与拖拽均为浏览器原生能力；WPF 版看板保留为兜底（经典视图按钮）。
/// </summary>
public partial class WorkItemKanbanWebViewWindow : Window, INotifyPropertyChanged
{
    private readonly PingCodeApiService api;
    private readonly string iterationId;
    private readonly DispatcherTimer refreshTimer;
    private readonly TimeSpan baseRefreshInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan fastRefreshInterval = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, StatePlanInfo> planCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<StateDto>> flowsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string boardHtml;

    private List<WorkItemInfo> allItems = new();
    private bool refreshing;
    private bool handlingMove;
    private DateTime fastRefreshUntil = DateTime.MinValue;
    private string lastItemsSignature;
    private Entity selectedMember;
    private string selectedParticipant;
    private CoreWebView2 core;
    private bool pageReady;

    /// <summary>
    /// 初始化 <see cref="WorkItemKanbanWebViewWindow"/> 并准备模板与刷新调度。
    /// </summary>
    /// <param name="iterationId">迭代唯一标识。</param>
    /// <param name="members">可选的初始成员列表（当前以工作项数据重建，保留参数兼容旧入口）。</param>
    /// <param name="selectedMember">初始选中的成员。</param>
    public WorkItemKanbanWebViewWindow(string iterationId, IEnumerable<Entity> members, Entity selectedMember)
    {
        InitializeComponent();
        api = new PingCodeApiService();
        this.iterationId = iterationId;
        WindowState = WindowState.Maximized;
        DataContext = this;
        boardHtml = LoadBoardTemplate();
        Loaded += async (s, e) => await InitializeAsync();
        refreshTimer = new DispatcherTimer { Interval = baseRefreshInterval };
        refreshTimer.Tick += async (s, e) => await RefreshWorkItemsAsync();
        Closed += (s, e) => refreshTimer.Stop();
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>获取看板成员（筛选）列表。</summary>
    public ObservableCollection<Entity> Members { get; } = new();

    /// <summary>获取参与人（筛选）列表。</summary>
    public ObservableCollection<string> Participants { get; } = new();

    /// <summary>获取或设置当前选中的成员筛选。</summary>
    public Entity SelectedMember
    {
        get => selectedMember;
        set
        {
            if (!Equals(selectedMember, value))
            {
                selectedMember = value;
                OnPropertyChanged();
                _ = PushBoardAsync();
            }
        }
    }

    /// <summary>获取或设置当前选中的参与人筛选。</summary>
    public string SelectedParticipant
    {
        get => selectedParticipant;
        set
        {
            if (!string.Equals(selectedParticipant, value, StringComparison.Ordinal))
            {
                selectedParticipant = value;
                OnPropertyChanged();
                _ = PushBoardAsync();
            }
        }
    }

    /// <summary>触发属性变更通知。</summary>
    /// <param name="name">属性名，默认为调用方成员名。</param>
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private async Task InitializeAsync()
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            LoggingService.LogInfo($"[看板桥接] 模板加载 {boardHtml?.Length ?? 0} 字符");
            await InitializeWebViewAsync();
            LoggingService.LogInfo("[看板桥接] WebView2 初始化完成，开始导航");
            await LoadWorkItemsAsync();
            refreshTimer.Start();
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "[看板桥接] 初始化失败");
            MessageBox.Show("初始化看板失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async Task InitializeWebViewAsync()
    {
        var userDataFolder = Path.Combine(new DataPersistenceService().GetDataFolderPath(), "WebView2Cache");
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await BoardWeb.EnsureCoreWebView2Async(env);
        core = BoardWeb.CoreWebView2;
        core.Settings.IsWebMessageEnabled = true;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.NavigationCompleted += (s, e) =>
            LoggingService.LogInfo($"[看板桥接] 页面导航完成 IsSuccess={e.IsSuccess} HttpStatus={e.HttpStatusCode}");
        core.NavigateToString(boardHtml);
    }

    private async void Core_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json = null;
        try
        {
            // 页面 postMessage 对象 → WebMessageAsJson；字符串 → 需 TryGetWebMessageAsString 取回
            json = e.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(json))
            {
                json = e.TryGetWebMessageAsString();
            }

            LoggingService.LogInfo($"[看板桥接] 收到页面消息: {(string.IsNullOrWhiteSpace(json) ? "<空>" : json.Substring(0, Math.Min(120, json.Length)))}");

            var msg = string.IsNullOrWhiteSpace(json) ? null : JsonConvert.DeserializeObject<BoardWebMessage>(json);
            if (msg == null)
            {
                LoggingService.LogWarning("[看板桥接] 消息反序列化为空，已忽略");
                return;
            }

            switch (msg.Type)
            {
                case "ready":
                    pageReady = true;
                    LoggingService.LogInfo($"[看板桥接] 页面就绪，推送首版数据（数据已就绪: {allItems.Count} 项）");
                    if (allItems.Count > 0)
                    {
                        await PushBoardAsync();
                    }

                    // 数据未到时由 LoadWorkItemsAsync 到达后推送；两者时序无论先后都能触发渲染
                    break;
                case "openDetail":
                    await OpenDetailAsync(msg.Id);
                    break;
                case "moveItem":
                    await MoveItemAsync(msg.Id, msg.To);
                    break;
                case "renderError":
                    LoggingService.LogWarning($"[看板桥接] 页面渲染异常: {msg.To}");
                    break;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, $"[看板桥接] 消息处理异常: {json ?? "<空>"}");
        }
    }

    private async Task LoadWorkItemsAsync()
    {
        try
        {
            allItems = await api.GetIterationWorkItemsAsync(iterationId) ?? new List<WorkItemInfo>();
            LoggingService.LogInfo($"[看板桥接] 工作项拉取完成: {allItems.Count} 项（pageReady={pageReady}）");
            _ = PrefetchProductOptionsAsync();
            RebuildMembersFromItems();
            RebuildParticipantsFromItems();
            await PushBoardAsync();

            // 数据晚于页面就绪到达时的兜底：延迟再推一次（防 ExecuteScriptAsync 在页面
            // 初始化早期静默失败的窗口），重复渲染无害（整页重建，保留滚动位置）
            if (pageReady)
            {
                await Task.Delay(300);
                await PushBoardAsync();
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "[看板桥接] 加载看板数据失败");
            MessageBox.Show("加载看板失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 后台预取所属产品枚举字典，使点击卡片打开详情时命中缓存。
    /// </summary>
    private async Task PrefetchProductOptionsAsync()
    {
        try
        {
            foreach (var pid in allItems.Select(i => i?.ProjectId)
                                        .Where(id => !string.IsNullOrWhiteSpace(id))
                                        .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await api.PrefetchCustomFieldOptionsAsync(pid);
            }
        }
        catch
        {
            // 预取失败无害：详情打开时按需加载
        }
    }

    private async Task RefreshWorkItemsAsync()
    {
        if (refreshing || !IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        refreshing = true;
        try
        {
            var latest = await api.GetIterationWorkItemsAsync(iterationId) ?? new List<WorkItemInfo>();
            var latestSig = ComputeItemsSignature(latest);
            if (string.Equals(latestSig, lastItemsSignature, StringComparison.Ordinal))
            {
                if (DateTime.UtcNow > fastRefreshUntil)
                {
                    refreshTimer.Interval = baseRefreshInterval;
                }

                return;
            }

            lastItemsSignature = latestSig;
            allItems = latest;
            RebuildMembersFromItems();
            RebuildParticipantsFromItems();
            await PushBoardAsync();

            if (DateTime.UtcNow > fastRefreshUntil)
            {
                refreshTimer.Interval = baseRefreshInterval;
            }
        }
        catch
        {
        }
        finally
        {
            refreshing = false;
        }
    }

    private void RebuildMembersFromItems()
    {
        var previous = selectedMember;
        Members.Clear();
        Members.Add(new Entity { Id = "*", Name = "全部" });
        var pointsByPerson = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in allItems)
        {
            var key = !string.IsNullOrWhiteSpace(it.AssigneeId) ? it.AssigneeId.Trim().ToLowerInvariant() : (it.AssigneeName ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            pointsByPerson[key] = (pointsByPerson.TryGetValue(key, out var v) ? v : 0) + it.StoryPoints;
        }

        foreach (var kv in pointsByPerson.Where(kv => kv.Value > 0.0).OrderBy(kv => kv.Key))
        {
            var one = allItems.FirstOrDefault(i => string.Equals((i.AssigneeId ?? "").Trim(), kv.Key, StringComparison.OrdinalIgnoreCase));
            var id = one?.AssigneeId ?? kv.Key;
            var nm = one?.AssigneeName ?? id;
            Members.Add(new Entity { Id = id, Name = nm });
        }

        SelectedMember = previous != null && Members.Any(m => string.Equals(m.Id, previous.Id, StringComparison.OrdinalIgnoreCase))
            ? Members.First(m => string.Equals(m.Id, previous.Id, StringComparison.OrdinalIgnoreCase))
            : Members.FirstOrDefault();
    }

    private void RebuildParticipantsFromItems()
    {
        var previous = selectedParticipant;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in allItems)
        {
            foreach (var nm in it.ParticipantNames ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(nm))
                {
                    set.Add(nm.Trim());
                }
            }

            foreach (var nm in it.WatcherNames ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(nm))
                {
                    set.Add(nm.Trim());
                }
            }
        }

        Participants.Clear();
        Participants.Add("无");
        foreach (var nm in set.OrderBy(x => x))
        {
            Participants.Add(nm);
        }

        SelectedParticipant = previous != null && Participants.Contains(previous) ? previous : Participants.FirstOrDefault();
    }

    private IEnumerable<WorkItemInfo> ApplyCurrentFilter(IEnumerable<WorkItemInfo> items)
    {
        var filtered = items;
        var participantActive = !string.IsNullOrWhiteSpace(SelectedParticipant) &&
                                !string.Equals(SelectedParticipant.Trim(), "无", StringComparison.OrdinalIgnoreCase);
        if (participantActive)
        {
            var target = SelectedParticipant.Trim();
            return filtered.Where(i => (i.ParticipantNames ?? new List<string>()).Any(n => string.Equals((n ?? "").Trim(), target, StringComparison.OrdinalIgnoreCase)) ||
                                       (i.WatcherNames ?? new List<string>()).Any(n => string.Equals((n ?? "").Trim(), target, StringComparison.OrdinalIgnoreCase)));
        }

        if (!((SelectedMember == null) || (SelectedMember.Id == "*") || ((SelectedMember.Name ?? "").Trim() == "全部")))
        {
            var id = (SelectedMember.Id ?? "").Trim().ToLowerInvariant();
            var nm = (SelectedMember.Name ?? "").Trim().ToLowerInvariant();
            filtered = filtered.Where(i =>
            {
                var iid = (i.AssigneeId ?? "").Trim().ToLowerInvariant();
                var inm = (i.AssigneeName ?? "").Trim().ToLowerInvariant();
                return (!string.IsNullOrEmpty(iid) && (iid == id)) || (!string.IsNullOrEmpty(inm) && (inm == nm));
            });
        }

        return filtered;
    }

    /// <summary>
    /// 将筛选后的看板数据以 JSON 推送到页面渲染（列分组与计数在 C# 侧完成）。
    /// </summary>
    private async Task PushBoardAsync()
    {
        if (core == null || !pageReady)
        {
            return;
        }

        var order = new[] { "未开始", "进行中", "可测试", "测试中", "已完成", "已关闭" };
        var filtered = ApplyCurrentFilter(allItems).ToList();
        var titles = new List<string>(order);
        titles.AddRange(filtered.Select(i => (i.StateCategory ?? "").Trim())
                                .Where(c => !string.IsNullOrEmpty(c) && !titles.Contains(c, StringComparer.OrdinalIgnoreCase))
                                .Distinct(StringComparer.OrdinalIgnoreCase));

        var byCategory = filtered.ToLookup(i => (i.StateCategory ?? "").Trim(), StringComparer.OrdinalIgnoreCase);
        var columns = titles.Select(title => new
        {
            title,
            items = byCategory[title].Select(ToBoardItem).ToList(),
        }).ToList();

        var payload = new
        {
            type = "render",
            columns,
        };

        try
        {
            var script = $"window.applyBoard({JsonConvert.SerializeObject(payload, Formatting.None)})";
            await core.ExecuteScriptAsync(script);
            LoggingService.LogInfo($"[看板桥接] 已推送渲染: {filtered.Count} 项 / {columns.Count} 列");
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "[看板桥接] 看板数据推送失败");
        }
    }

    private static object ToBoardItem(WorkItemInfo i)
    {
        return new
        {
            id = i.Id,
            identifier = i.Identifier,
            title = i.Title,
            status = i.Status,
            category = (i.StateCategory ?? "").Trim(),
            assignee = i.AssigneeName,
            avatar = i.AssigneeAvatar,
            priority = i.Priority,
            severity = i.Severity,
            isDefect = string.Equals(i.Type ?? "", "bug", StringComparison.OrdinalIgnoreCase),
            points = i.StoryPoints,
            startText = i.StartAt?.ToString("yy-MM-dd"),
            endText = i.EndAt?.ToString("yy-MM-dd"),
            commentCount = i.CommentCount,
            tags = i.Tags ?? new List<string>(),
        };
    }

    private async Task OpenDetailAsync(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var details = await api.GetWorkItemDetailsAsync(itemId);
            if (details != null)
            {
                var win = new WorkItemDetailsWindow(details, api) { Owner = this };
                win.ShowDialog();
            }
        }
        catch
        {
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async Task MoveItemAsync(string itemId, string targetCategory)
    {
        if (handlingMove || string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(targetCategory))
        {
            return;
        }

        var item = allItems.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (item == null || string.Equals(item.StateCategory ?? "", targetCategory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            handlingMove = true;
            var targetStateId = await ResolveTargetStateIdAsync(item, targetCategory);
            var ok = false;
            if ((targetStateId != null) && !string.IsNullOrWhiteSpace(targetStateId.Value.Item1))
            {
                ok = await api.UpdateWorkItemStateByIdAsync(item.Id, targetStateId.Value.Item1);
            }

            if (ok)
            {
                item.StateCategory = targetCategory;
                item.Status = targetStateId.Value.Item2;
                if (!string.IsNullOrWhiteSpace(targetStateId.Value.Item1))
                {
                    item.StateId = targetStateId.Value.Item1;
                }

                lastItemsSignature = null; // 强制下次刷新重建
                fastRefreshUntil = DateTime.UtcNow.AddSeconds(30);
                refreshTimer.Interval = fastRefreshInterval;
                await PushBoardAsync();
            }
            else
            {
                MessageBox.Show("更新状态失败：未找到符合状态方案与流转规则的目标状态", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                await PushBoardAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("更新状态异常：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            await PushBoardAsync();
        }
        finally
        {
            handlingMove = false;
        }
    }

    private async Task<(string, string)?> ResolveTargetStateIdAsync(WorkItemInfo item, string targetCategory)
    {
        if ((item == null) || string.IsNullOrWhiteSpace(targetCategory))
        {
            return null;
        }

        var type = (item.Type ?? "").Trim();
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var projectId = (item.ProjectId ?? "").Trim();
        var planKey = $"{projectId}|{type}";
        if (!planCache.TryGetValue(planKey, out var plan))
        {
            var plans = await api.GetWorkItemStatePlansAsync(projectId);
            plan = plans.FirstOrDefault(p => string.Equals((p?.WorkItemType ?? "").Trim(), type, StringComparison.OrdinalIgnoreCase));
            if ((plan == null) || string.IsNullOrWhiteSpace(plan.Id))
            {
                return null;
            }

            planCache[planKey] = plan;
        }

        var flowsKey = $"{plan.Id}|{item.StateId}";
        if (!flowsCache.TryGetValue(flowsKey, out var flows))
        {
            flows = await api.GetWorkItemStateFlowsAsync(plan.Id, item.StateId);
            flowsCache[flowsKey] = flows ?? new List<StateDto>();
        }

        if ((flows == null) || (flows.Count == 0))
        {
            return null;
        }

        flows.RemoveAll(x => (x?.Name ?? "").Contains("挂起") || (x?.Name ?? "").Contains("受阻"));
        var candidates = flows.Where(f => string.Equals(MapStateNameToCategory(f?.Name), targetCategory, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var pn in GetPriorityNamesForCategory(targetCategory))
        {
            var m = candidates.FirstOrDefault(f => string.Equals(f?.Name ?? "", pn, StringComparison.OrdinalIgnoreCase));
            if ((m != null) && !string.IsNullOrWhiteSpace(m.Id))
            {
                return (m.Id, m.Name);
            }
        }

        return null;
    }

    private static string MapStateNameToCategory(string name)
    {
        var s = (name ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s))
        {
            return "未开始";
        }

        if (s.Contains("关闭") || s.Contains("已拒绝"))
        {
            return "已关闭";
        }

        if (s.Contains("已完成") || s.Contains("已发布"))
        {
            return "已完成";
        }

        if (s.Contains("测试中"))
        {
            return "测试中";
        }

        if (s.Contains("可测试") || s.Contains("已修复"))
        {
            return "可测试";
        }

        if (s.Contains("重新打开") || s.Contains("进行中") || s.Contains("处理中") || s.Contains("待完善") || s.Contains("开发中") || s.Contains("挂起"))
        {
            return "进行中";
        }

        return "未开始";
    }

    private static List<string> GetPriorityNamesForCategory(string category)
    {
        return (category ?? "").Trim() switch
        {
            "未开始" => new List<string> { "新提交", "打开", "未开始", "新建", "待处理" },
            "进行中" => new List<string> { "待完善", "处理中", "重新打开", "进行中" },
            "可测试" => new List<string> { "已修复", "可测试" },
            "测试中" => new List<string> { "测试中" },
            "已完成" => new List<string> { "已完成", "已发布" },
            "已关闭" => new List<string> { "关闭", "已拒绝" },
            _ => new List<string>(),
        };
    }

    private static string ComputeItemsSignature(IEnumerable<WorkItemInfo> items)
    {
        var list = new List<string>();
        foreach (var it in items ?? Enumerable.Empty<WorkItemInfo>())
        {
            list.Add($"{it?.Id}|{it?.StateCategory}|{it?.Status}|{it?.AssigneeId}|{it?.StoryPoints ?? 0}|{it?.ParticipantNames?.Count ?? 0}|{it?.WatcherNames?.Count ?? 0}");
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join(";", list);
    }

    private static string LoadBoardTemplate()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("kanban-board.html", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(name))
            {
                using var s = asm.GetManifestResourceStream(name);
                using var reader = new StreamReader(s, System.Text.Encoding.UTF8, true);
                return reader.ReadToEnd();
            }
        }
        catch
        {
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var path = Path.Combine(baseDir, "Views", "Templates", "kanban-board.html");
        if (!File.Exists(path))
        {
            var dir = new DirectoryInfo(baseDir);
            for (var i = 0; i < 6 && dir != null; i++)
            {
                var p = Path.Combine(dir.FullName, "Features", "PingCode", "Templates", "kanban-board.html");
                if (File.Exists(p))
                {
                    return File.ReadAllText(p, System.Text.Encoding.UTF8);
                }

                dir = dir.Parent;
            }
        }

        return File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : "<html><body>看板模板缺失</body></html>";
    }

    private void ClassicViewButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new WorkItemKanbanWindow(iterationId, Members.ToList(), SelectedMember);
            win.Show();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开经典视图失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>页面到宿主的消息载体。</summary>
    private sealed class BoardWebMessage
    {
        /// <summary>获取或设置消息类型：ready/openDetail/moveItem/renderError。</summary>
        public string Type { get; set; }

        /// <summary>获取或设置工作项标识。</summary>
        public string Id { get; set; }

        /// <summary>获取或设置附加信息：拖拽目标列（moveItem）或错误消息（renderError）。</summary>
        public string To { get; set; }
    }
}
