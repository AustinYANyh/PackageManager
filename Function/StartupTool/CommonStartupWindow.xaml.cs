using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MftScanner;
using PackageManager.Services;

namespace PackageManager.Function.StartupTool;

public partial class CommonStartupWindow : Window
{
    private const string AllGroupsKey = "__all_groups__";
    private const string DefaultGroupName = "项目入口";
    private const int MaxDisplayedResults = 500;
    private const int SearchPageSize = 500;
    private const int RecentDays = 7;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopMost = new IntPtr(-1);
    private static readonly IntPtr HwndNoTopMost = new IntPtr(-2);
    private static readonly TimeSpan FileSearchDebounceInterval = TimeSpan.FromMilliseconds(450);

    private static readonly string[] PresetGroupNames = { "项目入口", "开发工具", "运维脚本", "目录快捷方式", "临时工具" };

    private readonly DataPersistenceService _persistence;
    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _liveRefreshTimer;
    private readonly object _indexGate = new();
    private readonly ObservableCollection<StartupNavigationItemVm> _viewItems = new();
    private readonly ObservableCollection<StartupNavigationItemVm> _groupItems = new();
    private readonly ObservableCollection<StartupItemVm> _startupItems = new();
    private readonly ObservableCollection<StartupItemVm> _featuredItems = new();
    private readonly ObservableCollection<StartupItemVm> _workspaceItems = new();
    private readonly ObservableCollection<StartupActivityVm> _recentActivities = new();
    private readonly ObservableCollection<ScanResultItem> _scanResults = new();
    private readonly List<CommonStartupGroup> _groupDefinitions = new();
    private readonly ISharedIndexService _indexService = SharedIndexServiceFactory.Create("CtrlQ.CommonStartup");
    private readonly bool _canUseIntegratedFileSearch;
    private readonly PinyinMatcher _pinyinMatcher = new PinyinMatcher();

    private CancellationTokenSource _scanCts;
    private CancellationTokenSource _indexCts;
    private Task<int> _indexTask;
    private StartupViewKind _currentView = StartupViewKind.All;
    private StartupItemVm _selectedItem;
    private bool _indexReady;
    private bool _hideInsteadOfClose;
    private bool _allowProcessExit;
    private bool _hasInitialized;
    private bool _suppressNavigationSelection;
    private int _searchVersion;
    private string _currentGroupName = string.Empty;
    private bool _wasSearchKeywordActive;
    private int _searchContextTrackingSuppressionCount;
    private int _ignoredWorkbenchScrollChangeCount;
    private int _workbenchRefreshVersion;
    private int _launchInProgress;
    private WorkbenchSearchContext _searchRestoreContext;

    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;
    private const uint WmSysCommand = 0x0112;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmApp = 0x8000;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint QtTrayNotifyMessage = WmApp + 101;
    private const uint MkLButton = 0x0001;
    private static readonly UIntPtr ScRestore = new UIntPtr(0xF120);
    private const byte VkMenu = 0x12;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint BmClick = 0x00F5;
    private const uint TbGetButton = 0x0417;
    private const uint TbButtonCount = 0x0418;
    private const uint TbGetButtonTextW = 0x044B;
    private const uint TbGetItemRect = 0x041D;
    private const uint GwOwner = 4;
    private const int NotifyIconSuccess = 0;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsChild = 0x40000000;
    private const int WsDisabled = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExNoActivate = 0x08000000;
    private const int MaxTrayLogItems = 40;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new IntPtr(-4);

    public CommonStartupWindow(DataPersistenceService persistence)
    {
        InitializeComponent();

        _persistence = persistence;
        _canUseIntegratedFileSearch = CanUseIntegratedFileSearch();

        ViewList.ItemsSource = _viewItems;
        GroupList.ItemsSource = _groupItems;
        FeaturedItemsControl.ItemsSource = _featuredItems;
        WorkspaceItemsControl.ItemsSource = _workspaceItems;
        CandidateList.ItemsSource = _scanResults;
        ActivityList.ItemsSource = _recentActivities;

        _debounceTimer = new DispatcherTimer { Interval = FileSearchDebounceInterval };
        _debounceTimer.Tick += DebounceTimer_Tick;
        _liveRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _liveRefreshTimer.Tick += LiveRefreshTimer_Tick;
        _indexService.IndexChanged += IndexService_IndexChanged;
        _indexService.IndexStatusChanged += IndexService_IndexStatusChanged;

        _hideInsteadOfClose = true;
        LoadSavedItems();
        UpdateKeyboardHint();
        RefreshWorkbench();
    }

    public void FocusSearchBoxAndSelectAll()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void PrepareForProcessExit()
    {
        _allowProcessExit = true;
        _hideInsteadOfClose = false;
    }

    public void ReloadFromPersistence()
    {
        var selectedPath = _selectedItem?.FullPath;
        LoadSavedItems();

        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            _selectedItem = _startupItems.FirstOrDefault(item =>
                string.Equals(item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        }

        RefreshWorkbench();
    }

    private void LoadSavedItems()
    {
        var settings = _persistence.LoadSettings();
        var normalized = NormalizeSettings(settings, out _);

        _groupDefinitions.Clear();
        _groupDefinitions.AddRange(normalized.CommonStartupGroups
            .Where(group => !string.IsNullOrWhiteSpace(group?.Name))
            .OrderBy(group => group.Order)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase));

        _startupItems.Clear();
        foreach (var item in normalized.CommonStartupItems
                     .OrderBy(item => GetGroupOrder(item.GroupName))
                     .ThenBy(item => item.Order)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var vm = StartupItemVm.FromModel(item);
            UpdateItemRuntimeState(vm);
            _startupItems.Add(vm);
        }

        // 在后台线程构建拼音索引，避免阻塞 UI 线程
        var itemsSnapshot = _startupItems.ToList();
        Task.Run(() => _pinyinMatcher.BuildIndex(itemsSnapshot));
    }

