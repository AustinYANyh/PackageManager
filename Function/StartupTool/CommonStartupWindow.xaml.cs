using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private WorkbenchSearchContext _searchRestoreContext;

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

    private void LaunchItem(StartupItemVm item)
    {
        if (item == null)
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = item.FullPath,
                Arguments = item.Arguments ?? string.Empty,
                UseShellExecute = true
            };

            var workingDirectory = GetLaunchWorkingDirectory(item.FullPath);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            Process.Start(startInfo);

            item.LastLaunchedAt = DateTime.Now;
            item.LaunchCount++;
            SaveItems();
            SelectStartupItem(item);
            RefreshWorkbench();
            StatusText.Text = $"已启动：{item.Name}";
        }
        catch (Exception ex)
        {
            UpdateItemRuntimeState(item);
            RefreshWorkbench();
            MessageBox.Show($"启动失败：{ex.Message}", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            ? "Esc 隐藏 · Enter 启动 · Ctrl+Enter 打开目录 · F2 编辑 · Delete 删除 · Ctrl+C 复制路径"
            : "Esc 隐藏 · Enter 启动 · Ctrl+Enter 打开目录 · F2 编辑 · Delete 删除 · Ctrl+C 复制路径 · 当前未启用文件搜索联动";
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
            else if (_selectedItem != null && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenItemFolder(_selectedItem.FullPath);
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

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenItemFolder(_selectedItem.FullPath);
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

public enum StartupViewKind
{
    All,
    Recent,
    Favorites,
    Broken
}
