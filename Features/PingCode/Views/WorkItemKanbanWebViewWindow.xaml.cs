using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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
    private DateTime fastRefreshUntil = DateTime.MinValue;
    private string lastItemsSignature;
    private Entity selectedMember;
    private string selectedParticipant;
    private CoreWebView2 core;
    private bool pageReady;

    /// <summary>待写状态意图（卡片 id → 目标列 + 登记时的源状态指纹）。
    /// 连拖同卡覆盖合并（A→B→C 合并为 A→C）；源指纹用于双重执行守卫：
    /// 处理前校验当前 StateId 仍等于登记时的源——不等说明已被处理过（泵交接缝隙的双跑），静默丢弃。</summary>
    private sealed class PendingMove
    {
        /// <summary>获取目标状态分类。</summary>
        public string Target { get; set; }

        /// <summary>获取登记时工作项的 StateId（源状态指纹）。</summary>
        public string SourceStateId { get; set; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingMove> pendingMoves =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>写 API 并发闸（3 路约 3 张/秒排空，高于人工连拖速率）。</summary>
    private readonly SemaphoreSlim writeGate = new SemaphoreSlim(3);

    /// <summary>流转解析串行闸：planCache/flowsCache 非线程安全，解析阶段（缓存命中≈0ms）串行。</summary>
    private readonly SemaphoreSlim resolveGate = new SemaphoreSlim(1);

    /// <summary>意图排空流水线运行标志（单泵）。</summary>
    private int drainRunning;

    /// <summary>停手确认刷新防抖（写全部落库后 500ms 拉一次服务端真相）。</summary>
    private readonly DispatcherTimer confirmRefreshTimer;

    /// <summary>关窗排空完成标志（防 OnClosing 循环）。</summary>
    private bool closeFlushDone;

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
        confirmRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        confirmRefreshTimer.Tick += async (s, e) =>
        {
            confirmRefreshTimer.Stop();
            await RefreshWorkItemsAsync();
        };
        Closed += (s, e) =>
        {
            refreshTimer.Stop();
            confirmRefreshTimer.Stop();
        };
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>获取看板成员（筛选）列表。</summary>
    public ObservableCollection<Entity> Members { get; } = new();

    /// <summary>获取参与人（筛选）列表。</summary>
    public ObservableCollection<string> Participants { get; } = new();

    /// <summary>获取或设置当前选中的成员筛选。</summary>
    /// <summary>筛选下拉重建中：成员/参与者集合 Clear+回填会触发选中属性变化，
    /// 期间抑制自动推送，避免"无筛选全量 → 恢复筛选"的瞬间闪现（实测重建 54 卡的整屏闪烁源）。</summary>
    private bool rebuildingFilters;

    public Entity SelectedMember
    {
        get => selectedMember;
        set
        {
            if (!Equals(selectedMember, value))
            {
                selectedMember = value;
                OnPropertyChanged();
                if (!rebuildingFilters)
                {
                    _ = PushBoardAsync();
                }
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
                if (!rebuildingFilters)
                {
                    _ = PushBoardAsync();
                }
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
            LoggingService.LogDebug($"[看板桥接] 模板加载 {boardHtml?.Length ?? 0} 字符");
            await InitializeWebViewAsync();
            LoggingService.LogDebug("[看板桥接] WebView2 初始化完成，开始导航");
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
        var envWatch = System.Diagnostics.Stopwatch.StartNew();
        // 复用应用启动预热的共享环境
        var env = await Infrastructure.WebView2EnvironmentService.GetEnvironmentAsync();
        envWatch.Stop();
        LoggingService.LogDebug($"[看板性能] 共享环境就绪 {envWatch.ElapsedMilliseconds}ms");
        var ensureWatch = System.Diagnostics.Stopwatch.StartNew();
        await BoardWeb.EnsureCoreWebView2Async(env);
        ensureWatch.Stop();
        LoggingService.LogDebug($"[看板性能] EnsureCoreWebView2 {ensureWatch.ElapsedMilliseconds}ms");
        core = BoardWeb.CoreWebView2;
        core.Settings.IsWebMessageEnabled = true;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.NavigationCompleted += (s, e) =>
            LoggingService.LogDebug($"[看板桥接] 页面导航完成 IsSuccess={e.IsSuccess} HttpStatus={e.HttpStatusCode}");
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

            LoggingService.LogDebug($"[看板桥接] 收到页面消息: {(string.IsNullOrWhiteSpace(json) ? "<空>" : json.Substring(0, Math.Min(120, json.Length)))}");

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
                    LoggingService.LogDebug($"[看板桥接] 页面就绪，推送首版数据（数据已就绪: {allItems.Count} 项）");
                    if (allItems.Count > 0)
                    {
                        await PushBoardAsync();
                    }

                    // 数据未到时由 LoadWorkItemsAsync 到达后推送；两者时序无论先后都能触发渲染
                    break;
                case "openDetail":
                    await OpenDetailAsync(msg.Id);
                    break;
                case "prefetchDetail":
                    _ = PrefetchDetailAsync(msg.Id);
                    break;
                case "moveItem":
                    await MoveItemAsync(msg.Id, msg.To);
                    break;
                case "renderError":
                    LoggingService.LogWarning($"[看板桥接] 页面渲染异常: {msg.To}");
                    break;
                case "renderMode":
                    LoggingService.LogDebug($"[看板桥接] 渲染模式: {msg.Id}（{msg.To}）");
                    break;
                case "renderStats":
                    LoggingService.LogDebug($"[看板桥接] 增量渲染统计: {msg.To}");
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
            LoggingService.LogDebug($"[看板桥接] 工作项拉取完成: {allItems.Count} 项（pageReady={pageReady}）");
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
            detailPrefetchCache.Clear();
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
        rebuildingFilters = true;
        try
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

        // 全部指派人入列（不过滤零故事点）：选中成员不会因故事点归零被静默踢出引发全板突变
        foreach (var kv in pointsByPerson.OrderBy(kv => kv.Key))
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
        finally
        {
            rebuildingFilters = false;
        }
    }

    private void RebuildParticipantsFromItems()
    {
        rebuildingFilters = true;
        try
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
        finally
        {
            rebuildingFilters = false;
        }
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
        // pending 钉住：写入在途的卡片按意图目标列呈现（配合 JS 乐观移动），
        // 处理期间任何来源的推送（含陈旧刷新）都不会把在途卡片弹回旧列
        var effective = filtered
            .Select(i => pendingMoves.TryGetValue(i.Id, out var pending)
                ? (Item: i, Cat: pending.Target?.Trim())
                : (Item: i, Cat: (i.StateCategory ?? "").Trim()))
            .Select(e => string.IsNullOrWhiteSpace(e.Cat) ? (Item: e.Item, Cat: (e.Item.StateCategory ?? "").Trim()) : e)
            .ToList();
        var titles = new List<string>(order);
        // 额外自定义类别排序后追加：推送载荷的列序列跨刷新稳定，杜绝页面侧误判结构变化
        titles.AddRange(effective.Select(e => e.Cat)
                                 .Where(c => !string.IsNullOrEmpty(c) && !titles.Contains(c, StringComparer.OrdinalIgnoreCase))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(c => c, StringComparer.OrdinalIgnoreCase));

        var byCategory = effective.ToLookup(e => e.Cat, StringComparer.OrdinalIgnoreCase);
        // 可达列集合：按 (type, StateId) 去重批量取（缓存命中零请求；未就绪返回 null 不限制拖拽）
        var reachableByItem = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var reachableKeys = new Dictionary<string, Task<HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in effective)
        {
            var key = $"{e.Item.Type}|{e.Item.StateId}";
            if (!reachableKeys.ContainsKey(key))
            {
                reachableKeys[key] = GetReachableCategoriesAsync(e.Item);
            }
        }

        var reachableSets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in reachableKeys)
        {
            string[] arr = null;
            try
            {
                var set = await kv.Value;
                if (set != null)
                {
                    arr = set.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }
            catch
            {
                arr = null;
            }

            reachableSets[kv.Key] = arr;
        }

        foreach (var e in effective)
        {
            var key = $"{e.Item.Type}|{e.Item.StateId}";
            reachableByItem[e.Item.Id] = reachableSets.TryGetValue(key, out var arr) ? arr : null;
        }

        var columns = titles.Select(title => new
        {
            title,
            // 列内顺序按编号确定性排序：与 API 返回顺序（updated_at）解耦，
            // 工作项被更新不再引发列内卡片洗牌（增量渲染零移动）
            items = byCategory[title].OrderBy(e => e.Item.Identifier ?? e.Item.Id ?? "", WorkItemIdentifierComparer.Instance)
                                    .Select(e => ToBoardItem(e.Item, e.Cat, reachableByItem.TryGetValue(e.Item.Id, out var r) ? r : null))
                                    .ToList(),
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
            LoggingService.LogDebug($"[看板桥接] 已推送渲染: {filtered.Count} 项 / {columns.Count} 列");
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "[看板桥接] 看板数据推送失败");
        }
    }

    private static object ToBoardItem(WorkItemInfo i, string effectiveCategory = null, IReadOnlyCollection<string> reachable = null)
    {
        return new
        {
            id = i.Id,
            identifier = i.Identifier,
            title = i.Title,
            status = i.Status,
            category = effectiveCategory ?? (i.StateCategory ?? "").Trim(),
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
            // 拖拽预校验：可达列集合（null/空 = 未知不限制）
            reachable = reachable ?? Array.Empty<string>(),
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
            // 共享单例详情窗：WebView 全程只初始化一次（启动预热），打开仅重新导航，瞬时可用
            var summary = allItems.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
            var win = await WorkItemDetailsWindow.GetSharedAsync(api);
            win.Owner = this;
            await win.ShowWorkItemAsync(itemId, summary, GetDetailsCachedAsync);
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开工作项详情失败");
        }
    }

    /// <summary>
    /// 详情预取缓存：悬停 350ms 触发拉取，点击时在途/已完成的任务直接复用，不重复请求。
    /// 看板数据签名变化（5 秒刷新有更新）时整体清空。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<WorkItemDetails>> detailPrefetchCache =
        new System.Collections.Concurrent.ConcurrentDictionary<string, Task<WorkItemDetails>>(StringComparer.OrdinalIgnoreCase);

    private Task<WorkItemDetails> GetDetailsCachedAsync(string itemId)
    {
        return detailPrefetchCache.GetOrAdd(itemId, FetchDetailForCacheAsync);
    }

    private async Task<WorkItemDetails> FetchDetailForCacheAsync(string itemId)
    {
        try
        {
            return await api.GetWorkItemDetailsAsync(itemId);
        }
        catch
        {
            // 失败不驻留缓存，下次悬停/点击可重试
            detailPrefetchCache.TryRemove(itemId, out _);
            throw;
        }
    }

    private async Task PrefetchDetailAsync(string itemId)
    {
        try
        {
            await GetDetailsCachedAsync(itemId);
        }
        catch
        {
            // 预取失败静默：点击时按需重试
        }
    }

    /// <summary>
    /// 登记拖拽状态意图并启动写入流水线：JS 乐观移动已把卡片放好，此处只负责落库。
    /// 同卡再拖覆盖意图（合并）；持续连拖不限量，队列长度 ≤ 不同卡片数。
    /// </summary>
    /// <param name="itemId">工作项标识。</param>
    /// <param name="targetCategory">目标状态分类。</param>
    private Task MoveItemAsync(string itemId, string targetCategory)
    {
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(targetCategory))
        {
            return Task.CompletedTask;
        }

        // 登记意图：携带登记时的源 StateId 指纹（双重执行守卫的比对基准）
        var sourceStateId = allItems.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase))?.StateId ?? "";
        pendingMoves[itemId] = new PendingMove { Target = targetCategory.Trim(), SourceStateId = sourceStateId?.Trim() };
        UpdateSaveBadge();
        StartDrainPendingMoves();
        return Task.CompletedTask;
    }

    /// <summary>启动意图排空流水线（单泵：已在运行则由泵自身循环消化新意图）。</summary>
    private void StartDrainPendingMoves()
    {
        if (Interlocked.Exchange(ref drainRunning, 1) == 1)
        {
            return;
        }

        _ = DrainPendingMovesAsync();
    }

    /// <summary>
    /// 意图排空流水线：逐张解析（串行，缓存命中≈0）→ 写 API（并发 3）→ 成功后就地更新当前列表。
    /// 失败仅回弹该卡（整板推送，增量渲染零闪烁）；全部落库后防抖触发一次确认刷新。
    /// </summary>
    private async Task DrainPendingMovesAsync()
    {
        try
        {
            while (!pendingMoves.IsEmpty)
            {
                var entries = pendingMoves.ToArray();
                var workers = entries.Select(entry => ProcessPendingMoveAsync(entry.Key)).ToList();
                await Task.WhenAll(workers);
                UpdateSaveBadge();
            }

            // 排空：防抖确认刷新（500ms 内再拖会重置）
            confirmRefreshTimer.Stop();
            confirmRefreshTimer.Start();
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "[看板拖拽] 意图流水线异常");
        }
        finally
        {
            UpdateSaveBadge();
            Interlocked.Exchange(ref drainRunning, 0);
            if (!pendingMoves.IsEmpty)
            {
                // 竞态兜底：复位瞬间又有新意图到达
                StartDrainPendingMoves();
            }
        }
    }

    /// <summary>
    /// 处理单条意图：净零跳过、解析目标状态、写 API、成功后更新当前 allItems 中的项（按 id 重查，防游离）。
    /// </summary>
    /// <param name="itemId">工作项标识。</param>
    private async Task ProcessPendingMoveAsync(string itemId)
    {
        try
        {
            if (!pendingMoves.TryGetValue(itemId, out var pending))
            {
                return;
            }

            var item = allItems.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                RemovePendingIfUnchanged(itemId, pending.Target);
                return;
            }

            // 双重执行守卫：当前源状态与登记时不一致 = 该意图已被处理过（前一次写入已改 StateId）
            // 或期间被其他路径变更——静默丢弃，绝不能按失败处理（否则弹"未找到目标状态"假错误）
            if (!string.Equals(item.StateId ?? "", pending.SourceStateId ?? "", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pending.SourceStateId))
            {
                RemovePendingIfUnchanged(itemId, pending.Target);
                LoggingService.LogDebug($"[看板拖拽] 意图源状态已变化，判定为已处理并丢弃 {itemId} → {pending.Target}");
                return;
            }

            // 净零：目标即当前所在列（含连拖回原位），无需写入
            if (string.Equals(item.StateCategory ?? "", pending.Target, StringComparison.OrdinalIgnoreCase))
            {
                RemovePendingIfUnchanged(itemId, pending.Target);
                return;
            }

            var resolveWatch = System.Diagnostics.Stopwatch.StartNew();
            (string, string)? targetStateId = null;
            await resolveGate.WaitAsync();
            try
            {
                targetStateId = await ResolveTargetStateIdAsync(item, pending.Target);
            }
            finally
            {
                resolveGate.Release();
            }

            resolveWatch.Stop();

            // 对账兜底：解析失败时若工作项实际已在目标列（前一次写入已生效的瞬时竞态），
            // 按成功收尾，不弹错误
            if ((targetStateId == null) || string.IsNullOrWhiteSpace(targetStateId.Value.Item1))
            {
                if (string.Equals(item.StateCategory ?? "", pending.Target, StringComparison.OrdinalIgnoreCase))
                {
                    RemovePendingIfUnchanged(itemId, pending.Target);
                    LoggingService.LogDebug($"[看板拖拽] 解析未命中但已在目标列，按成功收尾 {itemId} → {pending.Target}");
                    return;
                }
            }

            var ok = false;
            if ((targetStateId != null) && !string.IsNullOrWhiteSpace(targetStateId.Value.Item1))
            {
                await writeGate.WaitAsync();
                try
                {
                    ok = await api.UpdateWorkItemStateByIdAsync(item.Id, targetStateId.Value.Item1);
                }
                finally
                {
                    writeGate.Release();
                }
            }

            if (ok)
            {
                // 按 id 在当前 allItems 重查（刷新可能已整体换列表），避免改到游离对象
                var current = allItems.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
                if (current != null)
                {
                    current.StateCategory = pending.Target;
                    current.Status = targetStateId.Value.Item2;
                    if (!string.IsNullOrWhiteSpace(targetStateId.Value.Item1))
                    {
                        current.StateId = targetStateId.Value.Item1;
                    }
                }

                // 仅当意图未被更新覆盖时移除（写入期间用户可能再拖该卡，新意图留给下一轮处理）
                RemovePendingIfUnchanged(itemId, pending.Target);
                LoggingService.LogDebug($"[看板拖拽] 状态写入成功 {itemId} → {pending.Target}（解析 {resolveWatch.ElapsedMilliseconds}ms）");
            }
            else
            {
                // 写失败/目标不可达：清意图并整板推送还原；提示改标题徽标（非模态，不打断连拖）
                RemovePendingIfUnchanged(itemId, pending.Target);
                LoggingService.LogWarning($"[看板拖拽] 状态写入失败 {itemId} → {pending.Target}（解析 {resolveWatch.ElapsedMilliseconds}ms）");
                _ = PushBoardAsync();
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    Title = $"迭代看板 · {item.Identifier} 移动失败已还原";
                    var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                    resetTimer.Tick += (s2, e2) =>
                    {
                        resetTimer.Stop();
                        UpdateSaveBadge();
                    };
                    resetTimer.Start();
                }));
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, $"[看板拖拽] 意图处理异常 {itemId}");
            _ = PushBoardAsync();
        }
    }

    /// <summary>
    /// 按值移除意图：仅当当前登记的目标仍是本次处理的目标时才移除，
    /// 避免误删写入期间用户再拖产生的新意图。
    /// </summary>
    /// <param name="itemId">工作项标识。</param>
    /// <param name="target">本次处理的目标列。</param>
    private void RemovePendingIfUnchanged(string itemId, string target)
    {
        if (pendingMoves.TryGetValue(itemId, out var latest) &&
            string.Equals(latest.Target, target, StringComparison.OrdinalIgnoreCase))
        {
            pendingMoves.TryRemove(itemId, out _);
        }
    }

    /// <summary>标题栏保存徽标：pending 数量实时可见。</summary>
    private void UpdateSaveBadge()
    {
        var count = pendingMoves.Count;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Title = count > 0 ? $"迭代看板 · 正在保存 {count} 项" : "迭代看板";
        }));
    }

    /// <summary>
    /// 关窗排空：有未落库意图时阻塞等待（最多 10 秒），超时提示强制关闭或取消；绝不静默丢弃用户拖拽。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closeFlushDone && !pendingMoves.IsEmpty)
        {
            e.Cancel = true;
            _ = FlushPendingMovesAndCloseAsync();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>等待意图排空（10 秒上限）后关闭窗口；超时征询用户。</summary>
    private async Task FlushPendingMovesAndCloseAsync()
    {
        var flushed = false;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!pendingMoves.IsEmpty && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            flushed = pendingMoves.IsEmpty;
        }
        catch
        {
        }

        if (!flushed)
        {
            var choice = MessageBox.Show(
                $"仍有 {pendingMoves.Count} 项状态变更未保存完成，强制关闭将丢弃这些变更。仍要关闭吗？",
                "迭代看板",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
            {
                return; // 用户取消关闭，流水线继续
            }
        }

        closeFlushDone = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
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

    /// <summary>可达列缓存（type|stateId → 可达分类集合）：状态机配置级数据，会话内不变。</summary>
    private readonly Dictionary<string, HashSet<string>> reachableCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 计算指定工作项从当前状态可达的列集合（拖拽预校验用）。
    /// 复用 planCache/flowsCache 同源数据；解析失败返回 null（未知即不限制，宁可惜失不误杀）。
    /// </summary>
    /// <param name="item">工作项摘要。</param>
    /// <returns>可达列集合；未知返回 null。</returns>
    private async Task<HashSet<string>> GetReachableCategoriesAsync(WorkItemInfo item)
    {
        if ((item == null) || string.IsNullOrWhiteSpace(item.Type) || string.IsNullOrWhiteSpace(item.StateId))
        {
            return null;
        }

        var type = item.Type.Trim();
        var cacheKey = $"{type}|{item.StateId}";
        lock (reachableCache)
        {
            if (reachableCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        // 复用解析链路取流转（含缓存填充）；异常/未知返回 null
        try
        {
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

            if (flows == null)
            {
                return null;
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in flows)
            {
                var name = f?.Name ?? "";
                if (name.Contains("挂起") || name.Contains("受阻"))
                {
                    continue;
                }

                var cat = MapStateNameToCategory(name);
                if (!string.IsNullOrWhiteSpace(cat))
                {
                    set.Add(cat);
                }
            }

            lock (reachableCache)
            {
                reachableCache[cacheKey] = set;
            }

            return set;
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// 工作项编号比较器：按字母/数字块自然排序（PROJ-9 排在 PROJ-10 之前），
    /// 为看板列内提供确定性的稳定顺序。
    /// </summary>
    public sealed class WorkItemIdentifierComparer : IComparer<string>
    {
        /// <summary>获取单例实例。</summary>
        public static readonly WorkItemIdentifierComparer Instance = new WorkItemIdentifierComparer();

        /// <inheritdoc/>
        public int Compare(string x, string y)
        {
            if (string.Equals(x, y, StringComparison.Ordinal))
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(x))
            {
                return -1;
            }

            if (string.IsNullOrWhiteSpace(y))
            {
                return 1;
            }

            int px = 0, py = 0;
            while (px < x.Length && py < y.Length)
            {
                var cx = x[px];
                var cy = y[py];
                if (char.IsDigit(cx) && char.IsDigit(cy))
                {
                    var sx = px;
                    var sy = py;
                    while (px < x.Length && char.IsDigit(x[px])) px++;
                    while (py < y.Length && char.IsDigit(y[py])) py++;
                    var nx = x.Substring(sx, px - sx).TrimStart('0');
                    var ny = y.Substring(sy, py - sy).TrimStart('0');
                    var cmp = nx.Length != ny.Length ? nx.Length.CompareTo(ny.Length) : string.CompareOrdinal(nx, ny);
                    if (cmp != 0)
                    {
                        return cmp;
                    }
                }
                else
                {
                    var cmp = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                    if (cmp != 0)
                    {
                        return cmp;
                    }

                    px++;
                    py++;
                }
            }

            return (x.Length - px).CompareTo(y.Length - py);
        }
    }
}