    private AppSettings NormalizeSettings(AppSettings settings, out bool changed)
    {
        settings ??= new AppSettings();
        settings.CommonStartupItems ??= new List<CommonStartupItem>();
        settings.CommonStartupGroups ??= new List<CommonStartupGroup>();

        changed = false;

        var originalCount = settings.CommonStartupItems.Count;
        var items = settings.CommonStartupItems
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.FullPath))
            .ToList();

        if (items.Count != originalCount)
        {
            changed = true;
        }

        var groups = new List<CommonStartupGroup>();
        var knownGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var preset in PresetGroupNames)
        {
            knownGroups.Add(preset);
            groups.Add(new CommonStartupGroup { Name = preset });
        }

        foreach (var group in settings.CommonStartupGroups
                     .Where(group => group != null && !string.IsNullOrWhiteSpace(group.Name))
                     .OrderBy(group => group.Order <= 0 ? int.MaxValue : group.Order)
                     .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = group.Name.Trim();
            if (knownGroups.Add(name))
            {
                groups.Add(new CommonStartupGroup { Name = name });
            }
        }

        foreach (var item in items)
        {
            item.Name = string.IsNullOrWhiteSpace(item.Name)
                ? System.IO.Path.GetFileNameWithoutExtension(item.FullPath) ?? item.FullPath
                : item.Name.Trim();
            item.Arguments ??= string.Empty;
            item.Note ??= string.Empty;

            if (string.IsNullOrWhiteSpace(item.GroupName))
            {
                item.GroupName = DefaultGroupName;
                changed = true;
            }
            else
            {
                item.GroupName = item.GroupName.Trim();
            }

            if (knownGroups.Add(item.GroupName))
            {
                groups.Add(new CommonStartupGroup { Name = item.GroupName });
                changed = true;
            }
        }

        for (var i = 0; i < groups.Count; i++)
        {
            var expectedOrder = i + 1;
            if (groups[i].Order != expectedOrder)
            {
                groups[i].Order = expectedOrder;
                changed = true;
            }
        }

        foreach (var groupedItems in items.GroupBy(item => item.GroupName, StringComparer.OrdinalIgnoreCase))
        {
            var order = 1;
            foreach (var item in groupedItems
                         .OrderBy(item => item.Order <= 0 ? int.MaxValue : item.Order)
                         .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (item.Order != order)
                {
                    item.Order = order;
                    changed = true;
                }

                order++;
            }
        }

        settings.CommonStartupGroups = groups;
        settings.CommonStartupItems = items;
        return settings;
    }

    private bool SaveItems(bool allowStructureChange = false)
    {
        var settings = _persistence.LoadSettings() ?? new AppSettings();
        var normalized = NormalizeSettings(settings, out _);
        var currentItems = _startupItems
            .OrderBy(item => GetGroupOrder(item.GroupName))
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.ToModel())
            .ToList();
        var currentGroups = _groupDefinitions
            .OrderBy(group => group.Order)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CommonStartupGroup
            {
                Name = group.Name,
                Order = group.Order
            })
            .ToList();

        if (!allowStructureChange)
        {
            var persistedPathSet = new HashSet<string>(
                normalized.CommonStartupItems
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.FullPath))
                    .Select(item => item.FullPath),
                StringComparer.OrdinalIgnoreCase);
            var currentPathSet = new HashSet<string>(
                currentItems
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.FullPath))
                    .Select(item => item.FullPath),
                StringComparer.OrdinalIgnoreCase);

            var missingPersistedItems = persistedPathSet.Except(currentPathSet).Any();
            var groupCountDecreased = currentGroups.Count < normalized.CommonStartupGroups.Count;
            if (missingPersistedItems || groupCountDecreased)
            {
                LoggingService.LogInfo(
                    $"[StartupItems] 阻止覆盖较新的磁盘数据。DiskItems={persistedPathSet.Count}, MemoryItems={currentPathSet.Count}, DiskGroups={normalized.CommonStartupGroups.Count}, MemoryGroups={currentGroups.Count}");
                StatusText.Text = "检测到磁盘中的启动项配置比当前窗口更新，已阻止覆盖。请重新打开 Ctrl+Q。";
                ReloadFromPersistence();
                return false;
            }
        }

        normalized.CommonStartupItems = currentItems;
        normalized.CommonStartupGroups = currentGroups;
        return _persistence.SaveSettings(normalized, preserveExistingCommonStartupData: false);
    }

    private void RefreshWorkbench()
    {
        RefreshWorkbench(refreshExpensivePanels: true, refreshSearchPanels: true);
    }

    private void RefreshWorkbench(bool refreshExpensivePanels, bool refreshSearchPanels)
    {
        if (refreshExpensivePanels)
        {
            RefreshRuntimeState();
            RefreshNavigationCollections();
        }
        else
        {
            SyncNavigationSelection();
        }

        var visibleItems = GetVisibleItems().ToList();
        var featuredItems = GetFeaturedItems(visibleItems).ToList();
        var workspaceItems = visibleItems.Except(featuredItems).ToList();

        ReplaceCollection(_featuredItems, featuredItems);
        ReplaceCollection(_workspaceItems, workspaceItems);

        FeaturedSection.Visibility = featuredItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceEmptyText.Visibility = workspaceItems.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        EnsureSelectedItem(featuredItems, workspaceItems);
        RefreshHeader(visibleItems, featuredItems, workspaceItems);
        RefreshSummaryCards(visibleItems);
        if (refreshExpensivePanels)
        {
            RefreshSidebarStats();
            RefreshRecentActivities();
            RefreshManagePreview();
        }
        if (refreshSearchPanels)
        {
            RefreshQueue();
            RefreshCandidateSuggestions();
            RefreshCandidatePane();
        }
        UpdateFilterButtons();
    }

    private void RefreshRuntimeState()
    {
        foreach (var item in _startupItems)
        {
            UpdateItemRuntimeState(item);
        }
    }

    private void UpdateItemRuntimeState(StartupItemVm item)
    {
        if (item == null)
        {
            return;
        }

        item.IsBroken = string.IsNullOrWhiteSpace(item.FullPath) || !File.Exists(item.FullPath);
        item.AccentBrush = ResolveAccentBrush(item.GroupName);
    }

    private Brush ResolveAccentBrush(string groupName)
    {
        if (string.Equals(groupName, "开发工具", StringComparison.OrdinalIgnoreCase))
        {
            return CreateBrush(0x38, 0x7F, 0xE0);
        }

        if (string.Equals(groupName, "运维脚本", StringComparison.OrdinalIgnoreCase))
        {
            return CreateBrush(0xE1, 0x90, 0x2F);
        }

        if (string.Equals(groupName, "目录快捷方式", StringComparison.OrdinalIgnoreCase))
        {
            return CreateBrush(0x68, 0x78, 0x72);
        }

        if (string.Equals(groupName, "临时工具", StringComparison.OrdinalIgnoreCase))
        {
            return CreateBrush(0x8A, 0x64, 0x2B);
        }

        return CreateBrush(0x1A, 0x6A, 0x5F);
    }

    private void RefreshNavigationCollections()
    {
        ReplaceCollection(_viewItems, new[]
        {
            CreateViewNavigationItem(StartupViewKind.All, "全部启动项", _startupItems.Count),
            CreateViewNavigationItem(StartupViewKind.Recent, "最近启动", _startupItems.Count(IsRecentItem)),
            CreateViewNavigationItem(StartupViewKind.Favorites, "收藏", _startupItems.Count(item => item.IsFavorite)),
            CreateViewNavigationItem(StartupViewKind.Broken, "失效项", _startupItems.Count(item => item.IsBroken))
        });

        var groupItems = new List<StartupNavigationItemVm>
        {
            new()
            {
                Key = AllGroupsKey,
                Title = "全部分组",
                Count = _startupItems.Count,
                CountBackground = CreateBrush(0xED, 0xF1, 0xEE),
                CountForeground = CreateBrush(0x56, 0x62, 0x5B)
            }
        };

        groupItems.AddRange(_groupDefinitions
            .OrderBy(group => group.Order)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new StartupNavigationItemVm
            {
                Key = group.Name,
                Title = group.Name,
                Count = _startupItems.Count(item => string.Equals(item.GroupName, group.Name, StringComparison.OrdinalIgnoreCase)),
                CountBackground = CreateBrush(0xED, 0xF1, 0xEE),
                CountForeground = CreateBrush(0x56, 0x62, 0x5B)
            }));

        ReplaceCollection(_groupItems, groupItems);
        SyncNavigationSelection();
    }

    private void SyncNavigationSelection()
    {
        _suppressNavigationSelection = true;
        try
        {
            ViewList.SelectedItem = _viewItems.FirstOrDefault(item => item.Key == GetViewKey(_currentView));
            GroupList.SelectedItem = _groupItems.FirstOrDefault(item =>
                string.Equals(item.Key, string.IsNullOrWhiteSpace(_currentGroupName) ? AllGroupsKey : _currentGroupName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressNavigationSelection = false;
        }
    }

    private IEnumerable<StartupItemVm> GetVisibleItems()
    {
        var keyword = GetSearchKeyword();
        var groupOrderLookup = _groupDefinitions.ToDictionary(group => group.Name, group => group.Order, StringComparer.OrdinalIgnoreCase);
        return _startupItems
            .Where(MatchesCurrentView)
            .Where(MatchesCurrentGroup)
            .Where(item => MatchesKeyword(item, keyword, _pinyinMatcher))
            .OrderByDescending(item => item.IsFavorite)
            .ThenByDescending(item => item.LastLaunchedAt ?? DateTime.MinValue)
            .ThenBy(item => groupOrderLookup.TryGetValue(item.GroupName, out var order) ? order : int.MaxValue)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<StartupItemVm> GetFeaturedItems(IEnumerable<StartupItemVm> visibleItems)
    {
        if (_currentView == StartupViewKind.Broken)
        {
            return Enumerable.Empty<StartupItemVm>();
        }

        return visibleItems
            .Where(item => item.IsFavorite || IsRecentItem(item))
            .OrderByDescending(item => item.IsFavorite)
            .ThenByDescending(item => item.LastLaunchedAt ?? DateTime.MinValue)
            .ThenBy(item => item.Order)
            .Take(4);
    }

    private void EnsureSelectedItem(IReadOnlyCollection<StartupItemVm> featuredItems, IReadOnlyCollection<StartupItemVm> workspaceItems)
    {
        var nextSelection = _selectedItem;
        if (nextSelection == null || (!_startupItems.Contains(nextSelection)) || (!featuredItems.Contains(nextSelection) && !workspaceItems.Contains(nextSelection)))
        {
            nextSelection = featuredItems.FirstOrDefault() ?? workspaceItems.FirstOrDefault();
        }

        SelectStartupItem(nextSelection);
    }

    private void SelectStartupItem(StartupItemVm item)
    {
        _selectedItem = item;
        foreach (var startupItem in _startupItems)
        {
            startupItem.IsSelected = ReferenceEquals(startupItem, item);
        }
    }

    private void NavigateSelection(int delta)
    {
        // 当前显示的所有卡片：featured 在前，workspace 在后
        var allVisible = _featuredItems.Concat(_workspaceItems).ToList();
        if (allVisible.Count == 0)
            return;

        var currentIndex = _selectedItem == null ? -1 : allVisible.IndexOf(_selectedItem);
        var nextIndex = Math.Max(0, Math.Min(allVisible.Count - 1, currentIndex + delta));
        if (nextIndex == currentIndex && currentIndex >= 0)
            return;

        InvalidateSearchRestoreContext();
        SelectStartupItem(allVisible[nextIndex]);
        ScrollSelectedCardIntoView();
    }

    private void ScrollSelectedCardIntoView()
    {
        if (_selectedItem == null || WorkbenchScrollViewer == null)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 在 FeaturedItemsControl 和 WorkspaceItemsControl 中找选中卡片的容器
            var card = FindCardElement(FeaturedItemsControl, _selectedItem)
                       ?? FindCardElement(WorkspaceItemsControl, _selectedItem);
            if (card == null)
                return;

            var transform = card.TransformToAncestor(WorkbenchScrollViewer);
            var cardTop = transform.Transform(new Point(0, 0)).Y + WorkbenchScrollViewer.VerticalOffset;
            var cardBottom = cardTop + card.ActualHeight;
            var viewTop = WorkbenchScrollViewer.VerticalOffset;
            var viewBottom = viewTop + WorkbenchScrollViewer.ViewportHeight;

            if (cardTop < viewTop)
                WorkbenchScrollViewer.ScrollToVerticalOffset(cardTop - 16);
            else if (cardBottom > viewBottom)
                WorkbenchScrollViewer.ScrollToVerticalOffset(cardBottom - WorkbenchScrollViewer.ViewportHeight + 16);
        }), DispatcherPriority.Background);
    }

    private FrameworkElement FindCardElement(ItemsControl itemsControl, StartupItemVm item)
    {
        if (itemsControl == null || item == null)
            return null;

        for (var i = 0; i < itemsControl.Items.Count; i++)
        {
            if (!ReferenceEquals(itemsControl.Items[i], item))
                continue;

            var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (container == null)
                return null;

            // ItemsControl 的容器是 ContentPresenter，实际卡片是其第一个子元素 Border
            return FindVisualChild<Border>(container) ?? container;
        }

        return null;
    }

    private void RefreshHeader(IReadOnlyCollection<StartupItemVm> visibleItems, IReadOnlyCollection<StartupItemVm> featuredItems, IReadOnlyCollection<StartupItemVm> workspaceItems)
    {
        var viewTitle = GetViewTitle(_currentView);
        var groupTitle = string.IsNullOrWhiteSpace(_currentGroupName) ? "全部分组" : _currentGroupName;
        var scopeTitle = string.IsNullOrWhiteSpace(_currentGroupName) ? viewTitle : $"{_currentGroupName}工作台";

        WorkbenchTitleText.Text = scopeTitle;
        WorkbenchSubtitleText.Text = BuildWorkbenchSubtitle(visibleItems.Count, featuredItems.Count, workspaceItems.Count);
        CurrentViewBadgeText.Text = viewTitle;
        CurrentGroupBadgeText.Text = groupTitle;

        FeaturedSectionTitleText.Text = "置顶与收藏";
        FeaturedSectionHintText.Text = featuredItems.Count > 0
            ? "高频入口固定放在最上层，打开窗口后不需要滚动长列表。"
            : "当前筛选下没有高频入口，后续收藏或启动后会在这里沉淀。";
        FeaturedBadgeText.Text = $"{featuredItems.Count} 个高频入口";

        WorkspaceSectionTitleText.Text = string.IsNullOrWhiteSpace(_currentGroupName) ? "当前工作区条目" : $"{_currentGroupName}条目";
        WorkspaceSectionHintText.Text = visibleItems.Count > 0
            ? "卡片直接承载状态、备注、参数和最近使用，不再退化成长列表。"
            : "换一个视图、分组或搜索关键词试试。";
        WorkspaceBadgeText.Text = $"{workspaceItems.Count} 个条目";
    }

    private string BuildWorkbenchSubtitle(int visibleCount, int featuredCount, int workspaceCount)
    {
        var parts = new List<string>
        {
            $"{visibleCount} 个当前匹配项",
            $"{featuredCount} 个高频入口",
            $"{workspaceCount} 个主区卡片"
        };

        if (_currentView == StartupViewKind.Broken)
        {
            parts.Add("优先处理路径异常");
        }
        else if (_currentView == StartupViewKind.Recent)
        {
            parts.Add("聚焦最近 7 天使用记录");
        }

        return string.Join("，", parts) + "。";
    }

    private static string GetViewTitle(StartupViewKind view)
    {
        return view switch
        {
            StartupViewKind.Recent => "最近启动",
            StartupViewKind.Favorites => "收藏",
            StartupViewKind.Broken => "失效项",
            _ => "全部启动项"
        };
    }

    private void RefreshSummaryCards(IReadOnlyCollection<StartupItemVm> visibleItems)
    {
        PrimarySummaryText.Text = visibleItems.Count.ToString();
        SecondarySummaryText.Text = visibleItems.Count(item => item.IsFavorite).ToString();
        TertiarySummaryText.Text = visibleItems.Count(IsRecentItem).ToString();
        QuaternarySummaryText.Text = visibleItems.Count(item => item.IsBroken).ToString();

        PrimarySummaryLabel.Text = string.IsNullOrWhiteSpace(_currentGroupName) ? "当前筛选条目" : $"{_currentGroupName}条目";
        SecondarySummaryLabel.Text = "收藏";
        TertiarySummaryLabel.Text = $"近 {RecentDays} 天启动";
        QuaternarySummaryLabel.Text = "状态异常";
    }

    private void RefreshSidebarStats()
    {
        SidebarTotalItemsText.Text = _startupItems.Count.ToString();
        SidebarFavoriteItemsText.Text = _startupItems.Count(item => item.IsFavorite).ToString();
        SidebarBrokenItemsText.Text = _startupItems.Count(item => item.IsBroken).ToString();
        SidebarGroupCountText.Text = _groupDefinitions.Count.ToString();
    }

    private void RefreshQueue()
    {
        var brokenCount = _startupItems.Count(item => item.IsBroken);
        QueueBrokenTitleText.Text = $"{brokenCount} 个失效项待修复";
        QueueBrokenHintText.Text = brokenCount > 0
            ? "建议切换到“失效项”视图集中处理。"
            : "当前没有路径异常项。";

        if (!_canUseIntegratedFileSearch)
        {
            QueueCandidateTitleText.Text = "文件搜索联动未启用";
            QueueCandidateHintText.Text = "当前只能筛选已有工作台条目，不能调用集成文件搜索候选。";
            return;
        }

        QueueCandidateTitleText.Text = $"{_scanResults.Count} 个搜索候选待加入";
        QueueCandidateHintText.Text = string.IsNullOrWhiteSpace(GetSearchKeyword())
            ? "输入关键词后，右侧会出现可直接加入当前分组的候选。"
            : _scanResults.Count > 0
                ? "双击候选可直接加入当前分组，也可在右侧先确认路径。"
                : "当前关键词没有找到可加入的文件候选。";
    }

    private void RefreshRecentActivities()
    {
        var activities = _startupItems
            .Where(item => item.LastLaunchedAt.HasValue)
            .OrderByDescending(item => item.LastLaunchedAt.Value)
            .Take(5)
            .Select(item => new StartupActivityVm
            {
                TimeText = item.LastLaunchedAt.Value.ToString("MM-dd HH:mm"),
                Summary = $"{item.Name} 已启动，当前累计 {item.LaunchCountDisplay}，分组：{item.GroupName}"
            })
            .ToList();

        if (activities.Count == 0)
        {
            activities.Add(new StartupActivityVm
            {
                TimeText = "暂无记录",
                Summary = "启动历史会在成功启动后显示在这里。"
            });
        }

        ReplaceCollection(_recentActivities, activities);
    }

    private void RefreshManagePreview()
    {
        var previewLines = _startupItems
            .OrderByDescending(item => item.IsFavorite)
            .ThenByDescending(item => item.LastLaunchedAt ?? DateTime.MinValue)
            .ThenBy(item => GetGroupOrder(item.GroupName))
            .ThenBy(item => item.Order)
            .Take(3)
            .Select(item =>
                $"{item.Name} | {item.GroupName} | {item.FullPath} | {(string.IsNullOrWhiteSpace(item.Arguments) ? "-" : item.Arguments)} | {item.LastLaunchDisplay} | {(item.IsBroken ? "异常" : "正常")} | {(item.IsFavorite ? "★" : "-")} | {item.Order:00}")
            .ToList();

        ManagePreviewText.Text = previewLines.Count == 0
            ? "暂无数据"
            : string.Join(Environment.NewLine, previewLines);
    }

    private void RefreshCandidateSuggestions()
    {
        foreach (var result in _scanResults)
        {
            result.SuggestedGroupName = ResolveSuggestedGroup(result.FullPath);
        }
    }

    private void RefreshCandidatePane()
    {
        CandidateCountText.Text = $"{_scanResults.Count} 个候选";

        if (!_canUseIntegratedFileSearch)
        {
            CandidateHintText.Text = "当前未启用集成文件搜索。上方搜索仍可筛选已有启动项。";
            CandidateNameText.Text = "未启用文件搜索联动";
            CandidatePathText.Text = "启用后，这里会展示来自 MFT 索引的候选。";
            CandidateSuggestionText.Text = string.Empty;
            SetCandidateListInteraction(false);
            SetCandidateButtonsEnabled(false);
            return;
        }

        SetCandidateListInteraction(true);

        if (_scanResults.Count == 0)
        {
            CandidateHintText.Text = string.IsNullOrWhiteSpace(GetSearchKeyword())
                ? "输入关键词后，这里会显示可直接作为启动项的文件候选。"
                : "当前关键词没有找到可直接加入的文件候选。";
            CandidateNameText.Text = "暂无候选";
            CandidatePathText.Text = "候选会优先建议加入当前分组。";
            CandidatePathText.ToolTip = CandidatePathText.Text;
            CandidateSuggestionText.Text = string.Empty;
            CandidateList.SelectedItem = null;
            SetCandidateListInteraction(false);
            SetCandidateButtonsEnabled(false);
            return;
        }

        if (CandidateList.SelectedItem is not ScanResultItem selected || !_scanResults.Contains(selected))
        {
            selected = _scanResults[0];
            CandidateList.SelectedItem = selected;
        }

        CandidateHintText.Text = "搜索候选不再与工作台等宽并排，而是退到右侧作为联动区。";
        CandidateNameText.Text = selected.FileName;
        CandidatePathText.Text = CompactMiddleText(selected.FullPath, 72);
        CandidatePathText.ToolTip = selected.FullPath;
        CandidateSuggestionText.Text = $"建议加入：{selected.SuggestedGroupName}";
        SetCandidateButtonsEnabled(true);
    }

    private void SetCandidateButtonsEnabled(bool enabled)
    {
        AddCandidateButton.IsEnabled = enabled;
        OpenCandidateFolderButton.IsEnabled = enabled;
        CopyCandidatePathButton.IsEnabled = enabled;
    }

    private void SetCandidateListInteraction(bool enabled)
    {
        CandidateList.IsEnabled = enabled;
        CandidateList.IsHitTestVisible = enabled;
        CandidateList.Focusable = enabled;
    }

    private void AddItem(string fullPath, string targetGroupName = null, bool editBeforeSave = true)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        var existing = _startupItems.FirstOrDefault(item =>
            string.Equals(item.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(existing.GroupName))
            {
                _currentGroupName = existing.GroupName;
            }

            SelectStartupItem(existing);
            RefreshWorkbench();
            StatusText.Text = $"已存在：{existing.Name}";
            return;
        }

        var candidate = new StartupItemVm
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(fullPath) ?? fullPath,
            FullPath = fullPath,
            GroupName = NormalizeGroupName(targetGroupName),
            Order = GetNextItemOrder(NormalizeGroupName(targetGroupName))
        };

        StartupItemVm finalItem;
        if (editBeforeSave)
        {
            var editWindow = new StartupItemEditWindow(candidate, GetAvailableGroupNames()) { Owner = this };
            if (editWindow.ShowDialog() != true)
            {
                return;
            }

            finalItem = editWindow.Result;
            finalItem.GroupName = NormalizeGroupName(finalItem.GroupName);
            finalItem.Order = GetNextItemOrder(finalItem.GroupName);
        }
        else
        {
            finalItem = candidate;
        }

        EnsureGroupExists(finalItem.GroupName);
        UpdateItemRuntimeState(finalItem);
        _startupItems.Add(finalItem);
        _pinyinMatcher.UpdateEntry(finalItem);
        _currentGroupName = finalItem.GroupName;
        SelectStartupItem(finalItem);
        SaveItems(allowStructureChange: true);
        RefreshWorkbench();
        StatusText.Text = $"已添加：{finalItem.Name}";
    }

    private void EditItem(StartupItemVm item)
    {
        if (item == null)
        {
            return;
        }

        var originalGroup = item.GroupName;
        var editWindow = new StartupItemEditWindow(item.Clone(), GetAvailableGroupNames()) { Owner = this };
        if (editWindow.ShowDialog() != true)
        {
            return;
        }

        var edited = editWindow.Result;
        item.Name = edited.Name;
        item.FullPath = edited.FullPath;
        item.Arguments = edited.Arguments;
        item.Note = edited.Note;
        item.GroupName = NormalizeGroupName(edited.GroupName);
        item.IsFavorite = edited.IsFavorite;
        if (!string.Equals(originalGroup, item.GroupName, StringComparison.OrdinalIgnoreCase))
        {
            EnsureGroupExists(item.GroupName);
            item.Order = GetNextItemOrder(item.GroupName, item);
        }

        UpdateItemRuntimeState(item);
        _pinyinMatcher.UpdateEntry(item);
        SaveItems(allowStructureChange: true);
        SelectStartupItem(item);
        RefreshWorkbench();
        StatusText.Text = $"已更新：{item.Name}";
    }

    private void RemoveItem(StartupItemVm item)
    {
        if (item == null)
        {
            return;
        }

        if (MessageBox.Show($"确认删除“{item.Name}”？", "常用启动项", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _startupItems.Remove(item);
        _pinyinMatcher.RemoveEntry(item.Name);
        if (ReferenceEquals(_selectedItem, item))
        {
            _selectedItem = null;
        }

        SaveItems(allowStructureChange: true);
        RefreshWorkbench();
        StatusText.Text = $"已删除：{item.Name}";
    }

    private void ToggleFavorite(StartupItemVm item)
    {
        if (item == null)
        {
            return;
        }

        item.IsFavorite = !item.IsFavorite;
        SaveItems();
        SelectStartupItem(item);
        RefreshWorkbench();
        StatusText.Text = item.IsFavorite ? $"已收藏：{item.Name}" : $"已取消收藏：{item.Name}";
    }

    private async void LaunchItem(StartupItemVm item, bool forceNewInstance = false)
    {
        if (item == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _launchInProgress, 1) == 1)
        {
            StatusText.Text = "正在处理上一个启动请求，请稍候。";
            return;
        }

        try
        {
            StatusText.Text = forceNewInstance ? $"正在强制启动：{item.Name}" : $"正在唤醒：{item.Name}";
            var ownerHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var activationResult = forceNewInstance
                ? ExistingInstanceActivationResult.NotFound
                : await Task.Run(() => TryActivateExistingInstance(item, ownerHandle));
            if (activationResult == ExistingInstanceActivationResult.Activated)
            {
                SelectStartupItem(item);
                RefreshWorkbench();
                StatusText.Text = $"已唤醒：{item.Name}";
                return;
            }

            if (activationResult == ExistingInstanceActivationResult.FoundWithoutWindow
                && IsKnownSingleInstanceTrayApp(item.FullPath))
            {
                StatusText.Text = $"未能唤醒：{item.Name}，已保持工作台焦点。";
                return;
            }

            var startInfo = ShouldUseOriginalShellActivation(item.FullPath)
                ? CreateOriginalShellActivationStartInfo(item)
                : CreateLaunchStartInfo(item, forceNewInstance);

            LoggingService.LogInfo(
                $"启动常用项：Name={item.Name}, Path={item.FullPath}, ActivationResult={activationResult}, FileName={startInfo.FileName}, Arguments={startInfo.Arguments}, UseShellExecute={startInfo.UseShellExecute}");
            var startedProcess = Process.Start(startInfo);
            if (ShouldUseOriginalShellActivation(item.FullPath))
            {
                await Task.Run(() => WaitForExistingInstanceActivation(item.FullPath, ownerHandle, 5000));
            }
            try { startedProcess?.Dispose(); } catch { }

            RecordItemLaunch(item);
            StatusText.Text = activationResult == ExistingInstanceActivationResult.FoundWithoutWindow
                ? $"已尝试从托盘唤醒：{item.Name}"
                : (forceNewInstance ? $"已强制启动：{item.Name}" : $"已启动：{item.Name}");
        }
        catch (Exception ex)
        {
            UpdateItemRuntimeState(item);
            RefreshWorkbench();
            MessageBox.Show($"启动失败：{ex.Message}", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _launchInProgress, 0);
        }
    }

    private void RecordItemLaunch(StartupItemVm item)
    {
        item.LastLaunchedAt = DateTime.Now;
        item.LaunchCount++;
        SaveItems();
        SelectStartupItem(item);
        RefreshWorkbench();
    }

    private static ProcessStartInfo CreateLaunchStartInfo(StartupItemVm item, bool forceNewInstance)
    {
        if (forceNewInstance
            && !string.IsNullOrWhiteSpace(item.FullPath)
            && string.Equals(System.IO.Path.GetExtension(item.FullPath), ".lnk", StringComparison.OrdinalIgnoreCase)
            && File.Exists(item.FullPath))
        {
            return new ProcessStartInfo
            {
                FileName = item.FullPath,
                Arguments = item.Arguments ?? string.Empty,
                UseShellExecute = true
            };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = item.FullPath,
            Arguments = item.Arguments ?? string.Empty,
            UseShellExecute = true
        };

        var shortcut = TryResolveShortcut(item.FullPath);
        if (shortcut != null)
        {
            startInfo.FileName = shortcut.TargetPath;
            startInfo.Arguments = CombineArguments(shortcut.Arguments, item.Arguments);
            if (!string.IsNullOrWhiteSpace(shortcut.WorkingDirectory) && Directory.Exists(shortcut.WorkingDirectory))
            {
                startInfo.WorkingDirectory = shortcut.WorkingDirectory;
            }

            return NormalizeForceNewProcessStartInfo(startInfo, forceNewInstance);
        }

        var workingDirectory = GetLaunchWorkingDirectory(item.FullPath);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        return NormalizeForceNewProcessStartInfo(startInfo, forceNewInstance);
    }

    private static ProcessStartInfo CreateOriginalShellActivationStartInfo(StartupItemVm item)
    {
        var launchPath = ResolveOriginalShellLaunchPath(item);
        var arguments = item.Arguments ?? string.Empty;
        if (IsWeChatPath(launchPath)
            && string.Equals(System.IO.Path.GetExtension(launchPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = QuoteArgument(launchPath),
                UseShellExecute = false
            };
        }

        if (IsWeChatPath(launchPath)
            && string.Equals(System.IO.Path.GetExtension(launchPath), ".exe", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(arguments))
        {
            arguments = "--scene=desktop";
        }

        return new ProcessStartInfo
        {
            FileName = launchPath,
            Arguments = arguments,
            UseShellExecute = true
        };
    }

    private static string ResolveOriginalShellLaunchPath(StartupItemVm item)
    {
        var fullPath = item?.FullPath;
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return fullPath;
        }

        if (string.Equals(System.IO.Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        if (!IsWeChatPath(fullPath))
        {
            return fullPath;
        }

        var shortcut = FindShortcutForTarget(fullPath);
        return string.IsNullOrWhiteSpace(shortcut) ? fullPath : shortcut;
    }

    private static ProcessStartInfo NormalizeForceNewProcessStartInfo(ProcessStartInfo startInfo, bool forceNewInstance)
    {
        if (!forceNewInstance
            || startInfo == null
            || string.IsNullOrWhiteSpace(startInfo.FileName)
            || !string.Equals(System.IO.Path.GetExtension(startInfo.FileName), ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(startInfo.FileName))
        {
            return startInfo;
        }

        startInfo.UseShellExecute = false;
        if (string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
        {
            var directory = System.IO.Path.GetDirectoryName(startInfo.FileName);
            if (Directory.Exists(directory))
            {
                startInfo.WorkingDirectory = directory;
            }
        }

        return startInfo;
    }

    private static string ResolveActivationPath(string fullPath)
    {
        return TryResolveShortcut(fullPath)?.TargetPath ?? fullPath;
    }

    private static string FindShortcutForTarget(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        var normalizedTarget = NormalizeFilePath(targetPath);
        var desktopDirectories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        foreach (var directory in desktopDirectories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var shortcutPath in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly))
            {
                var shortcut = TryResolveShortcut(shortcutPath);
                if (shortcut == null)
                {
                    continue;
                }

                if (string.Equals(NormalizeFilePath(shortcut.TargetPath), normalizedTarget, StringComparison.OrdinalIgnoreCase)
                    && IsWeChatPath(shortcut.TargetPath))
                {
                    return shortcutPath;
                }
            }
        }

        return null;
    }

    private static string CombineArguments(string shortcutArguments, string itemArguments)
    {
        if (string.IsNullOrWhiteSpace(itemArguments))
        {
            return shortcutArguments ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(shortcutArguments))
        {
            return itemArguments ?? string.Empty;
        }

        return shortcutArguments + " " + itemArguments;
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static ShortcutTarget TryResolveShortcut(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)
            || !string.Equals(System.IO.Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            if (shell == null)
            {
                return null;
            }

            try
            {
                var shortcut = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { fullPath });
                if (shortcut == null)
                {
                    return null;
                }

                var shortcutType = shortcut.GetType();
                var targetPath = shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    return null;
                }

                try
                {
                    return new ShortcutTarget
                    {
                        TargetPath = targetPath,
                        Arguments = shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string ?? string.Empty,
                        WorkingDirectory = shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string ?? string.Empty
                    };
                }
                finally
                {
                    if (Marshal.IsComObject(shortcut))
                    {
                        Marshal.FinalReleaseComObject(shortcut);
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            return null;
        }
    }

    private ExistingInstanceActivationResult TryActivateExistingInstance(StartupItemVm item, IntPtr ownerHandle)
    {
        var fullPath = item?.FullPath;
        var processes = FindExistingProcesses(fullPath);
        var foundExistingProcess = processes.Count > 0;
        LoggingService.LogDebug($"[CtrlQ Activate] start name={item?.Name} path={fullPath} processCount={processes.Count} owner=0x{ownerHandle.ToInt64():X}");
        try
        {
            if (foundExistingProcess && ShouldPreferTrayActivationBeforeWindow(fullPath))
            {
                LoggingService.LogDebug($"[CtrlQ Activate] prefer-tray-first name={item?.Name} path={fullPath}");
                if (TryActivateFromTrayOrShell(item, ownerHandle))
                {
                    LoggingService.LogDebug($"[CtrlQ Activate] tray-first success name={item?.Name}");
                    return ExistingInstanceActivationResult.Activated;
                }
            }

            foreach (var process in processes)
            {
                if (TryBringProcessToFront(process, ownerHandle))
                {
                    LoggingService.LogDebug($"[CtrlQ Activate] existing-window success name={item?.Name} pid={process.Id} process={process.ProcessName}");
                    return ExistingInstanceActivationResult.Activated;
                }
            }

            if (foundExistingProcess && TryActivateFromTrayOrShell(item, ownerHandle))
            {
                LoggingService.LogDebug($"[CtrlQ Activate] tray-or-shell success name={item?.Name}");
                return ExistingInstanceActivationResult.Activated;
            }

            RestoreOwnerFocus(ownerHandle);
            LoggingService.LogDebug($"[CtrlQ Activate] not-activated name={item?.Name} foundProcess={foundExistingProcess}");
            return foundExistingProcess
                ? ExistingInstanceActivationResult.FoundWithoutWindow
                : ExistingInstanceActivationResult.NotFound;
        }
        finally
        {
            foreach (var process in processes)
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private static List<Process> FindExistingProcesses(string fullPath)
    {
        var activationPath = ResolveActivationPath(fullPath);
        if (string.IsNullOrWhiteSpace(activationPath) || !File.Exists(activationPath))
        {
            LoggingService.LogDebug($"[CtrlQ FindProcess] skip invalid path={fullPath} activationPath={activationPath}");
            return new List<Process>();
        }

        if (!string.Equals(System.IO.Path.GetExtension(activationPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            LoggingService.LogDebug($"[CtrlQ FindProcess] skip non-exe path={fullPath} activationPath={activationPath}");
            return new List<Process>();
        }

        var processName = System.IO.Path.GetFileNameWithoutExtension(activationPath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return new List<Process>();
        }

        var normalizedPath = NormalizeFilePath(activationPath);
        var exactMatches = new List<Process>();
        var fallbackMatches = new List<Process>();
        foreach (var relatedProcessName in GetRelatedProcessNames(processName))
        {
            foreach (var process in Process.GetProcessesByName(relatedProcessName))
            {
                var shouldDispose = true;
                try
                {
                    var processPath = TryGetProcessPath(process);
                    if (!string.IsNullOrWhiteSpace(processPath))
                    {
                        if (string.Equals(NormalizeFilePath(processPath), normalizedPath, StringComparison.OrdinalIgnoreCase)
                            || IsRelatedWeChatProcessPath(processName, processPath))
                        {
                            exactMatches.Add(process);
                            shouldDispose = false;
                        }
                    }
                    else
                    {
                        fallbackMatches.Add(process);
                        shouldDispose = false;
                    }
                }
                catch
                {
                }
                finally
                {
                    if (shouldDispose)
                    {
                        process.Dispose();
                    }
                }
            }
        }

        exactMatches.Sort(CompareProcessesByWindowPriority);
        fallbackMatches.Sort(CompareProcessesByWindowPriority);
        exactMatches.AddRange(fallbackMatches);
        LoggingService.LogDebug(
            $"[CtrlQ FindProcess] path={fullPath} activationPath={activationPath} processName={processName} exact={exactMatches.Count - fallbackMatches.Count} fallback={fallbackMatches.Count} total={exactMatches.Count}");
        return exactMatches;
    }

    private static IEnumerable<string> GetRelatedProcessNames(string processName)
    {
        if (IsWeChatProcessName(processName))
        {
            yield return "Weixin";
            yield return "WeChat";
            yield return "WeChatAppEx";
            yield break;
        }

        yield return processName;
    }

    private static bool IsRelatedWeChatProcessPath(string activationProcessName, string processPath)
    {
        if (!IsWeChatProcessName(activationProcessName))
        {
            return false;
        }

        var processName = System.IO.Path.GetFileNameWithoutExtension(processPath ?? string.Empty) ?? string.Empty;
        return IsWeChatProcessName(processName);
    }

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeFilePath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path ?? string.Empty;
        }
    }

    private static int CompareProcessesByWindowPriority(Process left, Process right)
    {
        var leftCandidate = FindBestProcessWindow(left);
        var rightCandidate = FindBestProcessWindow(right);
        if (leftCandidate.Handle != IntPtr.Zero && rightCandidate.Handle == IntPtr.Zero)
        {
            return -1;
        }

        if (leftCandidate.Handle == IntPtr.Zero && rightCandidate.Handle != IntPtr.Zero)
        {
            return 1;
        }

        var scoreCompare = rightCandidate.Score.CompareTo(leftCandidate.Score);
        if (scoreCompare != 0)
        {
            return scoreCompare;
        }

        return (right?.StartTime ?? DateTime.MinValue).CompareTo(left?.StartTime ?? DateTime.MinValue);
    }

    private static bool TryBringProcessToFront(Process process, IntPtr ownerHandle)
    {
        var candidate = FindBestProcessWindow(process);
        if (candidate.Handle == IntPtr.Zero)
        {
            LoggingService.LogDebug($"[CtrlQ BringFront] no-window pid={SafeProcessId(process)} process={SafeProcessName(process)}");
            return false;
        }

        var processName = process?.ProcessName ?? string.Empty;
        if (ShouldUseShellActivationForHiddenMainWindow(processName, candidate))
        {
            LoggingService.LogDebug($"[CtrlQ BringFront] hidden-wechat-skip-direct-show pid={SafeProcessId(process)} process={processName} handle=0x{candidate.Handle.ToInt64():X} title={candidate.Title} class={candidate.ClassName}");
            return false;
        }

        var handle = candidate.Handle;
        if (candidate.IsIconic)
        {
            ShowWindow(handle, SwRestore);
            SendMessage(handle, WmSysCommand, ScRestore, IntPtr.Zero);
        }
        else
        {
            ShowWindow(handle, SwShow);
        }

        if (BringWindowToFrontLegacy(handle))
        {
            LoggingService.LogDebug($"[CtrlQ BringFront] legacy-success pid={SafeProcessId(process)} process={processName} handle=0x{handle.ToInt64():X} title={candidate.Title}");
            return true;
        }

        if (ShouldUseAggressiveForeground(processName) || candidate.PreferAggressive)
        {
            if (ForceBringWindowToFront(handle))
            {
                LoggingService.LogDebug($"[CtrlQ BringFront] force-success pid={SafeProcessId(process)} process={processName} handle=0x{handle.ToInt64():X} title={candidate.Title}");
                return true;
            }
        }

        RestoreOwnerFocus(ownerHandle);
        LoggingService.LogDebug($"[CtrlQ BringFront] failed pid={SafeProcessId(process)} process={processName} handle=0x{handle.ToInt64():X} title={candidate.Title}");
        return false;
    }

    private static bool BringWindowToFrontLegacy(IntPtr handle)
    {
        BringWindowToTop(handle);
        return SetForegroundWindow(handle);
    }

    private static bool ForceBringWindowToFront(IntPtr handle)
    {
        keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyEventFKeyUp, UIntPtr.Zero);
        BringWindowToTop(handle);
        SetWindowPos(handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetWindowPos(handle, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        if (SetForegroundWindow(handle))
        {
            return true;
        }

        var foregroundHandle = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();
        var targetThreadId = GetWindowThreadProcessId(handle, out _);
        var foregroundThreadId = foregroundHandle == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundHandle, out _);

        var attachedToTarget = targetThreadId != 0 && targetThreadId != currentThreadId && AttachThreadInput(currentThreadId, targetThreadId, true);
        var attachedToForeground = foregroundThreadId != 0 && foregroundThreadId != currentThreadId && AttachThreadInput(currentThreadId, foregroundThreadId, true);
        try
        {
            BringWindowToTop(handle);
            return SetForegroundWindow(handle);
        }
        finally
        {
            if (attachedToForeground)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }

            if (attachedToTarget)
            {
                AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
    }

    private static bool ShouldUseShellActivationForHiddenMainWindow(string processName, WindowActivationCandidate candidate)
    {
        return !candidate.IsVisible && IsWeChatProcessName(processName);
    }

    private static bool ShouldUseAggressiveForeground(string processName)
    {
        return processName.Equals("QQ", StringComparison.OrdinalIgnoreCase)
            || IsWeChatProcessName(processName);
    }

    private static bool ShouldUseOriginalShellActivation(string fullPath)
    {
        var activationPath = ResolveActivationPath(fullPath);
        var processName = System.IO.Path.GetFileNameWithoutExtension(activationPath ?? fullPath) ?? string.Empty;
        return IsWeChatProcessName(processName);
    }

    private static bool IsWeChatPath(string path)
    {
        var processName = System.IO.Path.GetFileNameWithoutExtension(ResolveActivationPath(path) ?? path) ?? string.Empty;
        return IsWeChatProcessName(processName);
    }

    private static bool IsQQPath(string path)
    {
        var processName = System.IO.Path.GetFileNameWithoutExtension(ResolveActivationPath(path) ?? path) ?? string.Empty;
        return processName.Equals("QQ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldPreferTrayActivationBeforeWindow(string fullPath)
    {
        return IsQQPath(fullPath);
    }

    private bool TryActivateFromTrayOrShell(StartupItemVm item, IntPtr ownerHandle)
    {
        if (item == null || !IsKnownSingleInstanceTrayApp(item.FullPath))
        {
            return false;
        }

        var terms = GetTrayActivationTerms(item.FullPath);
        LoggingService.LogDebug($"[CtrlQ Tray] start name={item.Name} path={item.FullPath} terms={string.Join("|", terms)}");
        var isWeChat = IsWeChatPath(item.FullPath);
        if (isWeChat)
        {
            if (TryInvokeTrayIconWithAutomation(terms, item.Name) && WaitForExistingInstanceActivation(item.FullPath, ownerHandle, 1600))
            {
                LoggingService.LogDebug($"[CtrlQ Tray] wechat-uia-success name={item.Name}");
                return true;
            }

            if (TrySendWeChatTrayActivation(item.FullPath) && WaitForExistingInstanceActivation(item.FullPath, ownerHandle, 500))
            {
                LoggingService.LogDebug($"[CtrlQ Tray] wechat-callback-success name={item.Name}");
                return true;
            }

            LoggingService.LogDebug($"[CtrlQ Tray] wechat-all-paths-failed name={item.Name}; skip generic tray scan");
            return false;
        }

        if (TryInvokeTrayIconWithAutomation(terms, item.Name) && WaitForExistingInstanceActivation(item.FullPath, ownerHandle, 2500))
        {
            LoggingService.LogDebug($"[CtrlQ Tray] uia-success name={item.Name}");
            return true;
        }

        if (TryClickTrayIcon(terms) && WaitForExistingInstanceActivation(item.FullPath, ownerHandle, 3000))
        {
            LoggingService.LogDebug($"[CtrlQ Tray] click-success name={item.Name}");
            return true;
        }

        LoggingService.LogDebug($"[CtrlQ Tray] click-failed-or-no-window name={item.Name}; existing tray process will not be shell-started");
        return false;
    }

    private static bool TrySendWeChatTrayActivation(string fullPath)
    {
        var activationPath = ResolveActivationPath(fullPath);
        var processName = System.IO.Path.GetFileNameWithoutExtension(activationPath ?? fullPath) ?? string.Empty;
        if (!IsWeChatProcessName(processName))
        {
            return false;
        }

        var sent = false;
        foreach (var candidate in FindWeChatTrayWindows(fullPath))
        {
            LoggingService.LogDebug($"[CtrlQ WeChatTrayCallback] send pid={candidate.ProcessId} handle={FormatHandle(candidate.Handle)} class={SanitizeLogValue(candidate.ClassName)} title={SanitizeLogValue(candidate.Title)}");
            PostQtTrayActivationMessages(candidate.Handle);
            sent = true;
        }

        LoggingService.LogDebug($"[CtrlQ WeChatTrayCallback] sent={sent} path={fullPath}");
        return sent;
    }

    private static bool TryInvokeTrayIconWithAutomation(string[] terms, string itemName)
    {
        var result = false;
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = TryInvokeTrayIconWithAutomationCore(terms, itemName);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(1800))
        {
            LoggingService.LogDebug($"[CtrlQ TrayUIA] sta-timeout name={itemName}");
            return false;
        }

        if (failure != null)
        {
            LoggingService.LogDebug($"[CtrlQ TrayUIA] sta-failed name={itemName} {failure.GetType().Name}: {failure.Message}");
            return false;
        }

        return result;
    }

    private static bool TryInvokeTrayIconWithAutomationCore(string[] terms, string itemName)
    {
        System.Windows.Automation.AutomationElement overflowRoot = null;
        var openedByUs = false;
        var invoked = false;
        try
        {
            var directTrayButton = FindTrayIconAutomationButtonInTrayRoots(terms);
            if (directTrayButton != null)
            {
                LoggingService.LogDebug($"[CtrlQ TrayUIA] direct-button name={SanitizeLogValue(directTrayButton.Current.Name)} class={SanitizeLogValue(directTrayButton.Current.ClassName)} rect={FormatAutomationRect(directTrayButton.Current.BoundingRectangle)}");
                invoked = InvokeAutomationElement(directTrayButton, "tray-direct-button");
                return invoked;
            }

            overflowRoot = FindTrayOverflowAutomationRoot();
            if (overflowRoot == null)
            {
                if (!TryOpenTrayOverflowWithAutomation())
                {
                    LoggingService.LogDebug($"[CtrlQ TrayUIA] overflow-open-failed name={itemName}");
                    return false;
                }

                openedByUs = true;
                overflowRoot = WaitForTrayOverflowAutomationRoot(650);
            }

            if (overflowRoot == null)
            {
                LoggingService.LogDebug($"[CtrlQ TrayUIA] overflow-root-not-found name={itemName}");
                return false;
            }

            LoggingService.LogDebug($"[CtrlQ TrayUIA] overflow-root name={SanitizeLogValue(overflowRoot.Current.Name)} class={SanitizeLogValue(overflowRoot.Current.ClassName)} rect={FormatAutomationRect(overflowRoot.Current.BoundingRectangle)}");
            var weChatButton = WaitForTrayIconAutomationButton(overflowRoot, terms, 650);
            if (weChatButton == null)
            {
                LoggingService.LogDebug($"[CtrlQ TrayUIA] button-not-found name={itemName} terms={FormatTerms(terms)}");
                return false;
            }

            LoggingService.LogDebug($"[CtrlQ TrayUIA] button name={SanitizeLogValue(weChatButton.Current.Name)} class={SanitizeLogValue(weChatButton.Current.ClassName)} rect={FormatAutomationRect(weChatButton.Current.BoundingRectangle)}");
            invoked = InvokeAutomationElement(weChatButton, "tray-button");
            return invoked;
        }
        catch (Exception ex)
        {
            LoggingService.LogDebug($"[CtrlQ TrayUIA] failed name={itemName} {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (openedByUs)
            {
                TryCloseTrayOverflowAutomationRoot(overflowRoot ?? FindTrayOverflowAutomationRoot(), invoked ? "after-invoke" : "cleanup");
            }
        }
    }

    private static bool TryOpenTrayOverflowWithAutomation()
    {
        foreach (var trayRootHandle in EnumerateTrayRootWindows())
        {
            if (trayRootHandle == IntPtr.Zero)
            {
                continue;
            }

            var trayRoot = System.Windows.Automation.AutomationElement.FromHandle(trayRootHandle);
            if (trayRoot == null)
            {
                continue;
            }

            var descendants = trayRoot.FindAll(
                System.Windows.Automation.TreeScope.Descendants,
                System.Windows.Automation.Condition.TrueCondition);
            for (var i = 0; i < descendants.Count; i++)
            {
                var element = descendants[i];
                var name = element.Current.Name ?? string.Empty;
                if (!IsTrayOverflowButtonName(name))
                {
                    continue;
                }

                LoggingService.LogDebug($"[CtrlQ TrayUIA] overflow-button handle={FormatHandle(trayRootHandle)} name={SanitizeLogValue(name)} class={SanitizeLogValue(element.Current.ClassName)} rect={FormatAutomationRect(element.Current.BoundingRectangle)}");
                return InvokeAutomationElement(element, "open-overflow");
            }
        }

        return false;
    }

    private static System.Windows.Automation.AutomationElement FindTrayOverflowAutomationRoot()
    {
        var children = System.Windows.Automation.AutomationElement.RootElement.FindAll(
            System.Windows.Automation.TreeScope.Children,
            System.Windows.Automation.Condition.TrueCondition);
        for (var i = 0; i < children.Count; i++)
        {
            var element = children[i];
            var className = element.Current.ClassName ?? string.Empty;
            var name = element.Current.Name ?? string.Empty;
            if (className.IndexOf("TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("系统托盘溢出", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("tray overflow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return element;
            }
        }

        return null;
    }

    private static System.Windows.Automation.AutomationElement WaitForTrayOverflowAutomationRoot(int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        var attempts = 0;
        do
        {
            attempts++;
            var root = FindTrayOverflowAutomationRoot();
            if (root != null)
            {
                LoggingService.LogDebug($"[CtrlQ TrayUIA] overflow-root-ready attempts={attempts}");
                return root;
            }

            Thread.Sleep(50);
        }
        while (DateTime.UtcNow < deadline);

        LoggingService.LogDebug($"[CtrlQ TrayUIA] overflow-root-timeout attempts={attempts} timeoutMs={timeoutMilliseconds}");
        return null;
    }

    private static void TryCloseTrayOverflowAutomationRoot(System.Windows.Automation.AutomationElement overflowRoot, string reason)
    {
        if (overflowRoot == null)
        {
            return;
        }

        try
        {
            var nativeWindowHandle = overflowRoot.Current.NativeWindowHandle;
            if (nativeWindowHandle == 0)
            {
                LoggingService.LogDebug($"[CtrlQ TrayUIA] close-skip-no-hwnd reason={reason}");
                return;
            }

            var handle = new IntPtr(nativeWindowHandle);
            PostMessage(handle, WmClose, UIntPtr.Zero, IntPtr.Zero);
            LoggingService.LogDebug($"[CtrlQ TrayUIA] close-overflow reason={reason} handle={FormatHandle(handle)}");
        }
        catch (Exception ex)
        {
            LoggingService.LogDebug($"[CtrlQ TrayUIA] close-failed reason={reason} {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static System.Windows.Automation.AutomationElement FindTrayIconAutomationButton(System.Windows.Automation.AutomationElement root, string[] terms)
    {
        if (root == null || terms == null || terms.Length == 0)
        {
            return null;
        }

        var descendants = root.FindAll(
            System.Windows.Automation.TreeScope.Descendants,
            System.Windows.Automation.Condition.TrueCondition);
        LoggingService.LogDebug($"[CtrlQ TrayUIA] scan-descendants count={descendants.Count} terms={FormatTerms(terms)}");
        var logged = 0;
        for (var i = 0; i < descendants.Count; i++)
        {
            var element = descendants[i];
            var name = element.Current.Name ?? string.Empty;
            var className = element.Current.ClassName ?? string.Empty;
            var controlType = element.Current.ControlType;
            if (logged < MaxTrayLogItems)
            {
                LoggingService.LogDebug($"[CtrlQ TrayUIA] item[{i}] name={SanitizeLogValue(name)} class={SanitizeLogValue(className)} type={controlType?.ProgrammaticName} rect={FormatAutomationRect(element.Current.BoundingRectangle)}");
                logged++;
            }

            if (controlType != System.Windows.Automation.ControlType.Button
                || className.IndexOf("SystemTray", StringComparison.OrdinalIgnoreCase) < 0
                || !terms.Any(term => name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                continue;
            }

            return element;
        }

        return null;
    }

    private static System.Windows.Automation.AutomationElement FindTrayIconAutomationButtonInTrayRoots(string[] terms)
    {
        if (terms == null || terms.Length == 0)
        {
            return null;
        }

        foreach (var trayRootHandle in EnumerateTrayRootWindows())
        {
            if (trayRootHandle == IntPtr.Zero)
            {
                continue;
            }

            var trayRoot = System.Windows.Automation.AutomationElement.FromHandle(trayRootHandle);
            if (trayRoot == null)
            {
                continue;
            }

            LoggingService.LogDebug($"[CtrlQ TrayUIA] direct-scan root={DescribeWindowHandle(trayRootHandle)} terms={FormatTerms(terms)}");
            var button = FindTrayIconAutomationButton(trayRoot, terms);
            if (button != null)
            {
                return button;
            }
        }

        return null;
    }

    private static System.Windows.Automation.AutomationElement WaitForTrayIconAutomationButton(System.Windows.Automation.AutomationElement root, string[] terms, int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        var attempts = 0;
        var currentRoot = root;
        do
        {
            attempts++;
            currentRoot = FindTrayOverflowAutomationRoot() ?? currentRoot;
            if (currentRoot == null)
            {
                LoggingService.LogDebug($"[CtrlQ TrayUIA] button-wait-root-missing attempt={attempts}");
                Thread.Sleep(60);
                continue;
            }

            var button = FindTrayIconAutomationButton(currentRoot, terms);
            if (button != null)
            {
                LoggingService.LogDebug($"[CtrlQ TrayUIA] button-ready attempts={attempts}");
                return button;
            }

            Thread.Sleep(60);
        }
        while (DateTime.UtcNow < deadline);

        LoggingService.LogDebug($"[CtrlQ TrayUIA] button-timeout attempts={attempts} timeoutMs={timeoutMilliseconds} terms={FormatTerms(terms)}");
        return null;
    }

    private static bool InvokeAutomationElement(System.Windows.Automation.AutomationElement element, string reason)
    {
        if (element == null)
        {
            return false;
        }

        object pattern;
        if (element.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out pattern)
            && pattern is System.Windows.Automation.InvokePattern invokePattern)
        {
            invokePattern.Invoke();
            LoggingService.LogDebug($"[CtrlQ TrayUIA] invoke-success reason={reason}");
            return true;
        }

        var rect = element.Current.BoundingRectangle;
        if (!rect.IsEmpty)
        {
            var x = (int)(rect.Left + (rect.Width / 2));
            var y = (int)(rect.Top + (rect.Height / 2));
            if (TryPostDoubleClickAtPhysicalPoint(x, y, reason, out var targetDescription))
            {
                LoggingService.LogDebug($"[CtrlQ TrayUIA] message-fallback-success reason={reason} target={targetDescription}");
                return true;
            }
        }

        LoggingService.LogDebug($"[CtrlQ TrayUIA] invoke-unavailable reason={reason}");
        return false;
    }

    private static bool TryClickWeChatTrayIconByRect(string fullPath)
    {
        var trayWindows = FindWeChatTrayWindows(fullPath).ToList();
        if (trayWindows.Count == 0)
        {
            LoggingService.LogDebug($"[CtrlQ WeChatTrayRect] no-wechat-tray-window path={fullPath}");
            return false;
        }

        if (TryClickWeChatTrayIconByRect(trayWindows, "initial"))
        {
            return true;
        }

        if (TryOpenTrayOverflowWithWin32())
        {
            Thread.Sleep(200);
            if (TryClickWeChatTrayIconByRect(trayWindows, "overflow-opened"))
            {
                return true;
            }
        }
        else
        {
            LoggingService.LogDebug("[CtrlQ WeChatTrayRect] overflow-open-failed");
        }

        LoggingService.LogDebug($"[CtrlQ WeChatTrayRect] failed path={fullPath} candidates={trayWindows.Count}");
        return false;
    }

    private static bool TryClickWeChatTrayIconByRect(IReadOnlyCollection<WeChatTrayWindowCandidate> trayWindows, string stage)
    {
        var iconIds = new uint[] { 1, 0, 2 };
        foreach (var candidate in trayWindows)
        {
            foreach (var iconId in iconIds)
            {
                if (!TryGetNotifyIconRect(candidate.Handle, iconId, out var rect, out var hr))
                {
                    LoggingService.LogDebug($"[CtrlQ WeChatTrayRect] stage={stage} miss handle={FormatHandle(candidate.Handle)} iconId={iconId} hr=0x{hr:X8} class={SanitizeLogValue(candidate.ClassName)}");
                    continue;
                }

                var x = rect.Left + ((rect.Right - rect.Left) / 2);
                var y = rect.Top + ((rect.Bottom - rect.Top) / 2);
                if (TryPostDoubleClickAtPhysicalPoint(x, y, "wechat-notifyicon-rect", out var targetDescription))
                {
                    LoggingService.LogDebug(
                        $"[CtrlQ WeChatTrayRect] stage={stage} posted handle={FormatHandle(candidate.Handle)} iconId={iconId} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} target={targetDescription}");
                    return true;
                }

                LoggingService.LogDebug(
                    $"[CtrlQ WeChatTrayRect] stage={stage} hit-without-target handle={FormatHandle(candidate.Handle)} iconId={iconId} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} target={targetDescription}");
            }
        }

        return false;
    }

    private static bool TryGetNotifyIconRect(IntPtr iconWindowHandle, uint iconId, out RECT rect, out int hresult)
    {
        var identifier = new NOTIFYICONIDENTIFIER
        {
            CbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)),
            HWnd = iconWindowHandle,
            UID = iconId,
            GuidItem = Guid.Empty
        };

        hresult = Shell_NotifyIconGetRect(ref identifier, out rect);
        return hresult == NotifyIconSuccess
               && rect.Right > rect.Left
               && rect.Bottom > rect.Top;
    }

    private static IReadOnlyList<WeChatTrayWindowCandidate> FindWeChatTrayWindows(string fullPath)
    {
        var candidates = new List<WeChatTrayWindowCandidate>();
        using (var processes = new ProcessListScope(FindExistingProcesses(fullPath)))
        {
            foreach (var process in processes.Processes)
            {
                foreach (var handle in EnumerateProcessWindows(process))
                {
                    var className = GetWindowClassName(handle);
                    var title = GetWindowTitle(handle);
                    if (!IsWeChatQtTrayWindow(className, title))
                    {
                        continue;
                    }

                    candidates.Add(new WeChatTrayWindowCandidate(
                        handle,
                        SafeProcessId(process),
                        className,
                        title));
                }
            }
        }

        LoggingService.LogDebug($"[CtrlQ WeChatTrayWindow] count={candidates.Count} path={fullPath} items={string.Join(";", candidates.Select(DescribeWeChatTrayCandidate))}");
        return candidates;
    }

    private static IEnumerable<IntPtr> EnumerateProcessWindows(Process process)
    {
        var processId = SafeProcessId(process);
        if (processId <= 0)
        {
            yield break;
        }

        var handles = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var windowProcessId);
            if (windowProcessId == processId)
            {
                handles.Add(handle);
            }

            return true;
        }, IntPtr.Zero);

        foreach (var handle in handles)
        {
            yield return handle;
        }
    }

    private static bool IsWeChatQtTrayWindow(string className, string title)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        if (className.IndexOf("WxTrayIconMessageWindowClass", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return className.IndexOf("QWindowIcon", StringComparison.OrdinalIgnoreCase) >= 0
               && (string.Equals(title, "Weixin", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(title, "WeChat", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(title, "微信", StringComparison.OrdinalIgnoreCase)
                   || string.IsNullOrWhiteSpace(title));
    }

    private static void PostQtTrayActivationMessages(IntPtr handle)
    {
        var messages = new[] { WmLButtonUp, WmLButtonDblClk, NinSelect, NinKeySelect };
        var iconIds = new[] { 0, 1 };
        var sent = 0;
        foreach (var iconId in iconIds)
        {
            foreach (var message in messages)
            {
                PostMessage(handle, QtTrayNotifyMessage, new UIntPtr((uint)iconId), new IntPtr((int)message));
                PostMessage(handle, QtTrayNotifyMessage, UIntPtr.Zero, MakeLParam((int)message, iconId));
                sent += 2;
            }
        }

        LoggingService.LogDebug($"[CtrlQ WeChatTrayCallback] posted handle={FormatHandle(handle)} variants={sent}");
    }

    private static bool WaitForExistingInstanceActivation(string fullPath, IntPtr ownerHandle, int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        var attempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempts++;
            using (var processes = new ProcessListScope(FindExistingProcesses(fullPath)))
            {
                foreach (var process in processes.Processes)
                {
                    if (TryBringProcessToFront(process, ownerHandle))
                    {
                        LoggingService.LogDebug($"[CtrlQ WaitWindow] success path={fullPath} attempts={attempts} pid={process.Id}");
                        return true;
                    }
                }
            }

            Thread.Sleep(120);
        }

        RestoreOwnerFocus(ownerHandle);
        LoggingService.LogDebug($"[CtrlQ WaitWindow] timeout path={fullPath} attempts={attempts} timeoutMs={timeoutMilliseconds}");
        return false;
    }

    private static bool IsKnownSingleInstanceTrayApp(string fullPath)
    {
        var activationPath = ResolveActivationPath(fullPath);
        var processName = System.IO.Path.GetFileNameWithoutExtension(activationPath ?? fullPath) ?? string.Empty;
        return processName.Equals("QQ", StringComparison.OrdinalIgnoreCase)
               || IsWeChatProcessName(processName)
               || processName.Equals("cc-switch", StringComparison.OrdinalIgnoreCase)
               || processName.Equals("clash-verge", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] GetTrayActivationTerms(string fullPath)
    {
        var activationPath = ResolveActivationPath(fullPath);
        var processName = System.IO.Path.GetFileNameWithoutExtension(activationPath ?? fullPath) ?? string.Empty;
        if (processName.Equals("QQ", StringComparison.OrdinalIgnoreCase)) return new[] { "QQ", "腾讯QQ", "Tencent", "QQNT" };
        if (IsWeChatProcessName(processName)) return new[] { "微信", "Weixin", "WeChat", "WeChatAppEx" };
        if (processName.Equals("cc-switch", StringComparison.OrdinalIgnoreCase)) return new[] { "CC Switch", "cc-switch", "ccswitch" };
        if (processName.Equals("clash-verge", StringComparison.OrdinalIgnoreCase)) return new[] { "Clash Verge", "clash-verge", "Verge" };
        return Array.Empty<string>();
    }

    private static bool IsWeChatProcessName(string processName)
    {
        return processName.Equals("Weixin", StringComparison.OrdinalIgnoreCase)
               || processName.Equals("WeChat", StringComparison.OrdinalIgnoreCase)
               || processName.Equals("WeChatAppEx", StringComparison.OrdinalIgnoreCase);
    }

    private static void RestoreOwnerFocus(IntPtr ownerHandle)
    {
        if (ownerHandle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(ownerHandle, SwShow);
        BringWindowToTop(ownerHandle);
        SetForegroundWindow(ownerHandle);
    }

    private static bool TryClickTrayIcon(string[] terms)
    {
        if (terms == null || terms.Length == 0)
        {
            LoggingService.LogDebug("[CtrlQ TrayClick] skipped because no match terms were provided");
            return false;
        }

        LoggingService.LogDebug($"[CtrlQ TrayClick] start terms={FormatTerms(terms)}");
        if (TryClickTrayIconWithToolbar(terms, "initial"))
        {
            LoggingService.LogDebug($"[CtrlQ TrayClick] toolbar-success stage=initial terms={FormatTerms(terms)}");
            return true;
        }

        if (TryOpenTrayOverflowWithWin32())
        {
            Thread.Sleep(200);
            if (TryClickTrayIconWithToolbar(terms, "overflow-opened"))
            {
                LoggingService.LogDebug($"[CtrlQ TrayClick] toolbar-success stage=overflow-opened terms={FormatTerms(terms)}");
                return true;
            }
        }
        else
        {
            LoggingService.LogDebug("[CtrlQ TrayClick] win32 overflow-open failed or no overflow button was found");
        }

        LoggingService.LogDebug($"[CtrlQ TrayClick] failed terms={FormatTerms(terms)}");
        return false;
    }

    private static bool TryClickTrayIconWithToolbar(string[] terms, string stage)
    {
        foreach (var toolbarHandle in EnumerateTrayToolbarWindows(stage))
        {
            if (TryClickToolbarButton(toolbarHandle, terms))
            {
                LoggingService.LogDebug($"[CtrlQ TrayClick] toolbar-success stage={stage} handle={FormatHandle(toolbarHandle)} terms={FormatTerms(terms)}");
                return true;
            }
        }

        return false;
    }

    private static bool TryOpenTrayOverflowWithWin32()
    {
        var trayHandle = FindWindow("Shell_TrayWnd", null);
        if (trayHandle == IntPtr.Zero)
        {
            LoggingService.LogDebug("[CtrlQ TrayOverflowWin32] Shell_TrayWnd not found");
            return false;
        }

        var buttons = new List<IntPtr>();
        EnumChildWindows(trayHandle, (handle, _) =>
        {
            var className = GetWindowClassName(handle);
            if (string.Equals(className, "Button", StringComparison.OrdinalIgnoreCase))
            {
                buttons.Add(handle);
            }

            return true;
        }, IntPtr.Zero);

        LoggingService.LogDebug($"[CtrlQ TrayOverflowWin32] tray={DescribeWindowHandle(trayHandle)} buttonCount={buttons.Count}");
        for (var i = 0; i < buttons.Count; i++)
        {
            var buttonHandle = buttons[i];
            var title = GetWindowTitle(buttonHandle);
            LoggingService.LogDebug($"[CtrlQ TrayOverflowWin32] button[{i}] {DescribeWindowHandle(buttonHandle)}");
            if (!IsTrayOverflowButtonName(title))
            {
                continue;
            }

            LoggingService.LogDebug($"[CtrlQ TrayOverflowWin32] match index={i} title={SanitizeLogValue(title)}");
            return ClickTrayOverflowButton(buttonHandle, "named");
        }

        var visibleFallback = buttons.FirstOrDefault(IsWindowVisible);
        if (visibleFallback != IntPtr.Zero)
        {
            LoggingService.LogDebug($"[CtrlQ TrayOverflowWin32] fallback-visible-button {DescribeWindowHandle(visibleFallback)}");
            return ClickTrayOverflowButton(visibleFallback, "visible-fallback");
        }

        return false;
    }

    private static bool ClickTrayOverflowButton(IntPtr buttonHandle, string reason)
    {
        if (buttonHandle == IntPtr.Zero)
        {
            return false;
        }

        SendMessage(buttonHandle, BmClick, UIntPtr.Zero, IntPtr.Zero);
        LoggingService.LogDebug($"[CtrlQ TrayOverflowWin32] click-by-message reason={reason} handle={FormatHandle(buttonHandle)}");
        return true;
    }

    private static bool IsTrayOverflowButtonName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.IndexOf("显示隐藏", StringComparison.OrdinalIgnoreCase) >= 0
               || name.IndexOf("隐藏的图标", StringComparison.OrdinalIgnoreCase) >= 0
               || name.IndexOf("Show hidden icons", StringComparison.OrdinalIgnoreCase) >= 0
               || name.IndexOf("Hidden icons", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IEnumerable<IntPtr> EnumerateTrayRootWindows()
    {
        var roots = new List<IntPtr>
        {
            FindWindow("Shell_TrayWnd", null),
            FindWindow("NotifyIconOverflowWindow", null)
        };

        EnumWindows((handle, _) =>
        {
            var className = GetWindowClassName(handle);
            if (string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal)
                || string.Equals(className, "NotifyIconOverflowWindow", StringComparison.Ordinal))
            {
                roots.Add(handle);
            }

            return true;
        }, IntPtr.Zero);

        foreach (var root in roots.Where(handle => handle != IntPtr.Zero).Distinct())
        {
            yield return root;
        }
    }

    private static IEnumerable<IntPtr> EnumerateTrayToolbarWindows(string stage)
    {
        var roots = EnumerateTrayRootWindows().ToList();
        LoggingService.LogDebug($"[CtrlQ TrayToolbar] stage={stage} roots={string.Join(",", roots.Select(DescribeWindowHandle))}");
        foreach (var root in roots)
        {
            var handles = new List<IntPtr>();
            EnumChildWindows(root, (handle, _) =>
            {
                if (string.Equals(GetWindowClassName(handle), "ToolbarWindow32", StringComparison.Ordinal))
                {
                    handles.Add(handle);
                }

                return true;
            }, IntPtr.Zero);

            LoggingService.LogDebug($"[CtrlQ TrayToolbar] stage={stage} root={DescribeWindowHandle(root)} toolbarCount={handles.Count}");
            foreach (var handle in handles)
            {
                yield return handle;
            }
        }
    }

    private static bool TryClickToolbarButton(IntPtr toolbarHandle, string[] terms)
    {
        var count = (int)SendMessage(toolbarHandle, TbButtonCount, UIntPtr.Zero, IntPtr.Zero);
        LoggingService.LogDebug($"[CtrlQ TrayToolbar] inspect handle={FormatHandle(toolbarHandle)} count={count} terms={FormatTerms(terms)}");
        if (count <= 0)
        {
            return false;
        }

        var processHandle = OpenProcessForWindow(toolbarHandle);
        if (processHandle == IntPtr.Zero)
        {
            LoggingService.LogDebug($"[CtrlQ TrayToolbar] open-process-failed handle={FormatHandle(toolbarHandle)}");
            return false;
        }

        var loggedButtons = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (!TryGetToolbarButtonCommandId(toolbarHandle, processHandle, i, out var commandId))
                {
                    LoggingService.LogDebug($"[CtrlQ TrayToolbar] get-button-failed handle={FormatHandle(toolbarHandle)} index={i}");
                    continue;
                }

                var text = GetToolbarButtonText(toolbarHandle, processHandle, commandId);
                if (!string.IsNullOrWhiteSpace(text) && loggedButtons < MaxTrayLogItems)
                {
                    LoggingService.LogDebug($"[CtrlQ TrayToolbar] handle={FormatHandle(toolbarHandle)} button[{i}] command={commandId} text={SanitizeLogValue(text)}");
                    loggedButtons++;
                }

                if (string.IsNullOrWhiteSpace(text) || !terms.Any(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                if (TryGetToolbarButtonRect(toolbarHandle, processHandle, i, out var rect))
                {
                    LoggingService.LogDebug(
                        $"[CtrlQ TrayToolbar] match handle={FormatHandle(toolbarHandle)} index={i} command={commandId} text={SanitizeLogValue(text)} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
                    var x = rect.Left + ((rect.Right - rect.Left) / 2);
                    var y = rect.Top + ((rect.Bottom - rect.Top) / 2);
                    DoubleClickClientPoint(toolbarHandle, x, y);
                    return true;
                }

                LoggingService.LogDebug($"[CtrlQ TrayToolbar] match-without-rect handle={FormatHandle(toolbarHandle)} index={i} command={commandId} text={SanitizeLogValue(text)}");
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }

        LoggingService.LogDebug($"[CtrlQ TrayToolbar] no-match handle={FormatHandle(toolbarHandle)} count={count} loggedButtons={loggedButtons}");
        return false;
    }

    private static IntPtr OpenProcessForWindow(IntPtr windowHandle)
    {
        GetWindowThreadProcessId(windowHandle, out var processId);
        return processId <= 0
            ? IntPtr.Zero
            : OpenProcess(ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, processId);
    }

    private static bool TryGetToolbarButtonCommandId(IntPtr toolbarHandle, IntPtr processHandle, int index, out int commandId)
    {
        commandId = 0;
        var buttonSize = IntPtr.Size == 8 ? 32 : 20;
        var remoteBuffer = VirtualAllocEx(processHandle, IntPtr.Zero, new UIntPtr((uint)buttonSize), MemCommit | MemReserve, PageReadWrite);
        if (remoteBuffer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (SendMessage(toolbarHandle, TbGetButton, new UIntPtr((uint)index), remoteBuffer) == IntPtr.Zero)
            {
                return false;
            }

            var bytes = ReadRemoteBytes(processHandle, remoteBuffer, buttonSize);
            if (bytes == null || bytes.Length < 8)
            {
                return false;
            }

            commandId = BitConverter.ToInt32(bytes, 4);
            return commandId != 0;
        }
        finally
        {
            VirtualFreeEx(processHandle, remoteBuffer, UIntPtr.Zero, MemRelease);
        }
    }

    private static string GetToolbarButtonText(IntPtr toolbarHandle, IntPtr processHandle, int commandId)
    {
        const int bufferBytes = 1024;
        var remoteBuffer = VirtualAllocEx(processHandle, IntPtr.Zero, new UIntPtr((uint)bufferBytes), MemCommit | MemReserve, PageReadWrite);
        if (remoteBuffer == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var length = SendMessage(toolbarHandle, TbGetButtonTextW, new UIntPtr((uint)commandId), remoteBuffer).ToInt64();
            if (length <= 0)
            {
                return string.Empty;
            }

            var bytesToRead = (int)Math.Min(bufferBytes, (length + 1) * 2);
            var bytes = ReadRemoteBytes(processHandle, remoteBuffer, bytesToRead);
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var text = Encoding.Unicode.GetString(bytes);
            var nullIndex = text.IndexOf('\0');
            return nullIndex >= 0 ? text.Substring(0, nullIndex) : text;
        }
        finally
        {
            VirtualFreeEx(processHandle, remoteBuffer, UIntPtr.Zero, MemRelease);
        }
    }

    private static bool TryGetToolbarButtonRect(IntPtr toolbarHandle, IntPtr processHandle, int index, out RECT rect)
    {
        rect = default;
        var rectSize = Marshal.SizeOf(typeof(RECT));
        var remoteBuffer = VirtualAllocEx(processHandle, IntPtr.Zero, new UIntPtr((uint)rectSize), MemCommit | MemReserve, PageReadWrite);
        if (remoteBuffer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (SendMessage(toolbarHandle, TbGetItemRect, new UIntPtr((uint)index), remoteBuffer) == IntPtr.Zero)
            {
                return false;
            }

            var bytes = ReadRemoteBytes(processHandle, remoteBuffer, rectSize);
            if (bytes == null || bytes.Length < rectSize)
            {
                return false;
            }

            rect = new RECT
            {
                Left = BitConverter.ToInt32(bytes, 0),
                Top = BitConverter.ToInt32(bytes, 4),
                Right = BitConverter.ToInt32(bytes, 8),
                Bottom = BitConverter.ToInt32(bytes, 12)
            };
            return true;
        }
        finally
        {
            VirtualFreeEx(processHandle, remoteBuffer, UIntPtr.Zero, MemRelease);
        }
    }

    private static byte[] ReadRemoteBytes(IntPtr processHandle, IntPtr address, int bytesToRead)
    {
        var buffer = new byte[bytesToRead];
        return ReadProcessMemory(processHandle, address, buffer, bytesToRead, out var bytesRead) && bytesRead.ToInt64() > 0
            ? buffer
            : null;
    }

    private static void DoubleClickClientPoint(IntPtr handle, int x, int y)
    {
        var lParam = MakeLParam(x, y);
        LoggingService.LogDebug($"[CtrlQ WindowMessage] double-click handle={FormatHandle(handle)} x={x} y={y}");
        PostMessage(handle, WmMouseMove, UIntPtr.Zero, lParam);
        PostMessage(handle, WmLButtonDown, new UIntPtr(MkLButton), lParam);
        PostMessage(handle, WmLButtonUp, UIntPtr.Zero, lParam);
        PostMessage(handle, WmLButtonDblClk, new UIntPtr(MkLButton), lParam);
        PostMessage(handle, WmLButtonUp, UIntPtr.Zero, lParam);
    }

    private static bool PostDoubleClickToWindowChain(IntPtr startHandle, int screenX, int screenY, string reason)
    {
        if (startHandle == IntPtr.Zero)
        {
            LoggingService.LogDebug($"[CtrlQ WindowMessage] skip-empty-target reason={reason} screen={screenX},{screenY}");
            return false;
        }

        var handle = startHandle;
        var posted = false;
        var visited = new HashSet<IntPtr>();
        for (var i = 0; i < 6 && handle != IntPtr.Zero && visited.Add(handle); i++)
        {
            var point = new POINT { X = screenX, Y = screenY };
            if (ScreenToClient(handle, ref point))
            {
                LoggingService.LogDebug($"[CtrlQ WindowMessage] double-click-screen reason={reason} target={DescribeWindowHandle(handle)} screen={screenX},{screenY} client={point.X},{point.Y}");
                DoubleClickClientPoint(handle, point.X, point.Y);
                posted = true;
            }

            handle = GetParent(handle);
        }

        return posted;
    }

    private static bool TryPostDoubleClickAtPhysicalPoint(int screenX, int screenY, string reason, out string targetDescription)
    {
        var previousDpiContext = TrySetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        try
        {
            var target = WindowFromPoint(new POINT { X = screenX, Y = screenY });
            targetDescription = DescribeWindowHandle(target);
            return PostDoubleClickToWindowChain(target, screenX, screenY, reason);
        }
        finally
        {
            if (previousDpiContext != IntPtr.Zero)
            {
                TrySetThreadDpiAwarenessContext(previousDpiContext);
            }
        }
    }

    private static IntPtr TrySetThreadDpiAwarenessContext(IntPtr dpiContext)
    {
        try
        {
            return SetThreadDpiAwarenessContext(dpiContext);
        }
        catch (EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr MakeLParam(int lowWord, int highWord)
    {
        return new IntPtr((highWord << 16) | (lowWord & 0xFFFF));
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process?.Id ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeProcessName(Process process)
    {
        try
        {
            return process?.ProcessName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatTerms(IEnumerable<string> terms)
    {
        return string.Join("|", (terms ?? Array.Empty<string>()).Select(SanitizeLogValue));
    }

    private static string FormatHandle(IntPtr handle)
    {
        return $"0x{handle.ToInt64():X}";
    }

    private static string FormatAutomationRect(Rect rect)
    {
        return rect.IsEmpty
            ? "Empty"
            : $"{rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0}";
    }

    private static string DescribeWindowHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return "0x0";
        }

        return $"{FormatHandle(handle)} class={SanitizeLogValue(GetWindowClassName(handle))} title={SanitizeLogValue(GetWindowTitle(handle))} visible={IsWindowVisible(handle)}";
    }

    private static string DescribeWeChatTrayCandidate(WeChatTrayWindowCandidate candidate)
    {
        return $"{FormatHandle(candidate.Handle)} pid={candidate.ProcessId} class={SanitizeLogValue(candidate.ClassName)} title={SanitizeLogValue(candidate.Title)}";
    }

    private static string SanitizeLogValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static WindowActivationCandidate FindBestProcessWindow(Process process)
    {
        try
        {
            process.Refresh();
            var processName = process.ProcessName ?? string.Empty;
            var processId = process.Id;
            var best = WindowActivationCandidate.Empty;
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var windowProcessId);
                if (windowProcessId == processId)
                {
                    var candidate = BuildWindowActivationCandidate(processName, handle, process.MainWindowHandle);
                    if (candidate.Score > best.Score)
                    {
                        best = candidate;
                    }
                }

                return true;
            }, IntPtr.Zero);

            if (best.Handle == IntPtr.Zero && process.MainWindowHandle != IntPtr.Zero)
            {
                best = BuildWindowActivationCandidate(processName, process.MainWindowHandle, process.MainWindowHandle);
            }

            return best.Score > 0 ? best : WindowActivationCandidate.Empty;
        }
        catch
        {
            return WindowActivationCandidate.Empty;
        }
    }

    private static WindowActivationCandidate BuildWindowActivationCandidate(string processName, IntPtr handle, IntPtr mainWindowHandle)
    {
        if (handle == IntPtr.Zero)
        {
            return WindowActivationCandidate.Empty;
        }

        var className = GetWindowClassName(handle);
        var title = GetWindowTitle(handle);
        if (IsIgnoredActivationWindow(className, title))
        {
            return WindowActivationCandidate.Empty;
        }

        var style = GetWindowLong(handle, GwlStyle);
        var exStyle = GetWindowLong(handle, GwlExStyle);
        if ((style & WsChild) != 0)
        {
            return WindowActivationCandidate.Empty;
        }

        var visible = IsWindowVisible(handle);
        var iconic = IsIconic(handle);
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var owner = GetWindow(handle, GwOwner);
        var isOwnerless = owner == IntPtr.Zero;
        var score = 0;

        score += visible ? 260 : -80;
        score += iconic ? 60 : (visible ? 40 : 0);
        score += hasTitle ? 160 : -40;
        score += isOwnerless ? 100 : -80;

        if (handle == mainWindowHandle) score += 180;
        if ((exStyle & WsExAppWindow) != 0) score += 80;
        if ((exStyle & WsExToolWindow) != 0) score -= 180;
        if ((exStyle & WsExNoActivate) != 0) score -= 220;
        if ((style & WsDisabled) != 0) score -= 100;
        if (IsLikelyApplicationWindowClass(className)) score += 70;
        if (TitleContainsProcessName(title, processName)) score += 80;

        var preferAggressive = processName.Equals("QQ", StringComparison.OrdinalIgnoreCase)
                               || IsWeChatProcessName(processName);
        return score > 0
            ? new WindowActivationCandidate(handle, score, preferAggressive, visible, iconic, title, className)
            : WindowActivationCandidate.Empty;
    }

    private static bool IsIgnoredActivationWindow(string className, string title)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return true;
        }

        return className.Equals("MSCTFIME UI", StringComparison.Ordinal)
               || className.Equals("IME", StringComparison.Ordinal)
               || className.Equals("Tao Thread Event Target", StringComparison.Ordinal)
               || className.Equals("Base_PowerMessageWindow", StringComparison.Ordinal)
               || className.Equals("Chrome_SystemMessageWindow", StringComparison.Ordinal)
               || className.Equals("DisplayICC_SystemMessageWindow", StringComparison.Ordinal)
               || className.Equals("tray_icon_app", StringComparison.Ordinal)
               || className.Equals("Electron_NotifyIconHostWindow", StringComparison.Ordinal)
               || className.Equals("com.ccswitch.desktop-sic", StringComparison.Ordinal)
               || className.IndexOf("CandidateWindow", StringComparison.OrdinalIgnoreCase) >= 0
               || (!string.IsNullOrWhiteSpace(title) && title.StartsWith("GDI+ Window", StringComparison.OrdinalIgnoreCase))
               || string.Equals(title, "Default IME", StringComparison.OrdinalIgnoreCase)
               || string.Equals(title, "Mode Indicator", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyApplicationWindowClass(string className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return className.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0
               || className.IndexOf("Wnd", StringComparison.OrdinalIgnoreCase) >= 0
               || className.IndexOf("QWindow", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TitleContainsProcessName(string title, string processName)
    {
        var normalizedTitle = NormalizeWindowMatchText(title);
        var normalizedProcessName = NormalizeWindowMatchText(processName);
        return normalizedTitle.Length > 0
               && normalizedProcessName.Length > 0
               && normalizedTitle.IndexOf(normalizedProcessName, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string NormalizeWindowMatchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
    private static string GetWindowTitle(IntPtr handle)
    {
        var builder = new StringBuilder(512);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetWindowClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetLaunchWorkingDirectory(string fullPath)
    {
        if (!ShouldLaunchFromItemDirectory(fullPath))
        {
            return null;
        }

        var directory = System.IO.Path.GetDirectoryName(fullPath);
        return Directory.Exists(directory) ? directory : null;
    }

    private static bool ShouldLaunchFromItemDirectory(string fullPath)
    {
        var extension = System.IO.Path.GetExtension(fullPath) ?? string.Empty;
        return extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private void OpenItemFolder(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (File.Exists(fullPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true
            });
            return;
        }

        MessageBox.Show("目标路径不存在。", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenItemTerminal(string fullPath)
    {
        var targetDirectory = GetItemDirectory(fullPath);
        if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
        {
            MessageBox.Show("目标目录不存在。", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            StartPowerShellTerminal(targetDirectory);
            StatusText.Text = "已打开终端：" + targetDirectory;
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开终端：" + ex.Message, "常用启动项", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void StartPowerShellTerminal(string targetDirectory)
    {
        var powerShellPath = ResolvePowerShell7Path();
        var terminalPath = ResolveWindowsTerminalPath();
        var terminalTitle = GetTerminalTitle(targetDirectory);

        if (!string.IsNullOrWhiteSpace(terminalPath))
        {
            var arguments = "new-tab --title \"" + EscapeCommandLineArgument(terminalTitle) + "\" -d \"" + EscapeCommandLineArgument(targetDirectory) + "\" \"" + EscapeCommandLineArgument(powerShellPath) + "\" -NoLogo -NoExit";
            Process.Start(new ProcessStartInfo(terminalPath, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = targetDirectory,
            });
            return;
        }

        var argumentsFallback = "-NoLogo -NoExit";
        Process.Start(new ProcessStartInfo(powerShellPath, argumentsFallback)
        {
            UseShellExecute = true,
            WorkingDirectory = targetDirectory,
        });
    }

    private static string ResolvePowerShell7Path()
    {
        var candidates = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7\pwsh.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\PowerShell\7\pwsh.exe"),
            Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Microsoft\PowerShell\7\pwsh.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramW6432%\PowerShell\7\pwsh.exe"),
        };

        var candidate = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException("未找到 PowerShell 7 的 pwsh.exe。");
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return (value ?? string.Empty).Replace("'", "''");
    }

    private static string ResolveWindowsTerminalPath()
    {
        var candidate = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        return File.Exists(candidate) ? candidate : "wt.exe";
    }

    private static string GetTerminalTitle(string targetDirectory)
    {
        var trimmed = (targetDirectory ?? string.Empty).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var title = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(title) ? targetDirectory : title;
    }

    private static string EscapeCommandLineArgument(string value)
    {
        return (value ?? string.Empty).Replace("\"", "\\\"");
    }

    private static string GetItemDirectory(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        var directory = System.IO.Path.GetDirectoryName(fullPath);
        return Directory.Exists(directory) ? directory : null;
    }

    private void CopyPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        Clipboard.SetText(fullPath);
        StatusText.Text = "路径已复制到剪贴板。";
    }

    private void EnsureGroupExists(string groupName)
    {
        groupName = NormalizeGroupName(groupName);
        if (_groupDefinitions.Any(group => string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _groupDefinitions.Add(new CommonStartupGroup
        {
            Name = groupName,
            Order = _groupDefinitions.Count + 1
        });
    }

    private int GetNextItemOrder(string groupName, StartupItemVm exclude = null)
    {
        groupName = NormalizeGroupName(groupName);
        var maxOrder = _startupItems
            .Where(item => !ReferenceEquals(item, exclude))
            .Where(item => string.Equals(item.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Order)
            .DefaultIfEmpty(0)
            .Max();
        return maxOrder + 1;
    }

    private IEnumerable<string> GetAvailableGroupNames()
    {
        return _groupDefinitions
            .OrderBy(group => group.Order)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Name);
    }

    private string NormalizeGroupName(string groupName)
    {
        return string.IsNullOrWhiteSpace(groupName) ? DefaultGroupName : groupName.Trim();
    }

    private void CreateNewGroup()
    {
        var groupName = NormalizeGroupName(NewGroupNameBox.Text);
        if (_groupDefinitions.Any(group => string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"分组已存在：{groupName}";
            HideNewGroupPanel();
            return;
        }

        _groupDefinitions.Add(new CommonStartupGroup
        {
            Name = groupName,
            Order = _groupDefinitions.Count + 1
        });
        _currentGroupName = groupName;
        SaveItems(allowStructureChange: true);
        HideNewGroupPanel();
        RefreshWorkbench();
        StatusText.Text = $"已创建分组：{groupName}";
    }

    private void ShowNewGroupPanel()
    {
        NewGroupPanel.Visibility = Visibility.Visible;
        NewGroupNameBox.Text = string.Empty;
        NewGroupNameBox.Focus();
    }

    private void HideNewGroupPanel()
    {
        NewGroupPanel.Visibility = Visibility.Collapsed;
        NewGroupNameBox.Text = string.Empty;
    }

    private string ResolveSuggestedGroup(string fullPath) => ResolveSuggestedGroup(fullPath, _currentGroupName);

    private string ResolveSuggestedGroup(string fullPath, string currentGroupName)
    {
        if (!string.IsNullOrWhiteSpace(currentGroupName))
        {
            return currentGroupName;
        }

        var fileName = System.IO.Path.GetFileName(fullPath) ?? string.Empty;
        var extension = System.IO.Path.GetExtension(fullPath) ?? string.Empty;
        var hint = (fullPath ?? string.Empty).ToLowerInvariant();

        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || hint.Contains("script")
            || hint.Contains("jenkins")
            || hint.Contains("cleanup")
            || hint.Contains("publish"))
        {
            return "运维脚本";
        }

        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return "目录快捷方式";
        }

        if (hint.Contains("git")
            || hint.Contains("code")
            || hint.Contains("studio")
            || hint.Contains("clash")
            || hint.Contains("tool")
            || hint.Contains("sdk")
            || hint.Contains("proxy")
            || fileName.IndexOf("dev", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "开发工具";
        }

        return DefaultGroupName;
    }

    private void UpdateKeyboardHint()
    {
        KeyboardHintText.Text = _canUseIntegratedFileSearch
            ? "Esc 隐藏 · Enter 启动 · Ctrl+Enter 打开目录 · Ctrl+T 打开终端 · F2 编辑 · Delete 删除 · Ctrl+C 复制路径"
            : "Esc 隐藏 · Enter 启动 · Ctrl+Enter 打开目录 · Ctrl+T 打开终端 · F2 编辑 · Delete 删除 · Ctrl+C 复制路径 · 当前未启用文件搜索联动";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FocusSearchBoxAndSelectAll();
        if (_hasInitialized)
        {
            return;
        }

        _hasInitialized = true;
        if (_canUseIntegratedFileSearch)
        {
            StatusText.Text = "正在连接共享索引宿主，请稍候。";
            _ = WaitForIndexReadyAsync(forceRescan: false);
        }
        else
        {
            StatusText.Text = "就绪，可筛选当前工作台条目；文件搜索联动当前未启用。";
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_allowProcessExit || !_hideInsteadOfClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _debounceTimer.Stop();
        _liveRefreshTimer.Stop();
        CancelActiveSearch();
        CancelIndexing();
        _indexService.IndexChanged -= IndexService_IndexChanged;
        _indexService.IndexStatusChanged -= IndexService_IndexStatusChanged;
        _indexService.Shutdown();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (NewGroupPanel.Visibility == Visibility.Visible && NewGroupNameBox.IsKeyboardFocusWithin)
            {
                HideNewGroupPanel();
            }
            else
            {
                Close();
            }

            e.Handled = true;
            return;
        }

        if (_selectedItem != null
            && e.Key == Key.Delete
            && Keyboard.Modifiers == ModifierKeys.None)
        {
            RemoveItem(_selectedItem);
            e.Handled = true;
            return;
        }

        if (CandidateList.IsKeyboardFocusWithin && HandleCandidateShortcut(e))
        {
            e.Handled = true;
            return;
        }

        if (SearchBox.IsKeyboardFocusWithin || IsTextEditingControl())
        {
            // 搜索框有焦点时，Enter 启动选中项，方向键切换选中项
            if (_selectedItem != null && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                LaunchItem(_selectedItem);
                e.Handled = true;
            }
            else if (_selectedItem != null && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                LaunchItem(_selectedItem, forceNewInstance: true);
                e.Handled = true;
            }
            else if (_selectedItem != null && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenItemFolder(_selectedItem.FullPath);
                e.Handled = true;
            }
            else if (_selectedItem != null && e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenItemTerminal(_selectedItem.FullPath);
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.Up)
            {
                NavigateSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
            }
            else if (!SearchBox.IsKeyboardFocusWithin)
            {
                // 其他文本编辑控件，不拦截
            }
            return;
        }

        if (_selectedItem == null)
        {
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            LaunchItem(_selectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            LaunchItem(_selectedItem, forceNewInstance: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenItemFolder(_selectedItem.FullPath);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenItemTerminal(_selectedItem.FullPath);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2)
        {
            EditItem(_selectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            RemoveItem(_selectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyPath(_selectedItem.FullPath);
            e.Handled = true;
        }
    }

    private bool HandleCandidateShortcut(KeyEventArgs e)
    {
        if (CandidateList.SelectedItem is not ScanResultItem candidate)
        {
            return false;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            AddItem(candidate.FullPath, candidate.SuggestedGroupName, editBeforeSave: false);
            return true;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenItemFolder(candidate.FullPath);
            return true;
        }

        if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenItemTerminal(candidate.FullPath);
            return true;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyPath(candidate.FullPath);
            return true;
        }

        return false;
    }

    private bool IsTextEditingControl()
    {
        return Keyboard.FocusedElement is TextBoxBase
               || Keyboard.FocusedElement is TextBox
               || Keyboard.FocusedElement is ComboBox;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_canUseIntegratedFileSearch)
        {
            return;
        }

        _debounceTimer.Stop();
        Interlocked.Increment(ref _workbenchRefreshVersion);
        ApplyPendingSearchKeyword(startFileSearch: false, refreshWorkbench: true);
        _ = StartScanAsync(forceRescan: false);
        e.Handled = true;
    }

    private void SearchArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var clickedTextBox = FindParent<TextBox>(source);
        if (ReferenceEquals(clickedTextBox, SearchBox) && SearchBox.IsKeyboardFocusWithin && e.ClickCount == 1)
        {
            return;
        }

        FocusSearchBoxAndSelectAll();
        e.Handled = true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounceTimer.Stop();
        CancelActiveSearch();
        SetScanningState(false);

        var keyword = GetSearchKeyword();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            if (!_canUseIntegratedFileSearch)
            {
                StatusText.Text = "就绪，可筛选当前工作台条目；文件搜索联动当前未启用。";
            }
        }

        ScheduleWorkbenchRefresh();
        _debounceTimer.Start();
    }

    private void DebounceTimer_Tick(object sender, EventArgs e)
    {
        _debounceTimer.Stop();
        ApplyPendingSearchKeyword(startFileSearch: _canUseIntegratedFileSearch, refreshWorkbench: false);
    }

    private void LiveRefreshTimer_Tick(object sender, EventArgs e)
    {
        _liveRefreshTimer.Stop();
        if (!IsLoaded || string.IsNullOrWhiteSpace(GetSearchKeyword()) || !_canUseIntegratedFileSearch)
        {
            return;
        }

        _ = StartScanAsync(forceRescan: false, preserveExistingResults: true, silentRefresh: true);
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_canUseIntegratedFileSearch)
        {
            StatusText.Text = "当前未启用文件搜索联动。";
            return;
        }

        _ = StartScanAsync(forceRescan: true);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _debounceTimer.Stop();
        Interlocked.Increment(ref _workbenchRefreshVersion);
        CancelActiveSearch();
        StatusText.Text = "检索已停止。";
        SetScanningState(false);
    }

    private void CancelActiveSearch()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
    }

    private void CancelIndexing()
    {
        lock (_indexGate)
        {
            _indexCts?.Cancel();
            _indexCts?.Dispose();
            _indexCts = null;
            _indexTask = null;
            _indexReady = false;
        }
    }

    private Task<int> EnsureIndexAsync(bool forceRescan)
    {
        lock (_indexGate)
        {
            if (!forceRescan && _indexReady && _indexTask is { IsCanceled: false, IsFaulted: false })
            {
                return _indexTask;
            }

            if (!forceRescan && _indexTask != null && !_indexTask.IsCompleted)
            {
                return _indexTask;
            }

            _indexCts?.Cancel();
            _indexCts?.Dispose();
            _indexCts = new CancellationTokenSource();
            _indexReady = false;

            var progress = new Progress<string>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    StatusText.Text = message;
                }
            });

            _indexTask = forceRescan
                ? _indexService.RebuildIndexAsync(progress, _indexCts.Token)
                : _indexService.BuildIndexAsync(progress, _indexCts.Token);

            return _indexTask;
        }
    }

    private async Task<bool> WaitForIndexReadyAsync(bool forceRescan)
    {
        try
        {
            var indexedCount = await EnsureIndexAsync(forceRescan).ConfigureAwait(true);
            _indexReady = true;
            if (string.IsNullOrWhiteSpace(GetSearchKeyword()))
            {
                StatusText.Text = $"共享索引已就绪，当前可用 {indexedCount} 个对象。";
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = forceRescan ? "共享索引重建已取消。" : "共享索引连接已取消。";
            return false;
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "[StartupSearch] Initialize shared index failed");
            StatusText.Text = $"共享索引不可用：{ex.Message}";
            return false;
        }
    }

    private async Task StartScanAsync(bool forceRescan, bool preserveExistingResults = false, bool silentRefresh = false)
    {
        if (!_canUseIntegratedFileSearch)
        {
            return;
        }

        var totalStopwatch = Stopwatch.StartNew();
        var stageStopwatch = Stopwatch.StartNew();
        long indexWaitMs = 0;
        long searchMs = 0;
        long applyMs = 0;
        var keyword = GetSearchKeyword();
        CancelActiveSearch();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var currentVersion = Interlocked.Increment(ref _searchVersion);

        if (!preserveExistingResults && (!string.IsNullOrWhiteSpace(keyword) || forceRescan))
        {
            _scanResults.Clear();
            RefreshCandidatePane();
            RefreshQueue();
            ScrollRightSidebarToTop();
        }

        if (!silentRefresh)
        {
            SetScanningState(true);
            StatusText.Text = forceRescan
                ? "正在重建启动项搜索索引…"
                : string.IsNullOrWhiteSpace(keyword)
                    ? "正在准备索引…"
                    : $"正在搜索“{keyword}”…";
        }

        try
        {
            var indexReady = await WaitForIndexReadyAsync(forceRescan).ConfigureAwait(true);
            indexWaitMs = stageStopwatch.ElapsedMilliseconds;
            if (!indexReady || ct.IsCancellationRequested || currentVersion != _searchVersion)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(keyword))
            {
                StatusText.Text = $"已索引 {_indexService.IndexedCount} 个对象，输入关键词可继续查找候选。";
                return;
            }

            var queryProgress = new Progress<string>(message =>
            {
                if (!silentRefresh && !string.IsNullOrWhiteSpace(message) && currentVersion == _searchVersion && !ct.IsCancellationRequested)
                {
                    StatusText.Text = message;
                }
            });

            stageStopwatch.Restart();
            var searchResult = await SearchStartupResultsAsync(keyword, queryProgress, ct).ConfigureAwait(true);
            searchMs = stageStopwatch.ElapsedMilliseconds;
            if (ct.IsCancellationRequested || currentVersion != _searchVersion)
            {
                return;
            }

            stageStopwatch.Restart();
            ApplySearchResult(searchResult.Response, searchResult.Results);
            applyMs = stageStopwatch.ElapsedMilliseconds;
        }
        catch (OperationCanceledException)
        {
            if (currentVersion == _searchVersion)
            {
                StatusText.Text = "检索已停止。";
            }
        }
        catch (Exception ex)
        {
            if (currentVersion == _searchVersion)
            {
                LoggingService.LogError(ex, $"[StartupSearch] StartScanAsync failed: {ex.Message}");
                StatusText.Text = $"检索出错：{ex.Message}";
            }
        }
        finally
        {
            totalStopwatch.Stop();
            if (totalStopwatch.ElapsedMilliseconds >= 60 && !silentRefresh)
            {
                LoggingService.LogDebug(
                    $"[CtrlQ Search UI] keyword={keyword} totalMs={totalStopwatch.ElapsedMilliseconds} " +
                    $"indexWaitMs={indexWaitMs} searchMs={searchMs} applyMs={applyMs} " +
                    $"preserveExistingResults={preserveExistingResults} forceRescan={forceRescan}");
            }

            if (!silentRefresh && currentVersion == _searchVersion)
            {
                SetScanningState(false);
            }
        }
    }

    private void ApplySearchResult(SearchQueryResult response, IReadOnlyList<ScanResultItem> results)
    {
        ReconcileSearchResults(results);
        RefreshCandidateSuggestions();
        RefreshCandidatePane();
        RefreshQueue();
        ScrollRightSidebarToTop();

        if (results.Count == 0)
        {
            StatusText.Text = _indexService.IsBackgroundCatchUpInProgress
                ? $"未找到启动项候选（共 {response?.TotalIndexedCount ?? _indexService.IndexedCount} 个对象，后台追平中）"
                : $"未找到启动项候选（共 {response?.TotalIndexedCount ?? _indexService.IndexedCount} 个对象）";
            return;
        }

        if (_indexService.IsBackgroundCatchUpInProgress)
        {
            StatusText.Text = $"显示 {results.Count} 个候选（共 {response?.TotalMatchedCount ?? 0} 个对象，后台追平中）";
            return;
        }

        StatusText.Text = response?.IsTruncated == true
            ? $"显示 {results.Count} 个候选（共 {response.TotalMatchedCount} 个对象，输入更多字符可继续缩小）。"
            : $"{results.Count} 个候选（共 {response?.TotalMatchedCount ?? results.Count} 个对象）";
    }

    private async Task<StartupSearchResult> SearchStartupResultsAsync(string keyword, IProgress<string> progress, CancellationToken ct)
    {
        var response = await _indexService.SearchAsync(
            keyword,
            SearchPageSize,
            0,
            SearchTypeFilter.Launchable,
            progress,
            ct).ConfigureAwait(false);

        var currentGroupName = _currentGroupName;
        var startupResults = await Task.Run<IReadOnlyList<ScanResultItem>>(() =>
        {
            var results = new List<ScanResultItem>(MaxDisplayedResults);
            var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in response?.Results ?? Enumerable.Empty<ScannedFileInfo>())
            {
                ct.ThrowIfCancellationRequested();
                if (item == null || string.IsNullOrWhiteSpace(item.FullPath) || !dedup.Add(item.FullPath))
                    continue;

                results.Add(new ScanResultItem
                {
                    FileName = string.IsNullOrWhiteSpace(item.FileName) ? System.IO.Path.GetFileName(item.FullPath) : item.FileName,
                    FullPath = item.FullPath,
                    SuggestedGroupName = ResolveSuggestedGroup(item.FullPath, currentGroupName)
                });

                if (results.Count >= MaxDisplayedResults)
                    break;
            }

            return results;
        }, ct).ConfigureAwait(false);

        return new StartupSearchResult
        {
            Response = response ?? new SearchQueryResult
            {
                    TotalIndexedCount = _indexService.IndexedCount,
                TotalMatchedCount = 0,
                IsTruncated = false,
                Results = new List<ScannedFileInfo>()
            },
            Results = startupResults
        };
    }

    private void ReconcileSearchResults(IReadOnlyList<ScanResultItem> results)
    {
        var incoming = results ?? Array.Empty<ScanResultItem>();
        var incomingMap = incoming
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.FullPath))
            .ToDictionary(item => item.FullPath, StringComparer.OrdinalIgnoreCase);

        for (var i = _scanResults.Count - 1; i >= 0; i--)
        {
            var existing = _scanResults[i];
            if (existing == null || string.IsNullOrWhiteSpace(existing.FullPath))
            {
                _scanResults.RemoveAt(i);
                continue;
            }

            if (!incomingMap.TryGetValue(existing.FullPath, out var incomingItem))
            {
                _scanResults.RemoveAt(i);
                continue;
            }

            existing.FileName = incomingItem.FileName;
            existing.SuggestedGroupName = incomingItem.SuggestedGroupName;
        }

        var existingPaths = new HashSet<string>(_scanResults.Select(item => item.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FullPath) || !existingPaths.Add(item.FullPath))
            {
                continue;
            }

            _scanResults.Add(item);
        }
    }

    private void IndexService_IndexStatusChanged(object sender, IndexStatusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e == null)
            {
                return;
            }

            if (e.RequireSearchRefresh && _indexReady && !string.IsNullOrWhiteSpace(GetSearchKeyword()))
            {
                _ = StartScanAsync(forceRescan: false);
                return;
            }

            if (_indexReady && !string.IsNullOrWhiteSpace(GetSearchKeyword()) && e.IsBackgroundCatchUpInProgress)
            {
                ScheduleLiveRefresh();
            }

            if (string.IsNullOrWhiteSpace(GetSearchKeyword()) && !string.IsNullOrWhiteSpace(e.Message))
            {
                StatusText.Text = e.Message;
            }
        }));
    }

    private void IndexService_IndexChanged(object sender, IndexChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_indexReady || string.IsNullOrWhiteSpace(GetSearchKeyword()))
            {
                return;
            }

            ScheduleLiveRefresh();
        }));
    }

    private void ScheduleLiveRefresh()
    {
        _liveRefreshTimer.Stop();
        _liveRefreshTimer.Start();
    }

    private void SetScanningState(bool scanning)
    {
        StopButton.IsEnabled = scanning;
    }

    private void ViewList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigationSelection || ViewList.SelectedItem is not StartupNavigationItemVm selected)
        {
            return;
        }

        ApplyViewSelection(selected);
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigationSelection || GroupList.SelectedItem is not StartupNavigationItemVm selected)
        {
            return;
        }

        ApplyGroupSelection(selected);
    }

    private void ViewList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TryGetNavigationItemFromSource(e.OriginalSource, out var selected))
        {
            ViewList.SelectedItem = selected;
            ApplyViewSelection(selected);
            e.Handled = true;
        }
    }

    private void GroupList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TryGetNavigationItemFromSource(e.OriginalSource, out var selected))
        {
            GroupList.SelectedItem = selected;
            ApplyGroupSelection(selected);
            e.Handled = true;
        }
    }

    private void ApplyViewSelection(StartupNavigationItemVm selected)
    {
        if (selected == null)
        {
            return;
        }

        var nextView = ParseView(selected.Key);
        if (_currentView == nextView)
        {
            return;
        }

        InvalidateSearchRestoreContext();
        _currentView = nextView;
        RefreshWorkbench();
        ScrollWorkbenchToTop();
    }

    private void ApplyGroupSelection(StartupNavigationItemVm selected)
    {
        if (selected == null)
        {
            return;
        }

        var nextGroupName = selected.Key == AllGroupsKey ? string.Empty : selected.Key;
        if (string.Equals(_currentGroupName, nextGroupName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        InvalidateSearchRestoreContext();
        _currentGroupName = nextGroupName;
        RefreshWorkbench();
        ScrollWorkbenchToTop();
    }

    private void ScrollWorkbenchToTop()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ScrollWorkbenchToOffset(0d);
        }), DispatcherPriority.Background);
    }

    private void ScrollRightSidebarToTop()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RightSidebarScrollViewer?.ScrollToVerticalOffset(0);
        }), DispatcherPriority.Background);
    }

    private void AllViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentView == StartupViewKind.All)
            return;

        InvalidateSearchRestoreContext();
        _currentView = StartupViewKind.All;
        RefreshWorkbench();
    }

    private void RecentViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentView == StartupViewKind.Recent)
            return;

        InvalidateSearchRestoreContext();
        _currentView = StartupViewKind.Recent;
        RefreshWorkbench();
    }

    private void FavoriteViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentView == StartupViewKind.Favorites)
            return;

        InvalidateSearchRestoreContext();
        _currentView = StartupViewKind.Favorites;
        RefreshWorkbench();
    }

    private void BrokenViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentView == StartupViewKind.Broken)
            return;

        InvalidateSearchRestoreContext();
        _currentView = StartupViewKind.Broken;
        RefreshWorkbench();
    }

    private void ManualAddButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要添加的程序或脚本",
            Filter = "可执行文件|*.exe;*.bat;*.cmd;*.ps1;*.lnk|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            AddItem(dialog.FileName, _currentGroupName, editBeforeSave: true);
        }
    }

    private void ShowNewGroupPanelButton_Click(object sender, RoutedEventArgs e) => ShowNewGroupPanel();

    private void CreateGroupButton_Click(object sender, RoutedEventArgs e) => CreateNewGroup();

    private void CancelNewGroupButton_Click(object sender, RoutedEventArgs e) => HideNewGroupPanel();

    private void NewGroupNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CreateNewGroup();
            e.Handled = true;
        }
    }

    private void StartupCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<ButtonBase>(source) != null)
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is StartupItemVm item)
        {
            if (!ReferenceEquals(_selectedItem, item))
                InvalidateSearchRestoreContext();
            SelectStartupItem(item);
        }
    }

    private void StartupCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && FindParent<ButtonBase>(source) != null)
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is StartupItemVm item)
        {
            LaunchItem(item);
            e.Handled = true;
        }
    }

    private void LaunchItemButton_Click(object sender, RoutedEventArgs e) => LaunchItem(GetButtonTag<StartupItemVm>(sender));

    private void OpenFolderItemButton_Click(object sender, RoutedEventArgs e)
    {
        var item = GetButtonTag<StartupItemVm>(sender);
        OpenItemFolder(item?.FullPath);
    }

    private void EditItemButton_Click(object sender, RoutedEventArgs e) => EditItem(GetButtonTag<StartupItemVm>(sender));

    private void RemoveItemButton_Click(object sender, RoutedEventArgs e) => RemoveItem(GetButtonTag<StartupItemVm>(sender));

    private void ToggleFavoriteButton_Click(object sender, RoutedEventArgs e) => ToggleFavorite(GetButtonTag<StartupItemVm>(sender));

    private void CopyItemPathButton_Click(object sender, RoutedEventArgs e)
    {
        CopyPath(GetButtonTag<StartupItemVm>(sender)?.FullPath);
    }

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCandidatePane();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, UIntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, UIntPtr wParam, ref RECT lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    private void CandidateList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TryGetCandidateItemFromSource(e.OriginalSource, out var selected))
        {
            CandidateList.SelectedItem = selected;
            e.Handled = true;
        }
    }

    private void CandidateList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scanResults.Count == 0)
        {
            BubbleMouseWheelToScrollViewer(sender, e, RightSidebarScrollViewer);
            return;
        }

        if (sender is not DependencyObject dependencyObject)
        {
            e.Handled = true;
            return;
        }

        var childScrollViewer = FindVisualChild<ScrollViewer>(dependencyObject);
        if (childScrollViewer == null || childScrollViewer.ScrollableHeight <= 0)
        {
            e.Handled = true;
            return;
        }

        var nextOffset = childScrollViewer.VerticalOffset - e.Delta;
        nextOffset = Math.Max(0, Math.Min(childScrollViewer.ScrollableHeight, nextOffset));
        childScrollViewer.ScrollToVerticalOffset(nextOffset);
        e.Handled = true;
    }

    private void WorkbenchScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || _searchContextTrackingSuppressionCount > 0)
            return;

        if (_ignoredWorkbenchScrollChangeCount > 0)
        {
            _ignoredWorkbenchScrollChangeCount--;
            return;
        }

        InvalidateSearchRestoreContext();
    }

    private void WorkbenchScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (WorkbenchScrollViewer == null)
        {
            return;
        }

        var nextOffset = WorkbenchScrollViewer.VerticalOffset - e.Delta;
        nextOffset = Math.Max(0, Math.Min(WorkbenchScrollViewer.ScrollableHeight, nextOffset));

        if (Math.Abs(nextOffset - WorkbenchScrollViewer.VerticalOffset) > 0.1d)
        {
            WorkbenchScrollViewer.ScrollToVerticalOffset(nextOffset);
        }

        e.Handled = true;
    }

    private void LeftSidebarList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        BubbleMouseWheelToScrollViewer(sender, e, LeftSidebarScrollViewer);
    }

    private void ChildList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        BubbleMouseWheelToScrollViewer(sender, e, RightSidebarScrollViewer);
    }

    private void BubbleMouseWheelToScrollViewer(object sender, MouseWheelEventArgs e, ScrollViewer targetScrollViewer)
    {
        if (sender is not DependencyObject dependencyObject)
        {
            return;
        }

        var childScrollViewer = FindVisualChild<ScrollViewer>(dependencyObject);
        var shouldBubbleToPage = childScrollViewer == null
                                 || childScrollViewer.ScrollableHeight <= 0
                                 || (e.Delta > 0 && childScrollViewer.VerticalOffset <= 0)
                                 || (e.Delta < 0 && childScrollViewer.VerticalOffset >= childScrollViewer.ScrollableHeight);

        if (!shouldBubbleToPage)
        {
            return;
        }

        if (targetScrollViewer == null)
        {
            return;
        }

        var routedEvent = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender
        };

        targetScrollViewer.RaiseEvent(routedEvent);
        e.Handled = true;
    }

    private void CandidateList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CandidateList.SelectedItem is ScanResultItem candidate)
        {
            AddItem(candidate.FullPath, candidate.SuggestedGroupName, editBeforeSave: false);
        }
    }

    private void AddCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is ScanResultItem candidate)
        {
            AddItem(candidate.FullPath, candidate.SuggestedGroupName, editBeforeSave: false);
        }
    }

    private void OpenCandidateFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is ScanResultItem candidate)
        {
            OpenItemFolder(candidate.FullPath);
        }
    }

    private void CopyCandidatePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is ScanResultItem candidate)
        {
            CopyPath(candidate.FullPath);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private string GetSearchKeyword() => SearchBox.Text?.Trim() ?? string.Empty;

    private void ScheduleWorkbenchRefresh()
    {
        Interlocked.Increment(ref _workbenchRefreshVersion);
        ApplyPendingSearchKeyword(startFileSearch: false, refreshWorkbench: true);
    }

    private void ApplyPendingSearchKeyword(bool startFileSearch, bool refreshWorkbench)
    {
        var keyword = GetSearchKeyword();
        var hasKeyword = !string.IsNullOrWhiteSpace(keyword);

        if (refreshWorkbench && hasKeyword && !_wasSearchKeywordActive)
        {
            CaptureSearchRestoreContext();
        }

        if (refreshWorkbench && hasKeyword)
        {
            if (!string.IsNullOrWhiteSpace(_currentGroupName))
                _currentGroupName = string.Empty;

            SuppressSearchContextTrackingUntilLayoutSettled(() => RefreshWorkbench(refreshExpensivePanels: false, refreshSearchPanels: false));
            ScrollWorkbenchToTop();
        }
        else if (refreshWorkbench && _wasSearchKeywordActive)
        {
            var restored = TryRestoreSearchContext();
            if (!restored)
                ClearSearchRestoreContext();

            SuppressSearchContextTrackingUntilLayoutSettled(() => RefreshWorkbench(refreshExpensivePanels: false, refreshSearchPanels: false));

            if (!restored)
                ScrollWorkbenchToTop();
        }

        if (refreshWorkbench)
        {
            _wasSearchKeywordActive = hasKeyword;
        }

        if (!_canUseIntegratedFileSearch)
        {
            StatusText.Text = string.IsNullOrWhiteSpace(keyword)
                ? "就绪，可筛选当前工作台条目；文件搜索联动当前未启用。"
                : $"已筛选工作台条目：{keyword}";
            return;
        }

        if (string.IsNullOrWhiteSpace(keyword))
        {
            CancelActiveSearch();
            _scanResults.Clear();
            RefreshCandidatePane();
            RefreshQueue();
            ScrollRightSidebarToTop();
            StatusText.Text = _indexReady
                ? $"已索引 {_indexService.IndexedCount} 个对象，输入关键词可继续查找候选。"
                : "正在建立 MFT 索引，请稍候。";
            SetScanningState(false);
            return;
        }

        if (startFileSearch)
        {
            _ = StartScanAsync(forceRescan: false);
        }
    }

    private void CaptureSearchRestoreContext()
    {
        _searchRestoreContext = new WorkbenchSearchContext
        {
            View = _currentView,
            GroupName = _currentGroupName,
            SelectedItemPath = _selectedItem?.FullPath ?? string.Empty,
            VerticalOffset = WorkbenchScrollViewer?.VerticalOffset ?? 0d,
            CanRestore = true
        };
    }

    private void ClearSearchRestoreContext()
    {
        _searchRestoreContext = null;
    }

    private void InvalidateSearchRestoreContext()
    {
        if (!_wasSearchKeywordActive || _searchContextTrackingSuppressionCount > 0 || _searchRestoreContext == null)
            return;

        _searchRestoreContext.CanRestore = false;
    }

    private bool TryRestoreSearchContext()
    {
        var context = _searchRestoreContext;
        if (context == null)
            return false;

        ClearSearchRestoreContext();
        if (!context.CanRestore)
            return false;

        SuppressSearchContextTracking(delegate
        {
            _currentView = context.View;
            _currentGroupName = context.GroupName ?? string.Empty;
        });

        Dispatcher.BeginInvoke(new Action(delegate
        {
            SuppressSearchContextTracking(delegate
            {
                var selected = string.IsNullOrWhiteSpace(context.SelectedItemPath)
                    ? null
                    : _startupItems.FirstOrDefault(item => string.Equals(item.FullPath, context.SelectedItemPath, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                    SelectStartupItem(selected);

                var targetOffset = Math.Max(0, context.VerticalOffset);
                if (WorkbenchScrollViewer != null)
                    ScrollWorkbenchToOffset(Math.Min(targetOffset, WorkbenchScrollViewer.ScrollableHeight));
            });
        }), DispatcherPriority.Loaded);

        return true;
    }

    private void ScrollWorkbenchToOffset(double targetOffset)
    {
        if (WorkbenchScrollViewer == null)
            return;

        var boundedOffset = Math.Max(0d, Math.Min(targetOffset, WorkbenchScrollViewer.ScrollableHeight));
        if (Math.Abs(WorkbenchScrollViewer.VerticalOffset - boundedOffset) > 0.1d)
            _ignoredWorkbenchScrollChangeCount++;

        SuppressSearchContextTracking(() => WorkbenchScrollViewer.ScrollToVerticalOffset(boundedOffset));
    }

    private void SuppressSearchContextTracking(Action action)
    {
        _searchContextTrackingSuppressionCount++;
        try
        {
            action?.Invoke();
        }
        finally
        {
            _searchContextTrackingSuppressionCount--;
        }
    }

    private void SuppressSearchContextTrackingUntilLayoutSettled(Action action)
    {
        _searchContextTrackingSuppressionCount++;
        try
        {
            action?.Invoke();
        }
        finally
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_searchContextTrackingSuppressionCount > 0)
                    _searchContextTrackingSuppressionCount--;
            }), DispatcherPriority.Loaded);
        }
    }

    private bool MatchesCurrentView(StartupItemVm item)
    {
        return _currentView switch
        {
            StartupViewKind.All => true,
            StartupViewKind.Recent => IsRecentItem(item),
            StartupViewKind.Favorites => item.IsFavorite,
            StartupViewKind.Broken => item.IsBroken,
            _ => true
        };
    }

    private bool MatchesCurrentGroup(StartupItemVm item)
    {
        return string.IsNullOrWhiteSpace(_currentGroupName)
               || string.Equals(item.GroupName, _currentGroupName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesKeyword(StartupItemVm item, string keyword, PinyinMatcher pinyinMatcher)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        if (Contains(item.Name, keyword)
               || Contains(item.FullPath, keyword)
               || Contains(item.Note, keyword)
               || Contains(item.Arguments, keyword)
               || Contains(item.GroupName, keyword))
            return true;

        if (!ContainsChinese(keyword))
            return pinyinMatcher.IsMatch(item, keyword);

        return false;
    }

    private static bool ContainsChinese(string s)
    {
        foreach (char c in s)
            if (c >= '\u4E00' && c <= '\u9FFF') return true;
        return false;
    }

    private static bool Contains(string source, string keyword)
    {
        return !string.IsNullOrWhiteSpace(source)
               && source.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsRecentItem(StartupItemVm item)
    {
        return item?.LastLaunchedAt >= DateTime.Now.AddDays(-RecentDays);
    }

    private void UpdateFilterButtons()
    {
        ApplyFilterButtonStyle(AllViewButton, _currentView == StartupViewKind.All);
        ApplyFilterButtonStyle(RecentViewButton, _currentView == StartupViewKind.Recent);
        ApplyFilterButtonStyle(FavoriteViewButton, _currentView == StartupViewKind.Favorites);
        ApplyFilterButtonStyle(BrokenViewButton, _currentView == StartupViewKind.Broken);
    }

    private void ApplyFilterButtonStyle(Button button, bool isActive)
    {
        button.Background = isActive ? CreateBrush(0xDC, 0xEF, 0xE8) : Brushes.White;
        button.BorderBrush = isActive ? CreateBrush(0xBF, 0xD8, 0xCF) : CreateBrush(0xE4, 0xE8, 0xE4);
        button.Foreground = isActive ? CreateBrush(0x1A, 0x6A, 0x5F) : CreateBrush(0x1D, 0x26, 0x20);
    }

    private StartupNavigationItemVm CreateViewNavigationItem(StartupViewKind view, string title, int count)
    {
        var (background, foreground) = view switch
        {
            StartupViewKind.Favorites => (CreateBrush(0xF5, 0xEB, 0xC8), CreateBrush(0x8B, 0x6A, 0x1B)),
            StartupViewKind.Broken => (CreateBrush(0xF6, 0xDA, 0xDA), CreateBrush(0xA1, 0x48, 0x48)),
            StartupViewKind.Recent => (CreateBrush(0xDC, 0xEF, 0xE8), CreateBrush(0x1A, 0x6A, 0x5F)),
            _ => (CreateBrush(0xED, 0xF1, 0xEE), CreateBrush(0x56, 0x62, 0x5B))
        };

        return new StartupNavigationItemVm
        {
            Key = GetViewKey(view),
            Title = title,
            Count = count,
            CountBackground = background,
            CountForeground = foreground
        };
    }

    private static string GetViewKey(StartupViewKind view) => view.ToString();

    private static StartupViewKind ParseView(string key)
    {
        return Enum.TryParse(key, out StartupViewKind view) ? view : StartupViewKind.All;
    }

    private int GetGroupOrder(string groupName)
    {
        return _groupDefinitions.FirstOrDefault(group =>
            string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase))?.Order ?? int.MaxValue;
    }

    private static T GetButtonTag<T>(object sender) where T : class
    {
        return (sender as Button)?.Tag as T;
    }

    private static T FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T matched)
            {
                return matched;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private static bool TryGetNavigationItemFromSource(object originalSource, out StartupNavigationItemVm item)
    {
        item = null;
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        var container = FindParent<ListBoxItem>(source);
        if (container?.DataContext is not StartupNavigationItemVm navigationItem)
        {
            return false;
        }

        item = navigationItem;
        return true;
    }

    private static bool TryGetCandidateItemFromSource(object originalSource, out ScanResultItem item)
    {
        item = null;
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        var container = FindParent<ListBoxItem>(source);
        if (container?.DataContext is not ScanResultItem candidateItem)
        {
            return false;
        }

        item = candidateItem;
        return true;
    }

    private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
        {
            return null;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T matched)
            {
                return matched;
            }

            var nested = FindVisualChild<T>(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private static bool CanUseIntegratedFileSearch()
    {
        // return Environment.UserName.Equals("AustinYanyh", StringComparison.OrdinalIgnoreCase)
        //        || Environment.UserName.Equals("AustinYan", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static string CompactMiddleText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || maxLength <= 0 || text.Length <= maxLength)
        {
            return text ?? string.Empty;
        }

        if (maxLength <= 3)
        {
            return text.Substring(0, maxLength);
        }

        var keepStart = Math.Max(12, maxLength / 3);
        var keepEnd = Math.Max(18, maxLength - keepStart - 3);
        if (keepStart + keepEnd + 3 >= text.Length)
        {
            return text;
        }

        return string.Concat(
            text.Substring(0, keepStart),
            "...",
            text.Substring(text.Length - keepEnd, keepEnd));
    }

    private sealed class StartupSearchResult
    {
        public SearchQueryResult Response { get; set; }

        public IReadOnlyList<ScanResultItem> Results { get; set; }
    }

    private sealed class WorkbenchSearchContext
    {
        public StartupViewKind View { get; set; }

        public string GroupName { get; set; }

        public string SelectedItemPath { get; set; }

        public double VerticalOffset { get; set; }

        public bool CanRestore { get; set; }
    }
}

public class ScanResultItem : INotifyPropertyChanged
{
    private string _fileName;
    private string _fullPath;
    private string _suggestedGroupName;

    public string InitialLetter => string.IsNullOrWhiteSpace(FileName) ? "?" : FileName.Trim()[0].ToString().ToUpperInvariant();
    public string DirectoryNameDisplay => GetDirectoryNameDisplay(FullPath);

    public string FileName
    {
        get => _fileName;
        set
        {
            if (string.Equals(_fileName, value, StringComparison.Ordinal))
            {
                return;
            }

            _fileName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InitialLetter)));
        }
    }

    public string FullPath
    {
        get => _fullPath;
        set
        {
            if (string.Equals(_fullPath, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _fullPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullPath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DirectoryNameDisplay)));
        }
    }

    public string SuggestedGroupName
    {
        get => _suggestedGroupName;
        set
        {
            if (string.Equals(_suggestedGroupName, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _suggestedGroupName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SuggestedGroupName)));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private static string GetDirectoryNameDisplay(string fullPath)
    {
        var directoryPath = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return "目录信息不可用";
        }

        var directoryName = System.IO.Path.GetFileName(directoryPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(directoryName) ? directoryPath : directoryName;
    }
}

public class StartupItemVm : INotifyPropertyChanged
{
    private const string FallbackGroupName = "项目入口";
    private static readonly SolidColorBrush DefaultCardBrush = new(Color.FromRgb(0x1A, 0x6A, 0x5F));
    private static readonly SolidColorBrush StatusNormalBackground = new(Color.FromRgb(0xED, 0xF1, 0xEE));
    private static readonly SolidColorBrush StatusNormalForeground = new(Color.FromRgb(0x56, 0x62, 0x5B));
    private static readonly SolidColorBrush StatusFavoriteBackground = new(Color.FromRgb(0xF5, 0xEB, 0xC8));
    private static readonly SolidColorBrush StatusFavoriteForeground = new(Color.FromRgb(0x8B, 0x6A, 0x1B));
    private static readonly SolidColorBrush StatusBrokenBackground = new(Color.FromRgb(0xF6, 0xDA, 0xDA));
    private static readonly SolidColorBrush StatusBrokenForeground = new(Color.FromRgb(0xA1, 0x48, 0x48));

    private string _name;
    private string _fullPath;
    private string _arguments;
    private string _note;
    private string _groupName = FallbackGroupName;
    private bool _isFavorite;
    private int _order;
    private DateTime? _lastLaunchedAt;
    private int _launchCount;
    private bool _isBroken;
    private bool _isSelected;
    private Brush _accentBrush = DefaultCardBrush;

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(InitialLetter));
        }
    }

    public string FullPath
    {
        get => _fullPath;
        set
        {
            _fullPath = value;
            OnPropertyChanged(nameof(FullPath));
            OnPropertyChanged(nameof(TypeLabel));
            OnPropertyChanged(nameof(PathCompactDisplay));
        }
    }

    public string Arguments
    {
        get => _arguments;
        set
        {
            _arguments = value;
            OnPropertyChanged(nameof(Arguments));
            OnPropertyChanged(nameof(ArgumentsSummaryText));
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            _note = value;
            OnPropertyChanged(nameof(Note));
            OnPropertyChanged(nameof(DescriptionText));
            OnPropertyChanged(nameof(NoteSummaryText));
        }
    }

    public string GroupName
    {
        get => _groupName;
        set
        {
            _groupName = string.IsNullOrWhiteSpace(value) ? FallbackGroupName : value.Trim();
            OnPropertyChanged(nameof(GroupName));
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            _isFavorite = value;
            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(FavoriteVisibility));
            OnPropertyChanged(nameof(FavoriteButtonText));
            OnPropertyChanged(nameof(FavoriteButtonCompactText));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusBackground));
            OnPropertyChanged(nameof(StatusForeground));
        }
    }

    public int Order
    {
        get => _order;
        set
        {
            _order = value;
            OnPropertyChanged(nameof(Order));
        }
    }

    public DateTime? LastLaunchedAt
    {
        get => _lastLaunchedAt;
        set
        {
            _lastLaunchedAt = value;
            OnPropertyChanged(nameof(LastLaunchedAt));
            OnPropertyChanged(nameof(LastLaunchDisplay));
            OnPropertyChanged(nameof(LastLaunchInlineText));
        }
    }

    public int LaunchCount
    {
        get => _launchCount;
        set
        {
            _launchCount = value;
            OnPropertyChanged(nameof(LaunchCount));
            OnPropertyChanged(nameof(LaunchCountDisplay));
            OnPropertyChanged(nameof(LaunchCountInlineText));
        }
    }

    public bool IsBroken
    {
        get => _isBroken;
        set
        {
            _isBroken = value;
            OnPropertyChanged(nameof(IsBroken));
            OnPropertyChanged(nameof(BrokenVisibility));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusBackground));
            OnPropertyChanged(nameof(StatusForeground));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public Brush AccentBrush
    {
        get => _accentBrush;
        set
        {
            _accentBrush = value ?? DefaultCardBrush;
            OnPropertyChanged(nameof(AccentBrush));
        }
    }

    public string InitialLetter => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[0].ToString().ToUpperInvariant();

    public string DescriptionText => string.IsNullOrWhiteSpace(Note) ? "未填写备注，可通过编辑补充该入口的上下文说明。" : Note;
    public string NoteSummaryText => BuildSummaryText(Note, "未填写备注");
    public string ArgumentsSummaryText => BuildSummaryText(Arguments, "无启动参数");

    public string PathCompactDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FullPath))
            {
                return "路径不可用";
            }

            var fileName = System.IO.Path.GetFileName(FullPath);
            var directoryPath = System.IO.Path.GetDirectoryName(FullPath);
            var directoryName = string.IsNullOrWhiteSpace(directoryPath)
                ? string.Empty
                : System.IO.Path.GetFileName(directoryPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

            return string.IsNullOrWhiteSpace(directoryName)
                ? FullPath
                : $"{directoryName}\\{fileName}";
        }
    }

    public string LastLaunchDisplay
    {
        get
        {
            if (!LastLaunchedAt.HasValue)
            {
                return "未启动";
            }

            var span = DateTime.Now - LastLaunchedAt.Value;
            if (span.TotalMinutes < 1)
            {
                return "刚刚";
            }

            if (span.TotalHours < 1)
            {
                return $"{Math.Max(1, (int)span.TotalMinutes)} 分钟前";
            }

            if (span.TotalDays < 1)
            {
                return $"{Math.Max(1, (int)span.TotalHours)} 小时前";
            }

            if (span.TotalDays < 7)
            {
                return $"{Math.Max(1, (int)span.TotalDays)} 天前";
            }

            return LastLaunchedAt.Value.ToString("yyyy-MM-dd");
        }
    }

    public string LaunchCountDisplay => LaunchCount <= 0 ? "未启动" : $"{LaunchCount} 次";
    public string LastLaunchInlineText => $"最近：{LastLaunchDisplay}";
    public string LaunchCountInlineText => $"累计：{LaunchCountDisplay}";

    public string TypeLabel
    {
        get
        {
            var extension = System.IO.Path.GetExtension(FullPath) ?? string.Empty;
            if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                return "脚本";
            }

            return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "快捷方式" : "程序";
        }
    }

    public string FavoriteButtonText => IsFavorite ? "取消收藏" : "收藏";

    public string FavoriteButtonCompactText => IsFavorite ? "取消" : "收藏";

    public string StatusLabel => IsBroken ? "异常" : IsFavorite ? "收藏" : "正常";

    public Brush StatusBackground => IsBroken ? StatusBrokenBackground : IsFavorite ? StatusFavoriteBackground : StatusNormalBackground;

    public Brush StatusForeground => IsBroken ? StatusBrokenForeground : IsFavorite ? StatusFavoriteForeground : StatusNormalForeground;

    public Visibility FavoriteVisibility => IsFavorite ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BrokenVisibility => IsBroken ? Visibility.Visible : Visibility.Collapsed;

    public StartupItemVm Clone()
    {
        return new StartupItemVm
        {
            Name = Name,
            FullPath = FullPath,
            Arguments = Arguments,
            Note = Note,
            GroupName = GroupName,
            IsFavorite = IsFavorite,
            Order = Order,
            LastLaunchedAt = LastLaunchedAt,
            LaunchCount = LaunchCount,
            IsBroken = IsBroken,
            AccentBrush = AccentBrush
        };
    }

    public CommonStartupItem ToModel()
    {
        return new CommonStartupItem
        {
            Name = Name,
            FullPath = FullPath,
            Arguments = Arguments ?? string.Empty,
            Note = Note ?? string.Empty,
            GroupName = GroupName ?? string.Empty,
            IsFavorite = IsFavorite,
            Order = Order,
            LastLaunchedAt = LastLaunchedAt,
            LaunchCount = LaunchCount
        };
    }

    public static StartupItemVm FromModel(CommonStartupItem item)
    {
        return new StartupItemVm
        {
            Name = item.Name,
            FullPath = item.FullPath,
            Arguments = item.Arguments,
            Note = item.Note,
            GroupName = string.IsNullOrWhiteSpace(item.GroupName) ? FallbackGroupName : item.GroupName,
            IsFavorite = item.IsFavorite,
            Order = item.Order,
            LastLaunchedAt = item.LastLaunchedAt,
            LaunchCount = item.LaunchCount
        };
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string BuildSummaryText(string source, string emptyText)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return emptyText;
        }

        var normalized = source.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length <= 68)
        {
            return normalized;
        }

        return normalized.Substring(0, 65) + "...";
    }
}

public class StartupNavigationItemVm
{
    public string Key { get; set; }

    public string Title { get; set; }

    public int Count { get; set; }

    public Brush CountBackground { get; set; }

    public Brush CountForeground { get; set; }
}

public class StartupActivityVm
{
    public string TimeText { get; set; }

    public string Summary { get; set; }
}

internal enum ExistingInstanceActivationResult
{
    NotFound,
    Activated,
    FoundWithoutWindow
}

internal readonly struct WindowActivationCandidate
{
    public static readonly WindowActivationCandidate Empty = new WindowActivationCandidate(IntPtr.Zero, int.MinValue, false, false, false, string.Empty, string.Empty);

    public WindowActivationCandidate(IntPtr handle, int score, bool preferAggressive, bool isVisible, bool isIconic, string title, string className)
    {
        Handle = handle;
        Score = score;
        PreferAggressive = preferAggressive;
        IsVisible = isVisible;
        IsIconic = isIconic;
        Title = title ?? string.Empty;
        ClassName = className ?? string.Empty;
    }

    public IntPtr Handle { get; }

    public int Score { get; }

    public bool PreferAggressive { get; }

    public bool IsVisible { get; }

    public bool IsIconic { get; }

    public string Title { get; }

    public string ClassName { get; }
}

internal readonly struct WeChatTrayWindowCandidate
{
    public WeChatTrayWindowCandidate(IntPtr handle, int processId, string className, string title)
    {
        Handle = handle;
        ProcessId = processId;
        ClassName = className ?? string.Empty;
        Title = title ?? string.Empty;
    }

    public IntPtr Handle { get; }

    public int ProcessId { get; }

    public string ClassName { get; }

    public string Title { get; }
}

internal sealed class ProcessListScope : IDisposable
{
    public ProcessListScope(List<Process> processes)
    {
        Processes = processes ?? new List<Process>();
    }

    public List<Process> Processes { get; }

    public void Dispose()
    {
        foreach (var process in Processes)
        {
            try
            {
                process.Dispose();
            }
            catch
            {
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NOTIFYICONIDENTIFIER
{
    public uint CbSize;
    public IntPtr HWnd;
    public uint UID;
    public Guid GuidItem;
}

internal class ShortcutTarget
{
    public string TargetPath { get; set; }

    public string Arguments { get; set; }

    public string WorkingDirectory { get; set; }
}

public enum StartupViewKind
{
    All,
    Recent,
    Favorites,
    Broken
}
